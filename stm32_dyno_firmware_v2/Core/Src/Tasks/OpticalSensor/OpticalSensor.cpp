#include <Tasks/OpticalSensor/OpticalSensor.hpp>
#include <Tasks/OpticalSensor/opticalsensor_main.h>

extern size_t optical_encoder_circular_buffer_index_writer;
extern optical_encoder_output_data optical_encoder_circular_buffer[OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE];

extern size_t task_error_circular_buffer_index_writer;
extern task_error_data task_error_circular_buffer[TASK_ERROR_CIRCULAR_BUFFER_SIZE];

static volatile uint32_t num_counts = 0;
static volatile uint32_t num_overflows = 0;
static volatile uint32_t timestamp = 0;

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
    uint32_t prevTimestamp = 0;
    float prevAngularVelocity = 0.0f;

    while (1)
    {
        osDelay(OPTICAL_ENCODER_TASK_OSDELAY);
        // --- Get the latest enable/disable state ---
        GetLatestFromQueue(
            _sessionControllerToOpticalSensorHandle,
            &_opticalEncoderEnabled,
            sizeof(_opticalEncoderEnabled),
            _opticalEncoderEnabled ? 0 : osWaitForever
        );

        // Skip processing if the latest state says disabled
        if (!_opticalEncoderEnabled)
        {
        	continue;
        }

        // --- Copy critical data to avoid race conditions ---
        taskENTER_CRITICAL();
        uint32_t num_counts_copy = num_counts;
        num_counts = 0;
        uint32_t timestampCopy = get_timestamp();
        taskEXIT_CRITICAL();

        outputData.timestamp = timestampCopy;
        outputData.raw_value = num_counts_copy; 
        outputData.angular_velocity = CalculateAngularVelocity(num_counts_copy, prevTimestamp, timestampCopy);
        outputData.angular_acceleration = CalculateAngularAcceleration(prevAngularVelocity, outputData.angular_velocity, prevTimestamp, timestampCopy);

        _data_buffer_writer.WriteElementAndIncrementIndex(outputData);

        prevTimestamp = timestampCopy;
        prevAngularVelocity = outputData.angular_velocity;

    }
}



float OpticalSensor::CalculateRPM(uint32_t numCounts, uint32_t prevTimestamp, uint32_t currTimestamp)
{
    // Return 0 immediately if no pulse was detected
    if (numCounts == 0 || prevTimestamp == currTimestamp) {
        return 0.0f;
    }

    float deltaTimeSec = static_cast<float>(currTimestamp - prevTimestamp) / _timestampClockSpeedFreq;

    // RPM = revolutions per minute
    float rpm = (static_cast<float>(numCounts) / NUM_APERTURES / deltaTimeSec) * 60.0f;

    return rpm;
}

float OpticalSensor::CalculateAngularVelocity(uint32_t numCounts, uint32_t prevTimestamp, uint32_t currTimestamp)
{
    // Return 0 immediately if no pulse was detected
    if (numCounts == 0 || prevTimestamp == currTimestamp) {
        return 0.0f;
    }
    
    // Time difference in seconds
    float deltaTimeSec = static_cast<float>(currTimestamp - prevTimestamp) / _timestampClockSpeedFreq;

    // Angular velocity in radians per second
    float angularVelocity = (static_cast<float>(numCounts) / NUM_APERTURES) * (2.0f * M_PI) / deltaTimeSec;

    return angularVelocity;
}

float OpticalSensor::CalculateAngularAcceleration(float prevAngularVelocity, float currAngularVelocity, uint32_t prevTimestamp, uint32_t currTimestamp)
{
    // Return 0 if no time has passed
    if (prevTimestamp == currTimestamp) {
        return 0.0f;
    }

    // Time difference in seconds
    float deltaTimeSec = static_cast<float>(currTimestamp - prevTimestamp) / _timestampClockSpeedFreq;

    // Angular acceleration in radians per second squared
    float angularAcceleration = (currAngularVelocity - prevAngularVelocity) / deltaTimeSec;

    return angularAcceleration;
}

extern "C" void opticalsensor_input_interrupt()
{
    num_counts = num_counts + 1;
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

