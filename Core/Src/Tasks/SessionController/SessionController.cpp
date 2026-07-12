#include <Tasks/SessionController/SessionController.hpp>

extern UART_HandleTypeDef huart1;

extern size_t task_error_circular_buffer_index_writer;
extern task_error_data task_error_circular_buffer[TASK_ERROR_CIRCULAR_BUFFER_SIZE];

extern size_t forcesensor_circular_buffer_index_writer;
extern forcesensor_output_data forcesensor_circular_buffer[FORCESENSOR_CIRCULAR_BUFFER_SIZE];

extern size_t optical_encoder_circular_buffer_index_writer;
extern optical_encoder_output_data optical_encoder_circular_buffer[OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE];

SessionController::SessionController(session_controller_os_task_queues* task_queues, osMutexId_t usart1Mutex) : 
                _task_error_buffer_writer(task_error_circular_buffer, &task_error_circular_buffer_index_writer, TASK_ERROR_CIRCULAR_BUFFER_SIZE),
                _forcesensor_buffer_reader(forcesensor_circular_buffer, &forcesensor_circular_buffer_index_writer, FORCESENSOR_CIRCULAR_BUFFER_SIZE),
                _optical_encoder_buffer_reader(optical_encoder_circular_buffer, &optical_encoder_circular_buffer_index_writer, OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE),
                _fsm(task_queues->lumex_lcd),
                _task_queues(task_queues),
                _usart1Mutex(usart1Mutex),
                _force_data{},
                _optical_encoder_data{},
                _prevUSBLoggingEnabled(false),
                _prevSDLoggingEnabled(false),
                _prevPIDEnabled(false),
                _prevInSession(false),
                _pidAckReceived(false),
                _prevThrottleDutyCycle(0.0f),
                _prevBpmDutyCycle(0.0f),
                _prevForce(0.0f),
                _prevAngularVelocity(0.0f)
            {}

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
        #if LUMEX_LCD_TASK_ENABLE
        || _task_queues->lumex_lcd == nullptr
        #endif
    )
    {
        task_error_data error_data = PopulateTaskErrorDataStruct(
            get_timestamp(),
            TASK_OFFSET_SESSION_CONTROLLER,
            static_cast<uint32_t>(ERROR_SESSION_CONTROLLER_INVALID_TASK_QUEUE_POINTER)
        );
        _task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
        return false;
    }

    

    return true;
}

bool SessionController::Init(void)
{

    if (start_timestamp_timer() != HAL_OK)
    {
    	task_error_data error_data = PopulateTaskErrorDataStruct(
            get_timestamp(),
            TASK_OFFSET_SESSION_CONTROLLER,
            static_cast<uint32_t>(ERROR_SESSION_CONTROLLER_TIMESTAMP_TIMER_START_FAILURE)
        );
        _task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
        return false;
    }

    if (_usart1Mutex == nullptr)
    {
        task_error_data error_data = PopulateTaskErrorDataStruct(
            get_timestamp(),
            TASK_OFFSET_SESSION_CONTROLLER,
            static_cast<uint32_t>(ERROR_SESSION_CONTROLLER_INVALID_UART1_MUTEX_POINTER)
        );
        _task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
        return false;
    }

	return CheckTaskQueuesValid();
}

void SessionController::Run()
{
    // Send the PID controller its initial (disabled) target before entering the loop.
    session_controller_to_pid_controller pid_msg;
    pid_msg.enable_status = false;
    pid_msg.desired_angular_velocity = _fsm.GetDesiredAngularVelocity();
    osMessageQueuePut(_task_queues->pid_controller, &pid_msg, 0, osWaitForever);

    while (1)
    {
        _fsm.HandleUserInputs();

        SyncLoggingState();
        HandleSessionEdge();

        // Everything below only runs during an active session.
        if (!_fsm.GetInSessionStatus())
        {
            osDelay(SESSIONCONTROLLER_TASK_OSDELAY);
            continue;
        }

        SyncPidState();
        HandleManualControl();
        UpdateMetrics();

        osDelay(SESSIONCONTROLLER_TASK_OSDELAY);
    }
}

void SessionController::SyncLoggingState()
{
    bool usbLoggingEnabled = _fsm.GetUSBLoggingEnabledStatus();
    if (usbLoggingEnabled != _prevUSBLoggingEnabled)
    {
        #if USB_CONTROLLER_TASK_ENABLE
        osMessageQueuePut(_task_queues->usb_controller, &usbLoggingEnabled, 0, osWaitForever);
        #endif
        _prevUSBLoggingEnabled = usbLoggingEnabled;
    }

    bool sdLoggingEnabled = _fsm.GetSDLoggingEnabledStatus();
    if (sdLoggingEnabled != _prevSDLoggingEnabled)
    {
        #if SD_CONTROLLER_TASK_ENABLE
        osMessageQueuePut(_task_queues->sd_controller, &sdLoggingEnabled, 0, osWaitForever);
        #endif
        _prevSDLoggingEnabled = sdLoggingEnabled;
    }
}

void SessionController::HandleSessionEdge()
{
    bool inSession = _fsm.GetInSessionStatus();
    bool risingEdge = inSession && !_prevInSession;
    bool fallingEdge = !inSession && _prevInSession;

    if (!risingEdge && !fallingEdge)
    {
        return;
    }

    bool sensorsEnable = risingEdge;

    if (risingEdge)
    {
        _fsm.DisplayRpm(0);
        _fsm.DisplayTorque(0);
        _fsm.DisplayPower(0);

        if (_fsm.GetPIDOptionToggleableEnabledStatus()) _fsm.DisplayPIDEnabled();
        else if (_fsm.GetManualBpmModeStatus()) _fsm.DisplayManualBPMDutyCycle();
        else _fsm.DisplayManualThrottleDutyCycle();
    }
    else // falling edge: stop the brake PWM
    {
        session_controller_to_bpm bpmSettings;
        bpmSettings.op = STOP_PWM;
        bpmSettings.new_duty_cycle_percent = 0.0f;
        osMessageQueuePut(_task_queues->bpm_controller, &bpmSettings, 0, osWaitForever);
    }

    osMessageQueuePut(_task_queues->optical_sensor, &sensorsEnable, 0, osWaitForever);
    osMessageQueuePut(_task_queues->force_sensor, &sensorsEnable, 0, osWaitForever);

    _prevInSession = inSession;
}

void SessionController::SyncPidState()
{
    bool pidEnabled = _fsm.GetPIDEnabledModeStatus();

    if (pidEnabled != _prevPIDEnabled)
    {
        session_controller_to_pid_controller pid_msg;
        pid_msg.enable_status = pidEnabled;
        pid_msg.desired_angular_velocity = _fsm.GetDesiredAngularVelocity();
        _pidAckReceived = false;
        osMessageQueuePut(_task_queues->pid_controller, &pid_msg, 0, osWaitForever);
        _prevPIDEnabled = pidEnabled;
    }

    if (!_pidAckReceived)
    {
        GetLatestFromQueue(_task_queues->pid_controller_ack, &_pidAckReceived, sizeof(_pidAckReceived), 0);

        // Runs once after PID is enabled and the controller has acknowledged.
        if (_pidAckReceived)
        {
            if (pidEnabled)
            {
                session_controller_to_bpm bpmSettings{};
                bpmSettings.op = READ_FROM_PID;
                osMessageQueuePut(_task_queues->bpm_controller, &bpmSettings, 0, osWaitForever);
            }

            if (_fsm.GetPIDOptionToggleableEnabledStatus())
            {
                _fsm.DisplayPIDEnabled();
            }
        }
    }
}

void SessionController::HandleManualControl()
{
    // Manual control only applies when the toggleable PID option is off.
    if (_fsm.GetPIDOptionToggleableEnabledStatus())
    {
        return;
    }

    if (_fsm.GetManualThrottleModeStatus())
    {
        // Transmit the throttle duty cycle over UART only when it changes.
        float newThrottleDutyCycle = _fsm.GetDesiredThrottleDutyCycle();
        if (newThrottleDutyCycle != _prevThrottleDutyCycle)
        {
            osMutexAcquire(_usart1Mutex, osWaitForever);

            uint8_t newDutyCycle255 = static_cast<uint8_t>(newThrottleDutyCycle * 255.0f);
            HAL_UART_Transmit(&huart1, &newDutyCycle255, sizeof(newDutyCycle255), HAL_MAX_DELAY);

            osMutexRelease(_usart1Mutex);

            _prevThrottleDutyCycle = newThrottleDutyCycle;
        }
        _fsm.DisplayManualThrottleDutyCycle();
    }
    else
    {
        // Push the brake (BPM) duty cycle only when it changes.
        float newBpmDutyCycle = _fsm.GetDesiredBpmDutyCycle();
        if (newBpmDutyCycle != _prevBpmDutyCycle)
        {
            session_controller_to_bpm bpmSettings;
            bpmSettings.op = START_PWM;
            bpmSettings.new_duty_cycle_percent = newBpmDutyCycle;
            osMessageQueuePut(_task_queues->bpm_controller, &bpmSettings, 0, osWaitForever);
            _prevBpmDutyCycle = newBpmDutyCycle;
        }
        _fsm.DisplayManualBPMDutyCycle();
    }
}

void SessionController::UpdateMetrics()
{
    // Drain the circular buffers down to the most recent reading.
    while (_forcesensor_buffer_reader.GetElementAndIncrementIndex(_force_data));
    while (_optical_encoder_buffer_reader.GetElementAndIncrementIndex(_optical_encoder_data));

    float angularAcceleration = 0; // TODO: derive angular acceleration from the optical encoder
    float angularVelocity = _optical_encoder_data.angular_velocity;
    float force = _force_data.force;

    float torque = CalculateTorque(angularAcceleration, force, angularVelocity);

    if (_prevAngularVelocity != angularVelocity)
    {
        _fsm.DisplayRpm(_optical_encoder_data.angular_velocity);

        if (_prevForce != force)
        {
            _fsm.DisplayTorque(torque);
            _fsm.DisplayPower(CalculatePower(torque, angularVelocity));
            _prevForce = force;
        }
        _prevAngularVelocity = angularVelocity;
    }
}

float SessionController::CalculateTorque(float angularAcceleration, float force, float angularVelocity)
{
    return (MOMENT_OF_INERTIA_KG_M2 * angularAcceleration + force * DISTANCE_FROM_FORCE_SENSOR_TO_CENTER_OF_SHAFT_M + CalculateMechanicalLosses(angularAcceleration, angularVelocity));
}

float SessionController::CalculatePower(float torque, float angularVelocity)
{
    return torque * angularVelocity;
}

float SessionController::CalculateMechanicalLosses(float angularAcceleration, float angularVelocity)
{
    return 0;
}

extern "C" void sessioncontroller_main(session_controller_os_task_queues* task_queues, osMutexId_t usart1Mutex)
{
    SessionController controller = SessionController(task_queues, usart1Mutex);

	if (!controller.Init())
	{
		 osThreadSuspend(osThreadGetId());
	}


	controller.Run();
}




