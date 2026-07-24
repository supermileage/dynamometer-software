#include <Tasks/PID/pid_main.h>
#include <Tasks/PID/PID.hpp>
#include <Config/sysconfig.h>

extern UART_HandleTypeDef huart1;

extern size_t task_error_circular_buffer_index_writer;
extern task_error_data task_error_circular_buffer[TASK_ERROR_CIRCULAR_BUFFER_SIZE];

extern size_t optical_encoder_circular_buffer_index_writer;
extern optical_encoder_output_data optical_encoder_circular_buffer[OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE];

PIDController::PIDController(osMessageQueueId_t sessionControllerToPidControllerHandle, osMessageQueueId_t pidControllerToSessionControllerAckHandle, osMessageQueueId_t pidToBpmHandle, bool initialState) : 
			_data_buffer_reader(optical_encoder_circular_buffer, &optical_encoder_circular_buffer_index_writer, OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE),			
            _task_error_buffer_writer(task_error_circular_buffer, &task_error_circular_buffer_index_writer, TASK_ERROR_CIRCULAR_BUFFER_SIZE),
            _sessionControllerToPidHandle(sessionControllerToPidControllerHandle),
			_pidControllerToSessionControllerAckHandle(pidControllerToSessionControllerAckHandle),
			_pidToBpmHandle(pidToBpmHandle),
			_enabled(initialState),
			_curTimestamp(0),
			_prevTimestamp(0),
			_curAngularVelocity(static_cast<float>(0)),
			_desiredAngularVelocity(static_cast<float>(0)),
			_prevError(static_cast<float>(0)),
			_error(static_cast<float>(0))
{}

bool PIDController::Init()
{
	Reset();
	return true;
}

static inline float Clamp(float value, float min, float max)
{
    if (value < min) return min;
    if (value > max) return max;
    return value;
}

void PIDController::Run()
{
    float integral = 0.0f;
    float derivative = 0.0f;
    uint32_t timeDelta;
    optical_encoder_output_data latestOpticalEncoderData;

    while (true)
    {
        // --- Check for instructions ---
        session_controller_to_pid_controller msg;
        bool gotInstruction = GetLatestFromQueue(
            _sessionControllerToPidHandle,
            &msg,
            sizeof(msg),
            _enabled ? 0 : osWaitForever  // poll if enabled, block if disabled
        );

        if (gotInstruction)
        {
            bool ack = true;
            osMessageQueuePut(_pidControllerToSessionControllerAckHandle, &ack, 0, osWaitForever);
            
            _enabled = msg.enable_status;
            _desiredAngularVelocity = msg.desired_angular_velocity;

            if (_enabled)
            {
                Reset();
                integral = 0.0f; // reset integral too
            }
        }

        if (!_enabled)
        {
            EmptyQueue(_pidToBpmHandle, sizeof(session_controller_to_bpm));
            osDelay(sysconfig_get_u32(SYSCFG_PID_TASK_OSDELAY));
            continue;
        }

        // --- Read latest optical encoder data ---
        if (!_data_buffer_reader.GetElementAndIncrementIndex(latestOpticalEncoderData))
        {
            // Nothing new, skip loop
            osDelay(sysconfig_get_u32(SYSCFG_PID_TASK_OSDELAY));
            continue;
        }

        // Drain buffer to get the latest
        while (_data_buffer_reader.GetElementAndIncrementIndex(latestOpticalEncoderData));

        // --- Update current values ---
        _curTimestamp = latestOpticalEncoderData.timestamp;
        _curAngularVelocity = latestOpticalEncoderData.angular_velocity;

        // Compute time delta safely
        timeDelta = GetTimeDelta();

        // --- PID calculations ---
        _error = static_cast<float>(_desiredAngularVelocity) - _curAngularVelocity;
        derivative = (_error - _prevError) / static_cast<float>(timeDelta);
        integral += _error * static_cast<float>(timeDelta);

        float pidOutput = sysconfig_get_float(SYSCFG_K_P) * _error
                          + sysconfig_get_float(SYSCFG_K_D) * derivative
                          + sysconfig_get_float(SYSCFG_K_I) * integral;

        // TODO: Figure out how to merge this single input duo output PID controller
        // pidOutput = Clamp(pidOutput / sysconfig_get_float(SYSCFG_PID_MAX_OUTPUT), -1.0f, 1.0f); // Normalize and clamp to [-1, 1]

        // // Linear, branchless mixing
        // float horizontalBias = sysconfig_get_float(SYSCFG_HORIZONTAL_BIAS);
        // float verticalBias = sysconfig_get_float(SYSCFG_VERTICAL_BIAS);
        // float throttleOutput =
        //     Clamp(sysconfig_get_float(SYSCFG_THROTTLE_GAIN) * (0.5f * (1.0f - (pidOutput-horizontalBias)) - verticalBias), 0.0f, 1.0f);
        // float brakeOutput =
        //     Clamp(sysconfig_get_float(SYSCFG_BRAKE_GAIN) * (0.5f * (1.0f + (pidOutput-horizontalBias)) - verticalBias), 0.0f, 1.0f);

        // PID Graph
        // https://www.desmos.com/calculator/s3sjvmcamd

        // --- Send to actuators ---
        // SendThrottleDutyCycle(throttleOutput);
        SendBrakeDutyCycle(pidOutput);

        // --- Update previous values ---
        _prevTimestamp = _curTimestamp;
        _prevError = _error;

        // --- Yield to other tasks ---
        osDelay(sysconfig_get_u32(SYSCFG_PID_TASK_OSDELAY));
    }
}






float PIDController::GetTimeDelta()
{
	float timeDelta;
	
	if (_curTimestamp > _prevTimestamp)
	{
		timeDelta = _curTimestamp - _prevTimestamp;
	}
	else if (_curTimestamp == _prevTimestamp)
	{
		timeDelta = 1;
	}
	else
	{
		timeDelta = (UINT32_MAX - _prevTimestamp) + _curTimestamp + 1;
	}

	return timeDelta;
}

void PIDController::Reset()
{
	_curTimestamp = 0;
	_prevTimestamp = 0;
	_curAngularVelocity = static_cast<float>(0);

	_error = static_cast<float>(0);
	_prevError = static_cast<float>(0);
}

void PIDController::SendThrottleDutyCycle(float new_duty_cycle_percent)
{
    // TODO (Send Throttle Duty Cycle to Motor Controller)
    

}

void PIDController::SendBrakeDutyCycle(float new_duty_cycle_percent)
{

	if (osMessageQueuePut(_pidToBpmHandle, &new_duty_cycle_percent, 0, 0) != osOK)
	{
		task_error_data error_data = PopulateTaskErrorDataStruct(
            get_timestamp(),
            TASK_OFFSET_PID_CONTROLLER,
            static_cast<uint32_t>(WARNING_PID_CONTROLLER_MESSAGE_QUEUE_FULL)
        );
        _task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
        osDelay(sysconfig_get_u32(SYSCFG_TASK_WARNING_RETRY_OSDELAY));
        return;
	}

}

extern "C" void pid_main(osMessageQueueId_t sessionControllerToPidControllerHandle, osMessageQueueId_t pidControllerToSessionControllerAckHandle, osMessageQueueId_t pidToBpmHandle, bool initialState) 
{
	PIDController controller = PIDController(sessionControllerToPidControllerHandle, pidControllerToSessionControllerAckHandle, pidToBpmHandle, initialState);

	if (!controller.Init())
	{
		 osThreadSuspend(osThreadGetId());;
	}


	controller.Run();
}






