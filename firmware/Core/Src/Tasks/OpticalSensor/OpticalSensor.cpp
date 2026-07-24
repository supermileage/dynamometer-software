#include <Tasks/OpticalSensor/OpticalSensor.hpp>
#include <Tasks/OpticalSensor/opticalsensor_main.h>
#include <Config/sysconfig.h>

extern size_t optical_encoder_circular_buffer_index_writer;
extern optical_encoder_output_data optical_encoder_circular_buffer[OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE];

extern size_t task_error_circular_buffer_index_writer;
extern task_error_data task_error_circular_buffer[TASK_ERROR_CIRCULAR_BUFFER_SIZE];

// Written only by the encoder ISR, read only by the task inside a critical section. EXTI9_5 runs
// at priority 5, which taskENTER_CRITICAL()'s BASEPRI masks, so the task reads the pair atomically
// and no pulse is lost between reading the count and clearing it.
static volatile uint32_t num_counts = 0;
static volatile uint32_t last_pulse_timestamp = 0;

OpticalSensor::OpticalSensor(osMessageQueueId_t sessionControllerToOpticalSensorHandle) : 
		// this comes directly from circular_buffers.h and config.h
		_data_buffer_writer(optical_encoder_circular_buffer, &optical_encoder_circular_buffer_index_writer, OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE),
		_sessionControllerToOpticalSensorHandle(sessionControllerToOpticalSensorHandle),
		_timestampClockSpeedFreq(get_timestamp_scale()),
		_opticalEncoderEnabled(false)
{}

bool OpticalSensor::Init()
{
    return true;
}

void OpticalSensor::Run(void)
{
    optical_encoder_output_data outputData;

    // The reference edge: the pulse that ended the previous measurement interval. Until a first
    // pulse has been seen there is nothing to measure against -- the old code used 0 here, so the
    // first sample was divided by the entire time since boot (TIM2 free-runs), which is a wrong
    // answer of unpredictable size and, after a counter wrap, a meaningless one.
    uint32_t referenceTimestamp = 0;
    bool haveReference = false;

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

        // Skip processing if the latest state says disabled. The reference is dropped with it: the
        // ISR keeps counting while disabled, so resuming against a stale edge would attribute a
        // whole idle period's pulses to one interval.
        if (!_opticalEncoderEnabled)
        {
            haveReference = false;
            continue;
        }

        // --- Copy the ISR's state atomically (see the note on the globals) ---
        taskENTER_CRITICAL();
        uint32_t num_counts_copy = num_counts;
        num_counts = 0;
        uint32_t lastPulseCopy = last_pulse_timestamp;
        uint32_t now = get_timestamp();
        taskEXIT_CRITICAL();

        float angularVelocity;
        uint32_t sampleTimestamp;
        uint32_t deltaTicks;

        if (num_counts_copy > 0u && haveReference)
        {
            // Both ends of this interval are real pulse edges, so the angle is exactly
            // num_counts_copy apertures and the only error is the timestamps' 1 us resolution.
            // Unsigned subtraction is deliberate: it stays correct across a counter wrap.
            deltaTicks = lastPulseCopy - referenceTimestamp;
            angularVelocity = encoder_angular_velocity(
                num_counts_copy, deltaTicks, sysconfig_get_u32(SYSCFG_NUM_APERTURES),
                _timestampClockSpeedFreq);
            sampleTimestamp = lastPulseCopy;
        }
        else if (num_counts_copy > 0u)
        {
            // First pulses since the task started or was re-enabled: adopt this edge as the
            // reference and report nothing derived from it yet.
            deltaTicks = 0u;
            angularVelocity = 0.0f;
            sampleTimestamp = lastPulseCopy;
        }
        else
        {
            // No pulse this pass. The shaft has not covered another aperture, so its speed is
            // below one aperture per the elapsed silence -- a bound that decays toward zero on
            // its own rather than snapping there. Never report faster than the last known speed.
            deltaTicks = haveReference ? (now - referenceTimestamp) : 0u;
            float bound = encoder_velocity_upper_bound(
                deltaTicks, sysconfig_get_u32(SYSCFG_NUM_APERTURES), _timestampClockSpeedFreq);
            angularVelocity = (haveReference && bound < prevAngularVelocity)
                                  ? bound
                                  : (haveReference ? prevAngularVelocity : 0.0f);
            sampleTimestamp = now;
        }

        outputData.timestamp = sampleTimestamp;
        outputData.raw_value = num_counts_copy;
        outputData.angular_velocity = angularVelocity;
        outputData.angular_acceleration = haveReference
            ? encoder_angular_acceleration(prevAngularVelocity, angularVelocity, deltaTicks,
                                           _timestampClockSpeedFreq)
            : 0.0f;

        _data_buffer_writer.WriteElementAndIncrementIndex(outputData);

        if (num_counts_copy > 0u)
        {
            referenceTimestamp = lastPulseCopy;
            haveReference = true;
        }
        prevAngularVelocity = angularVelocity;
    }
}

extern "C" void opticalsensor_input_interrupt()
{
    num_counts = num_counts + 1;
    // Stamping the edge here is what makes the measurement interval start and end on real pulses
    // rather than on whenever the task happened to run -- see encoder_math.h.
    last_pulse_timestamp = get_timestamp();
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

