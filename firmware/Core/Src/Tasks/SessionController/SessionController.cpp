#include <Tasks/SessionController/SessionController.hpp>
#include <Config/sysconfig.h>

extern size_t task_error_circular_buffer_index_writer;
extern task_error_data task_error_circular_buffer[TASK_ERROR_CIRCULAR_BUFFER_SIZE];

extern size_t forcesensor_circular_buffer_index_writer;
extern forcesensor_output_data forcesensor_circular_buffer[FORCESENSOR_CIRCULAR_BUFFER_SIZE];

extern size_t optical_encoder_circular_buffer_index_writer;
extern optical_encoder_output_data optical_encoder_circular_buffer[OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE];


SessionController::SessionController(session_controller_os_task_queues* task_queues) :
                _task_error_buffer_writer(task_error_circular_buffer, &task_error_circular_buffer_index_writer, TASK_ERROR_CIRCULAR_BUFFER_SIZE),
                _forcesensor_buffer_reader(forcesensor_circular_buffer, &forcesensor_circular_buffer_index_writer, FORCESENSOR_CIRCULAR_BUFFER_SIZE),
                _optical_encoder_buffer_reader(optical_encoder_circular_buffer, &optical_encoder_circular_buffer_index_writer, OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE),
                _fsm(task_queues->display),
                _task_queues(task_queues),
                _prevPIDEnabled(false),
                _prevDesiredAngularVelocity(0.0f),
                _prevInSession(false),
                _pidAckReceived(false),
                _prevBpmDutyCycle(0.0f),
                _forceData{},
                _opticalData{},
                _prevForce(0.0f),
                _prevAngularVelocity(0.0f)
            {}

void SessionController::ReportError(session_controller_task_error_ids error_id)
{
    task_error_data error_data = PopulateTaskErrorDataStruct(
        get_timestamp(),
        TASK_OFFSET_SESSION_CONTROLLER,
        static_cast<uint32_t>(error_id)
    );
    _task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
}

bool SessionController::CheckTaskQueuesValid()
{
    if (_task_queues == nullptr
        #if USB_CONTROLLER_TASK_ENABLE
        || _task_queues->usb_controller == nullptr
        #endif
        #if SD_CONTROLLER_TASK_ENABLE
        || _task_queues->sd_controller == nullptr
        #endif
        #if FORCE_SENSOR_ADS1115_TASK_ENABLE || FORCE_SENSOR_ADC_TASK_ENABLE
        || _task_queues->force_sensor == nullptr
        #endif
        #if OPTICAL_ENCODER_TASK_ENABLE
        || _task_queues->optical_sensor == nullptr
        #endif
        #if BPM_CONTROLLER_TASK_ENABLE
        || _task_queues->bpm_controller == nullptr
        #endif
        #if PID_CONTROLLER_TASK_ENABLE
        || _task_queues->pid_controller == nullptr
        || _task_queues->pid_controller_ack == nullptr
        #endif
        #if (LUMEX_LCD_TASK_ENABLE || ILI9341_LCD_TASK_ENABLE)
        || _task_queues->display == nullptr
        #endif
        #if USB_CONTROLLER_TASK_ENABLE
        // The host command route: without these the SessionController silently swallows every
        // command the USB task forwards, rather than reporting the bad wiring.
        || _task_queues->usb_command == nullptr
        || _task_queues->task_completion == nullptr
        #endif
    )
    {
        ReportError(ERROR_SESSION_CONTROLLER_INVALID_TASK_QUEUE_POINTER);
        return false;
    }

    return true;
}

// The timestamp counter this task stamps every sample from is started in main(), before the
// scheduler runs -- it is shared with the display task and owned by neither, so neither starts it.
bool SessionController::Init(void)
{
    return CheckTaskQueuesValid();
}


// Puts the other tasks into the state this one assumes they are in, before the first iteration.
void SessionController::PublishStartupState()
{
    session_controller_to_pid_controller pid_msg;
    pid_msg.enable_status = false;
    pid_msg.desired_angular_velocity = _fsm.GetDesiredAngularVelocity();
    osMessageQueuePut(_task_queues->pid_controller, &pid_msg, 0, osWaitForever);

    // Sensor sampling runs continuously, independent of session state: enable it once here and
    // never disable it, so a session starts against sensors that are already warm. Only what
    // leaves the board (USB streaming) and what the board drives (the BPM) is gated behind an
    // active session.
    bool alwaysEnabled = true;
    osMessageQueuePut(_task_queues->optical_sensor, &alwaysEnabled, 0, osWaitForever);
    osMessageQueuePut(_task_queues->force_sensor, &alwaysEnabled, 0, osWaitForever);

    // The USB task streams sensor data only while a session runs, so it needs the session state.
    // There is no separate "USB logging" switch -- a host that is connected during a session
    // always receives that session's data. Post the starting state so it does not have to assume.
    #if USB_CONTROLLER_TASK_ENABLE
    bool sessionStreaming = false;
    osMessageQueuePut(_task_queues->usb_controller, &sessionStreaming, 0, osWaitForever);
    #endif
}

// A session just started or stopped. Sensor sampling is not gated (it is enabled once at
// startup and left on), so what changes here is what leaves the board, what the board drives,
// and -- critically -- that the BPM stops on the way out. The brake must never be actuated
// outside a session.
void SessionController::PublishSessionTransition(bool inSession)
{
    // Tell the USB task whether a session is running: it streams sensor data only then.
    #if USB_CONTROLLER_TASK_ENABLE
    bool sessionStreaming = inSession;
    osMessageQueuePut(_task_queues->usb_controller, &sessionStreaming, 0, osWaitForever);
    #endif

    if (inSession)
    {
        // Draw the fields of the in-session screen at their starting values.
        _fsm.DisplayAngularVelocity(0);
        _fsm.DisplayForce(0);

        if (_fsm.GetPIDOptionToggleableEnabledStatus()) _fsm.DisplayPIDEnabled();
        else _fsm.DisplayManualBPMDutyCycle();
    }
    else
    {
        session_controller_to_bpm bpmSettings;
        bpmSettings.op = STOP_PWM;
        bpmSettings.new_duty_cycle_percent = 0.0f;

        osMessageQueuePut(_task_queues->bpm_controller, &bpmSettings, 0, osWaitForever);

        // The loop stops with the session, and this is the only place that can say so: Run()
        // publishes PID instructions below its in-session gate, so once the session is over
        // that step never executes again. Left armed, the task went on computing against a
        // session that had ended and filling its output queue until every pass logged a
        // queue-full warning -- and because _prevPIDEnabled stayed true, the next session
        // found no enable edge to publish, so the BPM was never pointed at the PID output and
        // the screen read PIDE over a brake the controller no longer reached.
        PublishPidInstruction(false);
    }
}

// One instruction carries both halves of what the PID task needs -- whether to run, and what
// to aim at -- so this republishes when either moves.
//
// The setpoint half is new: it used to be sent only on an enable edge, which was enough while
// the only way to change it was the menu, and the menu is unreachable during a session. It is
// a runtime sysconfig parameter now, so the host can move it over USB mid-run, and a task
// still chasing the previous figure would leave the app showing one setpoint while the brake
// worked towards another.
//
// A setpoint change is only worth sending while the loop is enabled: disabled, the task is
// blocked waiting for an instruction and the next enable will carry the current value anyway.
// The task resets its integrator whenever it takes an instruction while enabled, which is the
// behaviour a setpoint change wants in any case.
void SessionController::PublishPidInstruction(bool pidEnabled)
{
    const float desiredAngularVelocity = _fsm.GetDesiredAngularVelocity();

    const bool enableMoved = (pidEnabled != _prevPIDEnabled);
    const bool setpointMoved = pidEnabled && (desiredAngularVelocity != _prevDesiredAngularVelocity);

    if (!enableMoved && !setpointMoved) return;

    session_controller_to_pid_controller pid_msg;
    pid_msg.enable_status = pidEnabled;
    pid_msg.desired_angular_velocity = desiredAngularVelocity;

    _pidAckReceived = false;
    osMessageQueuePut(_task_queues->pid_controller, &pid_msg, 0, osWaitForever);
    _prevPIDEnabled = pidEnabled;
    _prevDesiredAngularVelocity = desiredAngularVelocity;
}

// The PID task acknowledges an enable/disable, and only once it has may the BPM be pointed at
// the PID output. Does nothing once the outstanding change has been acknowledged.
void SessionController::AwaitPidAck(bool pidEnabled, bool pidOptionEnabled)
{
    if (_pidAckReceived) return;

    GetLatestFromQueue(_task_queues->pid_controller_ack, &_pidAckReceived, sizeof(_pidAckReceived), 0);
    if (!_pidAckReceived) return;

    if (pidEnabled)
    {
        session_controller_to_bpm bpmSettings{};
        bpmSettings.op = READ_FROM_PID;
        osMessageQueuePut(_task_queues->bpm_controller, &bpmSettings, 0, osWaitForever);
    }

    if (pidOptionEnabled)
    {
        _fsm.DisplayPIDEnabled();
    }
}

// With the PID option off, the rotary encoder sets the brake duty cycle by hand and it goes
// straight to the BPM task. The brake is the only actuator this board drives.
void SessionController::DriveManualBrake()
{
    const float bpmDutyCycle = _fsm.GetDesiredBpmDutyCycle();

    if (bpmDutyCycle != _prevBpmDutyCycle)
    {
        session_controller_to_bpm bpmSettings;
        bpmSettings.op = START_PWM;
        bpmSettings.new_duty_cycle_percent = bpmDutyCycle;

        osMessageQueuePut(_task_queues->bpm_controller, &bpmSettings, 0, osWaitForever);

        _prevBpmDutyCycle = bpmDutyCycle;
    }

    _fsm.DisplayManualBPMDutyCycle();
}

// Nothing is derived here. The device streams what it measures and the host computes torque
// and power from it, so the constants involved (inertia, lever arm, gear ratio) live on the PC
// and a past run can be recomputed after correcting one. The LCD shows the two measured
// quantities directly, so the dyno still reads out usefully with no computer attached.
void SessionController::UpdateMeasurementDisplay()
{
    // Drain each buffer to its newest sample; with nothing new, the last one stands.
    while (_forcesensor_buffer_reader.GetElementAndIncrementIndex(_forceData));
    while (_optical_encoder_buffer_reader.GetElementAndIncrementIndex(_opticalData));

    if (_prevAngularVelocity != _opticalData.angular_velocity)
    {
        _fsm.DisplayAngularVelocity(_opticalData.angular_velocity);
        _fsm.DisplayAngularAcceleration(_opticalData.angular_acceleration);
        _prevAngularVelocity = _opticalData.angular_velocity;
    }

    if (_prevForce != _forceData.force)
    {
        _fsm.DisplayForce(_forceData.force);
        _prevForce = _forceData.force;
    }
}

// The host's route into the UI state. Each command is acked through the shared completion
// queue the USB task relays, so the app learns whether it was applied rather than assuming.
void SessionController::DrainHostCommands()
{
    if (_task_queues->usb_command == nullptr)
    {
        return;
    }

    usb_task_command cmd;

    while (osMessageQueueGet(_task_queues->usb_command, &cmd, NULL, 0) == osOK)
    {
        uint32_t status = USB_RSP_UNKNOWN_COMMAND;

        switch (cmd.opcode)
        {
            case SESSION_CMD_SET_BRAKE_DUTY_CYCLE:
            {
                if (cmd.body_len < sizeof(session_set_brake_duty_body))
                {
                    status = USB_RSP_MALFORMED;
                    break;
                }

                session_set_brake_duty_body body;
                memcpy(&body, cmd.body, sizeof(body));

                // NOT_SUPPORTED rather than OK when no session is running: the command was
                // understood and deliberately not obeyed, and the app should say so rather
                // than show a duty cycle the brake is not at.
                status = _fsm.SetHostBrakeDutyCycle(body.duty_cycle)
                         ? USB_RSP_OK : USB_RSP_NOT_SUPPORTED;
                break;
            }

            default:
                break;
        }

        // msg_id 0 is a firmware-internal command that wants no ack.
        if (cmd.msg_id != 0 && _task_queues->task_completion != nullptr)
        {
            usb_task_completion done;
            done.task_offset = TASK_OFFSET_SESSION_CONTROLLER;
            done.opcode = cmd.opcode;
            done.msg_id = cmd.msg_id;
            done.status = status;
            osMessageQueuePut(_task_queues->task_completion, &done, 0, 0);
        }
    }
}

void SessionController::Run()
{
    PublishStartupState();

    while (1)
    {
        _fsm.HandleUserInputs();
        DrainHostCommands();

        const bool inSession = _fsm.GetInSessionStatus();
        if (inSession != _prevInSession)
        {
            PublishSessionTransition(inSession);
            _prevInSession = inSession;
        }

        // Everything below either drives an actuator or refreshes the in-session screen, so
        // outside a session there is nothing left to do this iteration.
        if (!inSession)
        {
            osDelay(sysconfig_get_u32(SYSCFG_SESSIONCONTROLLER_TASK_OSDELAY));
            continue;
        }

        const bool pidEnabled = _fsm.GetPIDEnabledModeStatus();
        const bool pidOptionEnabled = _fsm.GetPIDOptionToggleableEnabledStatus();

        PublishPidInstruction(pidEnabled);
        AwaitPidAck(pidEnabled, pidOptionEnabled);

        if (!pidOptionEnabled)
        {
            DriveManualBrake();
        }

        UpdateMeasurementDisplay();

        osDelay(sysconfig_get_u32(SYSCFG_SESSIONCONTROLLER_TASK_OSDELAY));
    }
}


extern "C" void sessioncontroller_main(session_controller_os_task_queues* task_queues)
{
    SessionController controller = SessionController(task_queues);

    if (!controller.Init())
    {
        osThreadSuspend(osThreadGetId());
    }

    controller.Run();
}
