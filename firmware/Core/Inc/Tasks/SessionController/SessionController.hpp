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
    || !defined(PID_CONTROLLER_TASK_ENABLE) || !defined(LUMEX_LCD_TASK_ENABLE)
#error "A *_TASK_ENABLE macro is not visible here; SessionController's #if-gated queue posts would silently compile out (include Config/debug.h)"
#endif

#include "FiniteStateMachine.hpp"

#include "MessagePassing/messages_public.h"
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

        bool _prevSDLoggingEnabled;
        bool _prevPIDEnabled;
        bool _prevInSession;

        bool CheckTaskQueuesValid();
};

#ifdef __cplusplus
}
#endif





#endif /* INC_TASKS_SESSIONCONTROLLER_SESSIONCONTROLLER_HPP_ */
