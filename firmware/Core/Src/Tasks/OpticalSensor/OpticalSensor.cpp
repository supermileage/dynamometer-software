#include <Tasks/OpticalSensor/OpticalSensor.hpp>
#include <Tasks/OpticalSensor/opticalsensor_main.h>
#include <Config/sysconfig.h>

extern size_t optical_encoder_circular_buffer_index_writer;
extern optical_encoder_output_data optical_encoder_circular_buffer[OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE];

extern size_t task_error_circular_buffer_index_writer;
extern task_error_data task_error_circular_buffer[TASK_ERROR_CIRCULAR_BUFFER_SIZE];

// Wraps of TIM4's 16-bit counter. Written only by the update ISR, read only by the task inside a
// critical section. TIM4_IRQn runs at priority 5, which taskENTER_CRITICAL()'s BASEPRI masks, so
// the task can read this and CNT as a consistent pair. It is never reset -- it is consumed as a
// difference, and unsigned wraparound handles its own overflow (encoder_math.h).
static volatile uint32_t counter_overflows = 0;

OpticalSensor::OpticalSensor(osMessageQueueId_t sessionControllerToOpticalSensorHandle) : 
		// this comes directly from circular_buffers.h and config.h
		_data_buffer_writer(optical_encoder_circular_buffer, &optical_encoder_circular_buffer_index_writer, OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE),
		_sessionControllerToOpticalSensorHandle(sessionControllerToOpticalSensorHandle),
		_timestampClockSpeedFreq(get_timestamp_scale()),
		_opticalEncoderEnabled(false)
{}

// Reads TIM4's running pulse total. Must be called with TIM4_IRQn masked (i.e. inside a critical
// section): CNT and counter_overflows only describe the same instant if the ISR cannot run between
// the two reads.
static uint32_t ReadPulseTotal()
{
    uint16_t counter = (uint16_t)__HAL_TIM_GET_COUNTER(opticalCounterTimer);
    bool overflowPending = __HAL_TIM_GET_FLAG(opticalCounterTimer, TIM_FLAG_UPDATE);

    if (overflowPending)
    {
        // The flag is set, so the wrap has already happened -- but it may have happened *after* the
        // read above, which would have caught a pre-wrap counter near 65535. Re-read now that the
        // wrap is known to be behind us, so the carry is paired with a post-wrap counter. Pairing
        // it with the stale one would invent a whole 65536 pulses.
        counter = (uint16_t)__HAL_TIM_GET_COUNTER(opticalCounterTimer);
    }

    return encoder_extended_count(counter_overflows, counter, overflowPending);
}

bool OpticalSensor::Init()
{
    // HAL_TIM_Base_Init() ends with an EGR.UG to latch the prescaler, which leaves the update flag
    // already set. Enabling the interrupt below would fire it at once and bank a wrap that never
    // happened -- 65536 phantom pulses in whichever window straddles it, i.e. one absurd velocity
    // spike shortly after boot. Clear it before arming.
    __HAL_TIM_CLEAR_FLAG(opticalCounterTimer, TIM_FLAG_UPDATE);

    // TIM4 is clocked by the encoder pin rather than by the CPU, so it free-runs for the life of
    // the program: counting costs nothing, and enabling/disabling the sensor stays a pure reporting
    // decision in Run() with no start/stop ordering to get wrong.
    return HAL_TIM_Base_Start_IT(opticalCounterTimer) == HAL_OK;
}

void OpticalSensor::Run(void)
{
    optical_encoder_output_data outputData;

    // Origin of the current window: the pulse total and the instant it was read. A window is the
    // difference against these, so until one reading exists there is nothing to divide by.
    uint32_t previousTotal = 0;
    uint32_t previousSampleTimestamp = 0;
    bool haveBaseline = false;

    // Ticks accumulated since the last window that actually saw a pulse, and how many windows that
    // is -- the two together decide how a silent shaft is reported.
    uint32_t ticksSinceLastCount = 0;
    uint32_t emptyWindows = 0;

    float prevAngularVelocity = 0.0f;

    while (1)
    {
        osDelay(sysconfig_get_u32(SYSCFG_OPTICAL_ENCODER_TASK_OSDELAY));
        // --- Get the latest enable/disable state ---
        GetLatestFromQueue(
            _sessionControllerToOpticalSensorHandle,
            &_opticalEncoderEnabled,
            sizeof(_opticalEncoderEnabled),
            _opticalEncoderEnabled ? 0 : osWaitForever
        );

        // Skip processing if the latest state says disabled. The baseline is dropped with it: TIM4
        // is clocked by the pin and keeps counting while disabled, so resuming against a stale
        // baseline would attribute a whole idle period's pulses to one window.
        if (!_opticalEncoderEnabled)
        {
            haveBaseline = false;
            continue;
        }

        // --- Sample the counter and the clock as one instant (see the note on the globals) ---
        taskENTER_CRITICAL();
        uint32_t pulseTotal = ReadPulseTotal();
        uint32_t now = get_timestamp();
        taskEXIT_CRITICAL();

        if (!haveBaseline)
        {
            // First window since the task started or was re-enabled: adopt this reading as the
            // origin. There is no interval behind it, so nothing is reported for it.
            previousTotal = pulseTotal;
            previousSampleTimestamp = now;
            haveBaseline = true;
            ticksSinceLastCount = 0u;
            emptyWindows = 0u;
            prevAngularVelocity = 0.0f;
            continue;
        }

        const uint32_t counts = encoder_count_delta(pulseTotal, previousTotal);
        // Time actually elapsed, not the osDelay that was asked for: the task's wake-ups jitter,
        // and the denominator being off is the speed being off. Unsigned subtraction is deliberate
        // -- it stays correct across TIM2's 32-bit wrap.
        const uint32_t deltaTicks = now - previousSampleTimestamp;
        const uint32_t apertures = sysconfig_get_u32(SYSCFG_NUM_APERTURES);

        float angularVelocity;

        if (counts > 0u)
        {
            angularVelocity = encoder_angular_velocity(counts, deltaTicks, apertures,
                                                       _timestampClockSpeedFreq);
            ticksSinceLastCount = 0u;
            emptyWindows = 0u;
        }
        else
        {
            ticksSinceLastCount += deltaTicks;
            emptyWindows++;

            if (emptyWindows >= OPTICAL_ENCODER_MAX_EMPTY_WINDOWS)
            {
                // Silent long enough that the bound has stopped saying anything useful. Call the
                // shaft stopped rather than letting an ever-shrinking ceiling pass for a
                // measurement. Zero is sticky: prevAngularVelocity below carries it forward.
                angularVelocity = 0.0f;
            }
            else
            {
                // No pulse this window, so the shaft has not covered another aperture: it is
                // turning slower than one aperture per the elapsed silence. Report that ceiling,
                // which decays on its own, but never faster than the last real measurement -- the
                // bound is only news when it is the lower of the two.
                const float bound = encoder_velocity_upper_bound(ticksSinceLastCount, apertures,
                                                                 _timestampClockSpeedFreq);
                angularVelocity = (bound < prevAngularVelocity) ? bound : prevAngularVelocity;
            }
        }

        outputData.timestamp = now;
        outputData.raw_value = counts;
        outputData.angular_velocity = angularVelocity;
        outputData.angular_acceleration = encoder_angular_acceleration(
            prevAngularVelocity, angularVelocity, deltaTicks, _timestampClockSpeedFreq);

        _data_buffer_writer.WriteElementAndIncrementIndex(outputData);

        previousTotal = pulseTotal;
        previousSampleTimestamp = now;
        prevAngularVelocity = angularVelocity;
    }
}

extern "C" void opticalsensor_overflow_interrupt()
{
    counter_overflows = counter_overflows + 1;
}

extern "C" void opticalsensor_main(osMessageQueueId_t sessionControllerToOpticalSensorHandle)
{
	OpticalSensor opticalsensor = OpticalSensor(sessionControllerToOpticalSensorHandle);

	if (!opticalsensor.Init())
	{
        osThreadSuspend(osThreadGetId());
	}


    opticalsensor.Run();

}


//uint32_t OpticalSensor::GetClockSpeed()
//{
//	uint32_t tim14_clk = HAL_RCC_GetPCLK1Freq();
//	/* If APB1 prescaler > 1, timer clock = PCLK1 * 2 */
//	if ((RCC->CFGR & RCC_CFGR_PPRE1) != RCC_CFGR_PPRE1_DIV1)
//	{
//	    tim14_clk *= 2;
//	}
//
//	return tim14_clk;
//}

