#include "Tasks/BPM/bpm_main.h"
#include "Tasks/BPM/BPM.hpp"
#include "Config/sysconfig.h"

extern size_t bpm_circular_buffer_index_writer;
extern bpm_output_data bpm_circular_buffer[BPM_CIRCULAR_BUFFER_SIZE];

extern size_t task_error_circular_buffer_index_writer;
extern task_error_data task_error_circular_buffer[TASK_ERROR_CIRCULAR_BUFFER_SIZE];

BPM::BPM(osMessageQueueId_t sessionControllerToBpmHandle, osMessageQueueId_t pidToBpmHandle)
    : 
	_data_buffer_writer(bpm_circular_buffer, &bpm_circular_buffer_index_writer, BPM_CIRCULAR_BUFFER_SIZE),
	_task_error_buffer_writer(task_error_circular_buffer, &task_error_circular_buffer_index_writer, TASK_ERROR_CIRCULAR_BUFFER_SIZE),
	  _fromSCHandle(sessionControllerToBpmHandle),
	  _fromPIDHandle(pidToBpmHandle), // Controls specific RPM
	  _prevBpmCtrlEnabled(false)
{}


bool BPM::Init()
{
	return true;
}


void BPM::Run(void)
{
	session_controller_to_bpm scMsg;
	bool readFromPID = false;

	while(1) {

		// do not want latest from queue since there may be specific instructions to address
		while (osMessageQueueGet(_fromSCHandle, &scMsg, NULL, osWaitForever) == osOK)
		{
			switch(scMsg.op)
			{
				case READ_FROM_PID:
					readFromPID = true;
					break;
				case START_PWM:
					SetDutyCycle(scMsg.new_duty_cycle_percent);
					if (!TogglePWM(true))
					{
						return;
					}
					readFromPID = false;
					break;
				case STOP_PWM:
					if (!TogglePWM(false))
					{
						return;
					}
					readFromPID = false;
					break;
				default:
					readFromPID = false;
					break;
			}


		}

		if (!readFromPID)
		{
			continue;
		}

		float latestDutyCycle;

		// Get the latest available value (non-blocking)
		if (!GetLatestFromQueue(_fromPIDHandle, &latestDutyCycle, sizeof(latestDutyCycle), 0))
		{
			continue;
		}

		SetDutyCycle(latestDutyCycle);
		if (!TogglePWM(true))
		{
			return;
		}
		bpm_output_data outputData;
		outputData.timestamp = get_timestamp();
		outputData.duty_cycle = latestDutyCycle;
		_data_buffer_writer.WriteElementAndIncrementIndex(outputData);

		osDelay(sysconfig_get_u32(SYSCFG_BPM_TASK_OSDELAY));
		
	

		

	}
}


bool BPM::TogglePWM(bool enable)
{
	// if master enables and BPM was previously disabled, then start PWM
	if (enable && !_prevBpmCtrlEnabled)
	{
		if (HAL_TIM_PWM_Start(bpmTimer, TIM_CHANNEL_1) != HAL_OK)
		{
			task_error_data error_data = PopulateTaskErrorDataStruct(
				get_timestamp(),
				TASK_OFFSET_BPM_CONTROLLER,
				static_cast<uint32_t>(ERROR_BPM_PWM_START_FAILURE)
			);

			_task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
			return false;
		}
	}
	// if master enables and BPM was previously enabled, then stop PWM
	else if (!enable && _prevBpmCtrlEnabled)
	{
		if (HAL_TIM_PWM_Stop(bpmTimer, TIM_CHANNEL_1) != HAL_OK)
		{
			task_error_data error_data = PopulateTaskErrorDataStruct(
				get_timestamp(),
				TASK_OFFSET_BPM_CONTROLLER,
				static_cast<uint32_t>(ERROR_BPM_PWM_STOP_FAILURE)
			);
			_task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
			return false;
		}
	}
	_prevBpmCtrlEnabled = enable;

	return true;
}


void BPM::SetDutyCycle(float new_duty_cycle_percent)
{

	// The two bounds are written independently over USB (the store range-checks each against
	// [0,1] but not against the other), so read them as an unordered pair: if a host update
	// left min > max, clamping to the lower/upper of the two keeps the duty cycle inside the
	// intended envelope instead of forcing a near-off request up to the inverted "min".
	float minDutyCycle = sysconfig_get_float(SYSCFG_MIN_DUTY_CYCLE_PERCENT);
	float maxDutyCycle = sysconfig_get_float(SYSCFG_MAX_DUTY_CYCLE_PERCENT);
	if (minDutyCycle > maxDutyCycle)
	{
		const float lower = maxDutyCycle;
		maxDutyCycle = minDutyCycle;
		minDutyCycle = lower;
	}
	if (new_duty_cycle_percent < minDutyCycle)
		new_duty_cycle_percent = minDutyCycle;
	else if (new_duty_cycle_percent > maxDutyCycle)
		new_duty_cycle_percent = maxDutyCycle;

	uint16_t new_duty_cycle = new_duty_cycle_percent * __HAL_TIM_GET_AUTORELOAD(bpmTimer);

	__HAL_TIM_SET_COMPARE(bpmTimer, TIM_CHANNEL_1, new_duty_cycle);
}

extern "C" void bpm_main(osMessageQueueId_t sessionControllerToBpmHandle, osMessageQueueId_t pidControllerToBpmHandle)
{
	BPM bpm = BPM(sessionControllerToBpmHandle, pidControllerToBpmHandle);

	if (!bpm.Init())
	{
		osThreadSuspend(osThreadGetId());
	}


	bpm.Run();
}
