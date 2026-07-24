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

		// Drain queued USB setting commands and ack each host-originated one (msg_id != 0).
		// The force sensor defines no command opcodes now -- its ADS1115 config is runtime
		// sysconfig applied by ReconcileConfig -- so anything received is acked UNKNOWN.
		void ProcessCommands();

		// Bring the ADS1115's config registers in line with the sysconfig store, issuing an
		// I2C write only for a register whose value actually changed (compared against
		// _applied). Called once from Init() to lay down the boot config, then every loop so
		// a host edit to any ADS1115_* parameter takes effect on the next pass. Returns false
		// if any write failed; the unchanged shadow means the next pass retries it.
		bool ReconcileConfig();
		bool ApplyIfChanged(uint8_t& applied, uint8_t target, bool (ADS1115::*setter)(uint8_t));
		bool ApplyIfChanged(uint8_t& applied, uint8_t target, bool (ADS1115::*setter)(bool));

		// Circular Buffer for ForceSensor with template bpm_output_data
		CircularBufferWriter<forcesensor_output_data> _data_buffer_writer;
		CircularBufferWriter<task_error_data> _task_error_buffer_writer;

		ADS1115 _ads1115;

		osMessageQueueId_t _sessionControllerToForceSensorHandle;
		osMessageQueueId_t _usbToForceSensorCommandHandle;
		osMessageQueueId_t _taskCompletionHandle;

		// What ReconcileConfig last wrote to the chip. Seeded to 0xFF (no valid register code)
		// so the first reconcile, from Init(), applies every register from sysconfig.
		struct Ads1115Applied
		{
			uint8_t mux;
			uint8_t gain;
			uint8_t rate;
			uint8_t comp_que;
			uint8_t mode;
			uint8_t comp_mode;
			uint8_t comp_pol;
			uint8_t comp_lat;
		};
		Ads1115Applied _applied = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

		

};

#ifdef __cplusplus
}
#endif




#endif /* INC_TASKS_FORCESENSOR_ADS1115_FORCESENSOR_ADS1115_HPP_ */
