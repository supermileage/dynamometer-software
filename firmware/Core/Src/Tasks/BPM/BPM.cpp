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
	bool readFromPID = false;
	bool pwmEnabled = false;
	float appliedDutyCycle = 0.0f;   // what the timer is really driving, after clamping

	while(1) {

		// 1. Drain every SessionController command waiting right now. This poll must not block:
		//    the task still has to follow the PID controller and emit telemetry on the passes
		//    where no command arrives, and an osWaitForever here parks it on the first quiet
		//    moment -- which is most of them, since manual mode only sends on a change.
		session_controller_to_bpm scMsg;
		while (osMessageQueueGet(_fromSCHandle, &scMsg, NULL, 0) == osOK)
		{
			switch(scMsg.op)
			{
				case READ_FROM_PID:
					// Duty cycle now comes from the PID queue below; hold the current
					// value until the controller produces its first one.
					readFromPID = true;
					break;
				case START_PWM:
					readFromPID = false;
					pwmEnabled = true;
					appliedDutyCycle = SetDutyCycle(scMsg.new_duty_cycle_percent);
					break;
				case STOP_PWM:
					readFromPID = false;
					pwmEnabled = false;
					appliedDutyCycle = 0.0f;
					break;
				default:
					readFromPID = false;
					break;
			}
		}

		// 2. In PID mode, follow the controller's latest request. Nothing queued just means it
		//    has not produced a new one since the last pass -- keep driving the current value.
		if (readFromPID)
		{
			float latestDutyCycle;
			if (GetLatestFromQueue(_fromPIDHandle, &latestDutyCycle, sizeof(latestDutyCycle), 0))
			{
				pwmEnabled = true;
				appliedDutyCycle = SetDutyCycle(latestDutyCycle);
			}
		}

		// 3. Reconcile the PWM output with the requested state. TogglePWM only touches the
		//    timer on a real edge, so calling it every pass costs a compare in steady state.
		if (!TogglePWM(pwmEnabled))
		{
			break;   // start/stop failed and was logged; do not keep driving blind
		}

		// 4. Record what is being driven -- every pass, not only when it changes. The host plots
		//    this as a time series, which needs a continuous line, and a change-only stream
		//    leaves the readout stale between adjustments (manual mode sends a command only when
		//    the value moves, so that path produced no telemetry at all).
		bpm_output_data outputData;
		outputData.timestamp = get_timestamp();
		outputData.duty_cycle = pwmEnabled ? appliedDutyCycle : 0.0f;
		outputData.raw_value = 0;   // padding in the wire struct; keep it deterministic
		_data_buffer_writer.WriteElementAndIncrementIndex(outputData);

		osDelay(sysconfig_get_u32(SYSCFG_BPM_TASK_OSDELAY));
	}

	// A FreeRTOS thread function must never return: falling out of Run() would fall off the end
	// of bpm_main() too. Park the task instead, leaving the error it just logged to be streamed.
	osThreadSuspend(osThreadGetId());
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


// Returns the duty cycle actually programmed into the timer, which is the requested value
// clamped into the configured envelope -- telemetry reports what is driven, not what was asked.
float BPM::SetDutyCycle(float new_duty_cycle_percent)
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

	return new_duty_cycle_percent;
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
