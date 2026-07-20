#include "Tasks/SessionController/FiniteStateMachine.hpp"

FSM::FSM(osMessageQueueId_t sessionControllerToLumexLcdHandle) :
        _sessionControllerToLumexLcdHandle(sessionControllerToLumexLcdHandle), 
        _state{
            State::MainDynoState::INIT_STATE,
            State::SettingsState::INIT_STATE,
            State::DesiredRpmUnitsState::INIT_STATE,
          },
        _sdLoggingEnabled(false),
        _pidOptionToggleableEnabled(false),
        _pidEnabled(false),
        _throttleControlModeEnabled(false),
        _inSession(false),
        _desiredManualBpmDutyCycle(0),
        _desiredManualThrottleDutyCycle(0),
        _desiredRpm(5000),
        _fsmInputDataIndex(0)
        {
            ClearDisplay();
            IdleState();

        }

template<typename T>
static inline T clamp(T x, T minVal, T maxVal)
{
    if (x < minVal) return minVal;
    if (x > maxVal) return maxVal;
    return x;
}


void FSM::HandleUserInputs(void)
{
    
    while(_fsmInputDataIndex != interrupt_input_data_index)
    {
        volatile button_press_data* current_button_data = get_circular_buffer_data(_fsmInputDataIndex);
        switch(current_button_data->opcode)
        {
            case ROT_EN_TICKS:
                HandleRotaryEncoderInput(current_button_data->positive);
                break;
            case ROT_EN_SW:
                HandleRotaryEncoderSwInput();
                break;
            case BTN_BACK:
                HandleButtonBackInput();
                break;
            case BTN_SELECT:
                HandleButtonSelectInput();
                break;
            case BTN_BRAKE:
                HandleButtonBrakeInput(current_button_data->positive);
                break;
            default:
                break;
        }

        _fsmInputDataIndex = (_fsmInputDataIndex + 1) % USER_INPUT_CIRCULAR_BUFFER_SIZE;
    }
}

void FSM::HandleRotaryEncoderInput(bool positiveTick)
{
    switch(_state.mainState)
    {
        case State::MainDynoState::IDLE:
            break;
        case State::MainDynoState::SETTINGS_MENU:
            switch(_state.settingsState)
            {
                case State::SettingsState::SD_LOGGING_OPTION_DISPLAYED:
                    if (positiveTick) PIDOptionDisplayedSettingsState();
                    else PIDDesiredRPMOptionDisplayedSettingsState();
                    break;
                case State::SettingsState::SD_LOGGING_OPTION_EDIT:
                    // _sdLoggingEnabled = positiveTick;
                    // SDLoggingOptionEditSettingsState();
                    break; 
                case State::SettingsState::PID_ENABLE_DISPLAYED:
                    if (positiveTick) PIDDesiredRPMOptionDisplayedSettingsState();
                    else SDLoggingOptionDisplayedSettingsState();
                    break;
                case State::SettingsState::PID_ENABLE_EDIT:
                    // _pidOptionToggleableEnabled = positiveTick;
                    // PIDOptionEditSettingsState();
                    break;
                case State::SettingsState::PID_DESIRED_RPM_DISPLAYED:
                    if (positiveTick) SDLoggingOptionDisplayedSettingsState();
                    else PIDOptionDisplayedSettingsState();
                    break;
                case State::SettingsState::PID_DESIRED_RPM_EDIT:
                    ConvertUserInputIntoDesiredRpm(positiveTick);
                    PIDDesiredRPMOptionEditSettingsState(false);
                    break;
                default:
                    break;
            }
            break;
        case State::MainDynoState::IN_SESSION:
            float increment = 0.01f;
            if (!positiveTick)
            {
                increment *= -1;
            }
            if (_throttleControlModeEnabled)
            {
                _desiredManualThrottleDutyCycle = clamp(_desiredManualThrottleDutyCycle + increment, 0.0f, 1.0f);
            }
            else
            {
                _desiredManualBpmDutyCycle = clamp(_desiredManualBpmDutyCycle + increment, 0.0f, 1.0f);
            }

            break;
    }

}
void FSM::HandleRotaryEncoderSwInput(void)
{
//  Was not working, will debug later
//    _inSession = true;
//    InSessionState();
    
    
//      switch(_state.mainState)
//      {
//          case State::MainDynoState::IDLE:
// //             InSessionState();
//              break;
//          case State::MainDynoState::SETTINGS_MENU:
// //             InSessionState();
//               switch(_state.settingsState)
//               {
//                   case State::SettingsState::PID_DESIRED_RPM_EDIT:
//                       break;
//                   default:
//                       break;
//               }
//              break;
//          case State::MainDynoState::IN_SESSION:
//              break;
//      }

}
void FSM::HandleButtonBackInput(void)
{
    switch(_state.mainState)
    {
        case State::MainDynoState::IDLE:
            break;
        case State::MainDynoState::SETTINGS_MENU:
            switch(_state.settingsState)
            {
                case State::SettingsState::SD_LOGGING_OPTION_DISPLAYED:
                    IdleState();
                    break;
                case State::SettingsState::SD_LOGGING_OPTION_EDIT:
                    SDLoggingOptionDisplayedSettingsState();
                    break; 
                case State::SettingsState::PID_ENABLE_DISPLAYED:
                    IdleState();
                    break;
                case State::SettingsState::PID_ENABLE_EDIT:
                    PIDOptionDisplayedSettingsState();
                    break;
                case State::SettingsState::PID_DESIRED_RPM_DISPLAYED:
                    IdleState();
                    break;
                case State::SettingsState::PID_DESIRED_RPM_EDIT:
                    _state.desiredRpmUnitsState = static_cast<State::DesiredRpmUnitsState>((static_cast<int>(_state.desiredRpmUnitsState) - 1 + static_cast<int>(State::DesiredRpmUnitsState::NUM_STATES)) % static_cast<int>(State::DesiredRpmUnitsState::NUM_STATES));
                    // This will occur if it loops back to State of NUM_STATES - 1
                    if (static_cast<int>(_state.desiredRpmUnitsState) != static_cast<int>(State::DesiredRpmUnitsState::NUM_STATES) - 1) {
                        PIDDesiredRPMOptionEditSettingsState(false);
                    }
                    else
                    {
                        PIDDesiredRPMOptionDisplayedSettingsState();
                    }
                    break;
                default:
                    break;
            }
            break;
        case State::MainDynoState::IN_SESSION:
            
            break;
    }

}

void FSM::HandleButtonSelectInput(void)
{
	switch(_state.mainState)
    {
        case State::MainDynoState::IDLE:
            SDLoggingOptionDisplayedSettingsState();
            break;
        case State::MainDynoState::SETTINGS_MENU:
            switch(_state.settingsState)
            {
                case State::SettingsState::SD_LOGGING_OPTION_DISPLAYED:
                    _sdLoggingEnabled = !_sdLoggingEnabled;
                    SDLoggingOptionEditSettingsState();
                    // We don't actually want to change state, should remove the SD_LOGGING_OPTION_EDIT state later
                    _state.settingsState = State::SettingsState::SD_LOGGING_OPTION_DISPLAYED;
                    break;
                case State::SettingsState::SD_LOGGING_OPTION_EDIT:
                    // SDLoggingOptionEditSettingsState();
                    break; 
                case State::SettingsState::PID_ENABLE_DISPLAYED:
                    _pidOptionToggleableEnabled = !_pidOptionToggleableEnabled;
                    PIDOptionEditSettingsState();
                    // We don't actually want to change state, should remove the PID_ENABLE_EDIT state later
                    _state.settingsState = State::SettingsState::PID_ENABLE_DISPLAYED;
                    break;
                case State::SettingsState::PID_ENABLE_EDIT:
                    // PIDOptionEditSettingsState();
                    break;
                case State::SettingsState::PID_DESIRED_RPM_DISPLAYED:
                    PIDDesiredRPMOptionEditSettingsState(true);
                    break;
                case State::SettingsState::PID_DESIRED_RPM_EDIT:
                    _state.desiredRpmUnitsState = static_cast<State::DesiredRpmUnitsState>((static_cast<int>(_state.desiredRpmUnitsState) + 1) % static_cast<int>(State::DesiredRpmUnitsState::NUM_STATES));
                    // This will occur if it loops back to State of 0
                    if (static_cast<int>(_state.desiredRpmUnitsState) != 0) {
                        PIDDesiredRPMOptionEditSettingsState(false);
                    }
                    else
                    {
                        PIDDesiredRPMOptionDisplayedSettingsState();
                    }

                    break;
                default:
                    break;
            }
            break;
        case State::MainDynoState::IN_SESSION:
            if (_pidOptionToggleableEnabled)
            {
                _pidEnabled = !_pidEnabled;
            }
            else
            {
                _throttleControlModeEnabled = !_throttleControlModeEnabled;
            }
            break;
    }
}


void FSM::HandleButtonBrakeInput(bool isEnabled)
{
     if (isEnabled)
     {
         _inSession = true;
         InSessionState();
     }
     else
     {
         _inSession = false;
         IdleState();
     }
    
    // switch(_state.mainState)
    // {
    //     case State::MainDynoState::IDLE:
    //         break;
    //     case State::MainDynoState::SETTINGS_MENU:
    //         _inSession = true;
    //         DisplayInSessionScreen();
    //         break;
    //     case State::MainDynoState::IN_SESSION:
    //         break;
    // }
    
}

int FSM::ConvertDesiredRpmUnitsStateToIncrement()
{
    switch (_state.desiredRpmUnitsState)
    {
        case State::DesiredRpmUnitsState::TEN_THOUSAND:
            return 10000;
        case State::DesiredRpmUnitsState::THOUSAND:
            return 1000;
        case State::DesiredRpmUnitsState::HUNDRED:
            return 100;
        case State::DesiredRpmUnitsState::TEN:
            return 10;
        case State::DesiredRpmUnitsState::ONE:
            return 1;
        default:
            return 0;
    }
}

void FSM::ConvertUserInputIntoDesiredRpm(bool positiveTick)
{
    int increment = ConvertDesiredRpmUnitsStateToIncrement();

    if (!positiveTick)
    {
        increment *= -1;
    }

    _desiredRpm = std::max(0, _desiredRpm + increment);

}

void FSM::ClearDisplay()
{

    AddToLumexLCDMessageQueue(CLEAR_DISPLAY, 0, 0, NULL, 0);

}

void FSM::IdleState()
{
    _state.mainState = State::MainDynoState::IDLE;

    ClearDisplay();
    
    AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 0, 6, "DYNO", sizeof("DYNO") - 1);
    AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 1, 2, "PRESS SELECT", sizeof("PRESS SELECT") - 1);
}

void FSM::SDLoggingOptionDisplayedSettingsState()
{
    _state.mainState = State::MainDynoState::SETTINGS_MENU;
    _state.settingsState = State::SettingsState::SD_LOGGING_OPTION_DISPLAYED;

    ClearDisplay();

    AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 0, 3, "SD LOGGING", sizeof("SD LOGGING") - 1);
    
    if (_sdLoggingEnabled)
        AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 1, 4, "ENABLED", sizeof("ENABLED") - 1);
    else
        AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 1, 4, "DISABLED", sizeof("DISABLED") - 1);
}

void FSM::SDLoggingOptionEditSettingsState()
{
    _state.mainState = State::MainDynoState::SETTINGS_MENU;
    _state.settingsState = State::SettingsState::SD_LOGGING_OPTION_EDIT;

    ClearDisplay();
    
    AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 0, 3, "SD LOGGING", sizeof("SD LOGGING") - 1);

    if (_sdLoggingEnabled)
        AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 1, 4, "ENABLED", sizeof("ENABLED") - 1);
    else
        AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 1, 4, "DISABLED", sizeof("DISABLED") - 1);
}

void FSM::PIDOptionDisplayedSettingsState()
{
    _state.mainState = State::MainDynoState::SETTINGS_MENU;
    _state.settingsState = State::SettingsState::PID_ENABLE_DISPLAYED;

    ClearDisplay();
    
    AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 0, 2, "PID LOGGING", sizeof("PID LOGGING") - 1);

    if (_pidOptionToggleableEnabled)
        AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 1, 4, "ENABLED", sizeof("ENABLED") - 1);
    else
        AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 1, 4, "DISABLED", sizeof("DISABLED") - 1);
}

void FSM::PIDOptionEditSettingsState()
{
    _state.mainState = State::MainDynoState::SETTINGS_MENU;
    _state.settingsState = State::SettingsState::PID_ENABLE_EDIT;

    ClearDisplay();
    
    AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 0, 2, "PID LOGGING", sizeof("PID LOGGING") - 1);

    if (_pidOptionToggleableEnabled)
        AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 1, 4, "ENABLED", sizeof("ENABLED") - 1);
    else
        AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 1, 4, "DISABLED", sizeof("DISABLED") - 1);
}

void FSM::PIDDesiredRPMOptionDisplayedSettingsState()
{
    _state.mainState = State::MainDynoState::SETTINGS_MENU;
    _state.settingsState = State::SettingsState::PID_DESIRED_RPM_DISPLAYED;
    _state.desiredRpmUnitsState = State::DesiredRpmUnitsState::INIT_STATE;

    ClearDisplay();

    AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY,  0, 2, "PID DES RPM", sizeof("PID DES RPM") - 1);

    char buffer[6];
    snprintf(buffer, sizeof(buffer), "%5d", static_cast<int>(_desiredRpm));

    AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 1, 5, buffer, sizeof(buffer) - 1);

}

void FSM::PIDDesiredRPMOptionEditSettingsState(bool clearDisplay)
{
    _state.mainState = State::MainDynoState::SETTINGS_MENU;
    _state.settingsState = State::SettingsState::PID_DESIRED_RPM_EDIT;

    if (clearDisplay) ClearDisplay();

    AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 0, 2, "PID DES RPM", sizeof("PID DES RPM") - 1);

    char buffer[12];
    snprintf(buffer, sizeof(buffer), "%5d %5d", static_cast<int>(_desiredRpm), ConvertDesiredRpmUnitsStateToIncrement());

    AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 1, 2, buffer, sizeof(buffer) - 1);
}



void FSM::InSessionState()
{
    _state.mainState = State::MainDynoState::IN_SESSION;

    _desiredManualBpmDutyCycle = 0.0f;
    _desiredManualThrottleDutyCycle = 0.0f;

    ClearDisplay();
    
    // The actual data will be managed in the session controller
    // Two measured quantities and the drive mode. Torque and power used to sit here, but the
    // device no longer derives them -- the host does, from these same measurements.
    //          col: 0123456789012345
    AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 0, 0, "n:     0 rpm    ", sizeof("n:     0 rpm    ") - 1);
    AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 1, 0, "F:    0.00 N    ", sizeof("F:    0.00 N    ") - 1);
}


void FSM::DisplayRpm(float rpm)
{
    char buf[6]; // Enough for 32-bit integers
    uint32_t value = static_cast<uint32_t>(std::round(rpm));
    snprintf(buf, sizeof(buf), "%5lu", value);

    AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 0, 3, buf, sizeof(buf) - 1);
}

void FSM::DisplayForce(float force)
{
    char buf[7]; // Enough for force with 2 decimals
    float value = std::round(force * 100.0) / 100.0;
    snprintf(buf, sizeof(buf), "%6.2f", value);

    // %6.2f is 6 chars wide, at cols 2-7: clear of the "F:" label and of the drive-mode
    // field at col 12, so neither can be overwritten however large the reading gets.
    AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 1, 2, buf, sizeof(buf) - 1);
}

void FSM::DisplayPIDEnabled()
{
    const char *pid_str = _pidEnabled ? "PIDE" : "PIDD";

    AddToLumexLCDMessageQueue(
        WRITE_TO_DISPLAY,
        1,
        12,
        pid_str,
        4
    );

}

void FSM::DisplayManualBPMDutyCycle()
{
    char buf[5]; // Enough for duty cycle with no decimals
    uint8_t value = std::round(_desiredManualBpmDutyCycle * 100.0);
    snprintf(buf, sizeof(buf), "B%3u", value);

    AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 1, 12, buf, sizeof(buf) - 1);
}

void FSM::DisplayManualThrottleDutyCycle()
{  
    char buf[5]; // Enough for duty cycle with no decimals
    uint8_t value = std::round(_desiredManualThrottleDutyCycle * 100.0);
    snprintf(buf, sizeof(buf), "T%3u", value);

    AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, 1, 12, buf, sizeof(buf) - 1);
}



void FSM::AddToLumexLCDMessageQueue(session_controller_to_lumex_lcd_opcode opcode, uint8_t row, uint8_t column, const char* display_string, size_t size)
{

	session_controller_to_lumex_lcd msg;
    msg.op = opcode;
    msg.row = row;
    msg.column = column;
    msg.size = size;

    strncpy(msg.display_string, display_string, sizeof(msg.display_string) - 1);
    msg.display_string[sizeof(msg.display_string) - 1] = '\0'; // Ensure null termination

    osMessageQueuePut(_sessionControllerToLumexLcdHandle, &msg, 0, 0);
}

State FSM::GetState() const 
{
    return _state;
}

bool FSM::GetSDLoggingEnabledStatus() const
{
    return _sdLoggingEnabled;
}

bool FSM::GetPIDEnabledModeStatus() const
{
    return _pidOptionToggleableEnabled && _pidEnabled;
}

bool FSM::GetPIDOptionToggleableEnabledStatus() const
{
    return _pidOptionToggleableEnabled;
}

bool FSM::GetManualBpmModeStatus() const
{
    return !_throttleControlModeEnabled;
}

bool FSM::GetManualThrottleModeStatus() const
{
    return _throttleControlModeEnabled;
}


bool FSM::GetInSessionStatus() const
{
    return _state.mainState == State::MainDynoState::IN_SESSION;
}

float FSM::GetDesiredBpmDutyCycle() const
{
    return _desiredManualBpmDutyCycle;
}

float FSM::GetDesiredThrottleDutyCycle() const
{
    return _desiredManualThrottleDutyCycle;
}

float FSM::GetDesiredRpm() const
{
    return _desiredRpm;
}

float FSM::GetDesiredAngularVelocity() const
{
    return _desiredRpm * 2 * M_PI / 60;
}
