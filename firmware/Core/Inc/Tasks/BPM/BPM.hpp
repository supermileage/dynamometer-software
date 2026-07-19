#ifndef INC_TASKS_BPM_BPM_HPP_
#define INC_TASKS_BPM_BPM_HPP_

#include "main.h"
#include "cmsis_os2.h"



#include "TimeKeeping/timestamps.h"

#include "MessagePassing/osqueue_helpers.h"
#include "MessagePassing/messages_public.h"
#include "MessagePassing/messages_private.h"

#include "CircularBufferWriter.hpp"

#include "Config/config.h"


#ifdef __cplusplus
extern "C" {
#endif

extern TIM_HandleTypeDef* bpmTimer;

class BPM
{
	public:
		BPM(osMessageQueueId_t sessionControllerToBpmHandle, osMessageQueueId_t pidToBpmHandle);
		~BPM() = default;
		bool Init();
		void Run();

	private:
		float SetDutyCycle(float new_duty_cycle_percent);
		bool TogglePWM(bool enable);

		CircularBufferWriter<bpm_output_data> _data_buffer_writer;
		CircularBufferWriter<task_error_data> _task_error_buffer_writer;

		osMessageQueueId_t _fromSCHandle;
		osMessageQueueId_t _fromPIDHandle;

		bool _prevBpmCtrlEnabled;

};

#ifdef __cplusplus
}
#endif


#endif /* INC_TASKS_BPM_BPM_HPP_ */
