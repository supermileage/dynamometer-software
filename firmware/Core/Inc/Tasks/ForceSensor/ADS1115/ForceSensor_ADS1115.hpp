#ifndef INC_TASKS_FORCESENSOR_ADS1115_FORCESENSOR_ADS1115_HPP_
#define INC_TASKS_FORCESENSOR_ADS1115_FORCESENSOR_ADS1115_HPP_

#include "main.h"
#include "cmsis_os.h"



#include "TimeKeeping/timestamps.h"

#include "MessagePassing/messages_private.h"
#include "CircularBufferWriter.hpp"

#include "ADS1115.hpp"

#ifdef __cplusplus
extern "C" {
#endif

class ForceSensorADS1115 
{
	public:
		ForceSensorADS1115(osMessageQueueId_t sessionControllerToForceSensorHandle,
		                   osMessageQueueId_t usbToForceSensorCommandHandle,
		                   osMessageQueueId_t taskCompletionHandle);
		~ForceSensorADS1115() = default;

		bool Init();
		void Run();

	private:
		float GetForce(uint16_t rawValue);

		// Drain queued USB setting commands, apply each, and post a completion ack
		// (with the real status) for any host-originated one (msg_id != 0).
		void ProcessCommands();
		uint32_t ApplyCommand(const usb_task_command& cmd);

		// Circular Buffer for ForceSensor with template bpm_output_data
		CircularBufferWriter<forcesensor_output_data> _data_buffer_writer;
		CircularBufferWriter<task_error_data> _task_error_buffer_writer;

		ADS1115 _ads1115;

		osMessageQueueId_t _sessionControllerToForceSensorHandle;
		osMessageQueueId_t _usbToForceSensorCommandHandle;
		osMessageQueueId_t _taskCompletionHandle;

		

};

#ifdef __cplusplus
}
#endif




#endif /* INC_TASKS_FORCESENSOR_ADS1115_FORCESENSOR_ADS1115_HPP_ */
