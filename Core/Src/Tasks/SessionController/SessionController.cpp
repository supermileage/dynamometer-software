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
                _prevSDLoggingEnabled(false),
                _prevPIDEnabled(false),
                _prevInSession(false)
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
    forcesensor_output_data force_data;
    optical_encoder_output_data optical_encoder_data;

    float prevThrottleDutyCycle = 0.0f;
    float prevBpmDutyCycle = 0.0f;
    float prevForce = 0.0f;
    float prevAngularVelocity = 0.0f;

    memset(&force_data, 0, sizeof(force_data));
    memset(&optical_encoder_data, 0, sizeof(optical_encoder_data));


    session_controller_to_pid_controller pid_msg;
    pid_msg.enable_status = false;
    pid_msg.desired_angular_velocity = _fsm.GetDesiredAngularVelocity();

    bool pidAckReceived = false;
    osMessageQueuePut(_task_queues->pid_controller, &pid_msg, 0, osWaitForever);

    // Sensor sampling and USB streaming run continuously, independent of session state, so the
    // dyno host receives live data the moment it connects -- no session required. Enable them
    // once here and never disable them; only actuation (driving the BPM / throttle) stays gated
    // behind an active session in the loop below.
    bool alwaysEnabled = true;
    #if USB_CONTROLLER_TASK_ENABLE
    osMessageQueuePut(_task_queues->usb_controller, &alwaysEnabled, 0, osWaitForever);
    #endif
    osMessageQueuePut(_task_queues->optical_sensor, &alwaysEnabled, 0, osWaitForever);
    osMessageQueuePut(_task_queues->force_sensor, &alwaysEnabled, 0, osWaitForever);

    while(1)
    {

        // First Handle Any User Inputs
        _fsm.HandleUserInputs();



        
        // Get SD Card Enabled Status and enable SD Card Controller
        bool SDLoggingEnabled = _fsm.GetSDLoggingEnabledStatus();
        // Only if the status has changed
        if (SDLoggingEnabled ^ _prevSDLoggingEnabled)
        {
            #if SD_CONTROLLER_TASK_ENABLE
            osMessageQueuePut(_task_queues->sd_controller, &SDLoggingEnabled, 0, osWaitForever);
            #endif 
            _prevSDLoggingEnabled = SDLoggingEnabled;
        }
        

        bool InSessionStatus = _fsm.GetInSessionStatus();

        bool InSessionRisingEdge = InSessionStatus && !_prevInSession;
        bool InSessionFallingEdge = !InSessionStatus && _prevInSession;
 
        // Get PID enabled status and enable PID Controller
        bool PIDEnabled = _fsm.GetPIDEnabledModeStatus();
        bool PIDOptionToggleableEnabled = _fsm.GetPIDOptionToggleableEnabledStatus();

        // Run only on an in-session transition. Sensor sampling and USB streaming stay on
        // continuously (enabled once at startup), so a transition just resets the display on
        // entry and -- critically -- stops driving the BPM on exit. The brake must never be
        // actuated outside a session.
        if (InSessionRisingEdge || InSessionFallingEdge)
        {
            if (InSessionRisingEdge)
            {
                _fsm.DisplayRpm(0);

                _fsm.DisplayTorque(0);
                _fsm.DisplayPower(0);

                if (PIDOptionToggleableEnabled) _fsm.DisplayPIDEnabled();
                else if (_fsm.GetManualBpmModeStatus()) _fsm.DisplayManualBPMDutyCycle();
                else _fsm.DisplayManualThrottleDutyCycle();

            }

            // Leaving the session: stop the BPM PWM so nothing is driven while idle.
            else if (InSessionFallingEdge)
            {
                session_controller_to_bpm bpmSettings;
                bpmSettings.op = STOP_PWM;
                bpmSettings.new_duty_cycle_percent = static_cast<float>(0);

                osMessageQueuePut(_task_queues->bpm_controller, &bpmSettings, 0, osWaitForever);
            }

            _prevInSession = InSessionStatus;


        }

        if (!InSessionStatus)
        {
            osDelay(SESSIONCONTROLLER_TASK_OSDELAY);
            continue;
        }

        // Only if the status has changed
        if (PIDEnabled ^ _prevPIDEnabled)
        {
            session_controller_to_pid_controller pid_msg;
            pid_msg.enable_status = PIDEnabled;
            pid_msg.desired_angular_velocity = _fsm.GetDesiredAngularVelocity();
            pidAckReceived = false;
            osMessageQueuePut(_task_queues->pid_controller, &pid_msg, 0, osWaitForever);
            _prevPIDEnabled = PIDEnabled;


        }

        if (!pidAckReceived)
        {
            GetLatestFromQueue(_task_queues->pid_controller_ack, &pidAckReceived, sizeof(pidAckReceived), 0);
            // This should only run once PIDEnabled changes from false to true and once the ack has been received
            if (pidAckReceived)
            {
                
                if (PIDEnabled)
                {
                    session_controller_to_bpm bpmSettings{};
                    bpmSettings.op = READ_FROM_PID;
                    osMessageQueuePut(_task_queues->bpm_controller, &bpmSettings, 0, osWaitForever);
                    
                }

                if (PIDOptionToggleableEnabled)
                {
                    _fsm.DisplayPIDEnabled();
                }
                
                
            } 
        }

        
        if (!PIDOptionToggleableEnabled) 
        {
        
            if (_fsm.GetManualThrottleModeStatus())
            {
                // Always run since the PID controller could be turned off while in-session
                float newThrottleDutyCycle = _fsm.GetDesiredThrottleDutyCycle();
                if (newThrottleDutyCycle != prevThrottleDutyCycle)
                {
                    osMutexAcquire(_usart1Mutex, osWaitForever);

                    uint8_t newDutyCycle255 = static_cast<uint8_t>(newThrottleDutyCycle * 255.0f);
                    HAL_UART_Transmit(&huart1, &newDutyCycle255, sizeof(newDutyCycle255), HAL_MAX_DELAY);

                    osMutexRelease(_usart1Mutex);

                    prevThrottleDutyCycle = newThrottleDutyCycle;
                }
                _fsm.DisplayManualThrottleDutyCycle();
                 
            }
            else
            {
                
                float newBpmDutyCycle = _fsm.GetDesiredBpmDutyCycle();

                if (newBpmDutyCycle != prevBpmDutyCycle)
                {
                    session_controller_to_bpm bpmSettings;
                    bpmSettings.op = START_PWM;
                    bpmSettings.new_duty_cycle_percent =  newBpmDutyCycle;
                    osMessageQueuePut(_task_queues->bpm_controller, &bpmSettings, 0, osWaitForever);
                    prevBpmDutyCycle = newBpmDutyCycle;
                } 
                _fsm.DisplayManualBPMDutyCycle();
            
            }
            

        }

        // Get the most recent force sensor data
        while(_forcesensor_buffer_reader.GetElementAndIncrementIndex(force_data));

        // Get the most recent optical encoder data
        while(_optical_encoder_buffer_reader.GetElementAndIncrementIndex(optical_encoder_data));

        // TO UPDATE
        float angularAcceleration = 0;
        float angularVelocity = optical_encoder_data.angular_velocity;
        float force = force_data.force;
        
        float torque = CalculateTorque(angularAcceleration, force, angularVelocity);
        
        
        if (prevAngularVelocity != angularVelocity)
        {
            _fsm.DisplayRpm(optical_encoder_data.angular_velocity);

            if (prevForce != force)
            {
                _fsm.DisplayTorque(torque);
                _fsm.DisplayPower(CalculatePower(torque, angularVelocity));
                prevForce = force;
            }
            prevAngularVelocity = angularVelocity;
        }


        

        osDelay(SESSIONCONTROLLER_TASK_OSDELAY);

        
            

            

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




