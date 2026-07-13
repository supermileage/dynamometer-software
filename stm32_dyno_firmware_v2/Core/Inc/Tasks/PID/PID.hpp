#ifndef INC_TASKS_PID_PID_HPP_
#define INC_TASKS_PID_PID_HPP_

#include "main.h"

#include "cmsis_os2.h"

#include "MessagePassing/osqueue_helpers.h"
#include "MessagePassing/messages_private.h"
#include "MessagePassing/messages_public.h"

#include "CircularBufferReader.hpp"
#include "CircularBufferWriter.hpp"

#include "TimeKeeping/timestamps.h"

#include "Config/config.h"

#include <stdint.h>

class PIDController
{
	public:
		PIDController(osMessageQueueId_t sessionControllerToPidControllerHandle, osMessageQueueId_t pidControllerToSessionControllerAckHandle, osMessageQueueId_t pidToBpmHandle, bool initialState);

		~PIDController() = default;

		bool Init();
		void Run();
	private:
		CircularBufferReader<optical_encoder_output_data> _data_buffer_reader;
		CircularBufferWriter<task_error_data> _task_error_buffer_writer;

		osMessageQueueId_t _sessionControllerToPidHandle;
		osMessageQueueId_t _pidControllerToSessionControllerAckHandle;
		osMessageQueueId_t _pidToBpmHandle;
		bool _enabled;

		uint32_t _curTimestamp;
		uint32_t _prevTimestamp;

		float _curAngularVelocity;

		float _desiredAngularVelocity;

		float _prevError;
		float _error;

		float GetTimeDelta();

		void SendBrakeDutyCycle(float new_duty_cycle_percent);
		void SendThrottleDutyCycle(float new_duty_cycle_percent);

		void Reset();


};


#endif /* INC_TASKS_PID_PID_HPP_ */
