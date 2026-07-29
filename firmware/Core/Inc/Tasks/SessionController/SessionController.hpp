#ifndef INC_TASKS_SESSIONCONTROLLER_SESSIONCONTROLLER_HPP_
#define INC_TASKS_SESSIONCONTROLLER_SESSIONCONTROLLER_HPP_

#include <cstring>

#include "main.h"
#include "cmsis_os.h"

#include "Config/config.h"
#include "Config/debug.h"

// SessionController.cpp gates its cross-task posts (USB session state, SD logging, BPM stop, ...)
// on the *_TASK_ENABLE macros from Config/debug.h. An undefined macro in #if is silently 0, and
// exactly that happened: this header never reached debug.h, so every gated post -- including the
// in-session flag the USB task streams sensor data by -- compiled out without a warning. The
// include above fixes it; these guards make any recurrence a build error instead of a silent one.
#if !defined(USB_CONTROLLER_TASK_ENABLE) || !defined(SD_CONTROLLER_TASK_ENABLE) \
    || !defined(FORCE_SENSOR_ADS1115_TASK_ENABLE) || !defined(FORCE_SENSOR_ADC_TASK_ENABLE) \
    || !defined(OPTICAL_ENCODER_TASK_ENABLE) || !defined(BPM_CONTROLLER_TASK_ENABLE) \
    || !defined(PID_CONTROLLER_TASK_ENABLE) || !defined(LUMEX_LCD_TASK_ENABLE) \
    || !defined(ILI9341_LCD_TASK_ENABLE)
#error "A *_TASK_ENABLE macro is not visible here; SessionController's #if-gated queue posts would silently compile out (include Config/debug.h)"
#endif

#include "FiniteStateMachine.hpp"

#include "MessagePassing/messages_public.h"
#include "MessagePassing/osqueue_helpers.h"

#include "CircularBufferReader.hpp"
#include "CircularBufferWriter.hpp"

#include "TimeKeeping/timestamps.h"

#include "input_manager_interrupts.h"
#include "sessioncontroller_main.h"


// The top-level task: runs the UI state machine and turns its state into commands for every
// other task. Run() never returns.
class SessionController
{
    public:
        SessionController(session_controller_os_task_queues* task_queues);
        ~SessionController() = default;

        bool Init(void);
        void Run(void);

    private:
        // Applies host commands routed here by the USB task, acking each one. Drained beside
        // HandleUserInputs because that is what these are: another source of input, differing
        // only in arriving over USB rather than off a button.
        void DrainHostCommands();


    private:
        bool CheckTaskQueuesValid();
        void ReportError(session_controller_task_error_ids error_id);

        // One-time posts that put the other tasks into their starting state.
        void PublishStartupState();

        // Each of these is one step of a Run() iteration; all of them are edge-triggered
        // against the _prev* fields below, so a steady state produces no queue traffic.
        void PublishSessionTransition(bool inSession);
        void PublishPidInstruction(bool pidEnabled);
        void AwaitPidAck(bool pidEnabled, bool pidOptionEnabled);
        void DriveManualBrake();
        void UpdateMeasurementDisplay();

        CircularBufferWriter<task_error_data> _task_error_buffer_writer;
        CircularBufferReader<forcesensor_output_data> _forcesensor_buffer_reader;
        CircularBufferReader<optical_encoder_output_data> _optical_encoder_buffer_reader;

        FSM _fsm;

        session_controller_os_task_queues* _task_queues;

        // Last values posted to the other tasks. A step runs only when its value moves.
        bool _prevPIDEnabled;
        float _prevDesiredAngularVelocity;
        bool _prevInSession;
        bool _pidAckReceived;
        float _prevBpmDutyCycle;

        // Newest sensor samples and what the LCD currently shows of them. The samples persist
        // across iterations: an iteration with nothing new in the buffer keeps the last reading.
        forcesensor_output_data _forceData;
        optical_encoder_output_data _opticalData;
        float _prevForce;
        float _prevAngularVelocity;
};


#endif /* INC_TASKS_SESSIONCONTROLLER_SESSIONCONTROLLER_HPP_ */
