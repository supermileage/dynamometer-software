#ifndef INC_TASKS_FORCESENSOR_ADC_FORCESENSOR_ADC_HPP_
#define INC_TASKS_FORCESENSOR_ADC_FORCESENSOR_ADC_HPP_

#include "main.h"
#include "cmsis_os2.h"



#include "TimeKeeping/timestamps.h"

#include "MessagePassing/osqueue_helpers.h"
#include "MessagePassing/messages_private.h"

#include "CircularBufferWriter.hpp"

#ifdef __cplusplus
extern "C" {
#endif

class ForceSensorADC
{
	public:
		ForceSensorADC(osMessageQueueId_t sessionControllerToForceSensorHandle);
		~ForceSensorADC() = default;

		bool Init();
		void Run();

	private:
		float GetForce(uint16_t adcValue);

		// Circular Buffer for ForceSensor with template bpm_output_data
		CircularBufferWriter<forcesensor_output_data> _data_buffer_writer;
		CircularBufferWriter<task_error_data> _task_error_buffer_writer;

		osMessageQueueId_t _sessionControllerToForceSensorHandle;
		osMessageQueueId_t _forceSensorToSessionControllerHandle;

};

#ifdef __cplusplus
}
#endif

#endif /* INC_TASKS_FORCESENSOR_ADC_FORCESENSOR_ADC_HPP_ */
