#ifndef INC_TASKS_SESSIONCONTROLLER_SESSIONCONTROLLER_HPP_
#define INC_TASKS_SESSIONCONTROLLER_SESSIONCONTROLLER_HPP_

#include <cstring>

#include "main.h"
#include "cmsis_os.h"

#include "FiniteStateMachine.hpp"

#include "MessagePassing/messages_public.h"
#include "MessagePassing/osqueue_helpers.h"

#include "CircularBufferReader.hpp"
#include "CircularBufferWriter.hpp"

#include "TimeKeeping/timestamps.h"

#include "input_manager_interrupts.h"
#include "sessioncontroller_main.h"


#ifdef __cplusplus
extern "C" {
#endif

class SessionController
{
    public:
        SessionController(session_controller_os_task_queues* task_queues, osMutexId_t usart1Mutex);
        ~SessionController() = default;

        bool Init(void);
        void Run(void);

    private:
        CircularBufferWriter<task_error_data> _task_error_buffer_writer;
        CircularBufferReader<forcesensor_output_data> _forcesensor_buffer_reader;
        CircularBufferReader<optical_encoder_output_data> _optical_encoder_buffer_reader;

        FSM _fsm;

        session_controller_os_task_queues* _task_queues;
        osMutexId_t _usart1Mutex;

        // Latest sensor readings (drained from the circular buffers each loop iteration).
        forcesensor_output_data _force_data;
        optical_encoder_output_data _optical_encoder_data;

        // Previous values, used for change-detection so we only act/transmit on a change.
        bool _prevUSBLoggingEnabled;
        bool _prevSDLoggingEnabled;
        bool _prevPIDEnabled;
        bool _prevInSession;
        bool _pidAckReceived;
        float _prevThrottleDutyCycle;
        float _prevBpmDutyCycle;
        float _prevForce;
        float _prevAngularVelocity;

        bool CheckTaskQueuesValid();

        // Run() loop steps (see Run() for ordering).
        void SyncLoggingState();    // notify USB/SD controllers when logging enable changes
        void HandleSessionEdge();   // enable/disable sensors + BPM on session start/stop
        void SyncPidState();        // push PID enable/target changes and handle the ack
        void HandleManualControl(); // push manual throttle/BPM duty cycle while in session
        void UpdateMetrics();       // drain sensors, compute torque/power, refresh the display

        float CalculateTorque(float angularAcceleration, float force, float angularVelocity);
        float CalculatePower(float torque, float angularVelocity);
        float CalculateMechanicalLosses(float angularAcceleration, float angularVelocity);

};

#ifdef __cplusplus
}
#endif





#endif /* INC_TASKS_SESSIONCONTROLLER_SESSIONCONTROLLER_HPP_ */
