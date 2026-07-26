#include "Tasks/SessionController/FiniteStateMachine.hpp"

#include "Config/sysconfig.h"

FSM::FSM(osMessageQueueId_t sessionControllerToLumexLcdHandle) :
        _sessionControllerToLumexLcdHandle(sessionControllerToLumexLcdHandle),
        _state{
            State::MainDynoState::INIT_STATE,
            State::SettingsState::INIT_STATE,
            State::DesiredRpmUnitsState::INIT_STATE,
          },
        _sdLoggingEnabled(false),
        _pidOptionToggleableEnabled(false),
        _desiredRpm(5000),
        _pidEnabled(false),
        _desiredManualBpmDutyCycle(0),
        // A brake already held as we come up is not a request to start a session -- it is just how
        // the board was left, or a finger on the button during a reset. Start disarmed in that case
        // so nothing can run until the button has been released and pressed deliberately.
        _brakeArmed(HAL_GPIO_ReadPin(BTN_BRAKE_GPIO_Port, BTN_BRAKE_Pin) == GPIO_PIN_RESET),
        // Start where the ISRs have already got to, rather than at 0. The button interrupts are
        // live from MX_GPIO_Init, which is well before the kernel starts and this FSM exists, so
        // anything already in the buffer happened during boot -- including, on a board reset with
        // the brake held, an edge latched before the NVIC was even enabled. None of it is input to
        // a UI that was not yet on screen, and replaying it would act on it.
        _fsmInputDataIndex(interrupt_input_data_index)
        {
            ClearDisplay();
            ShowIdleScreen();
        }


// ---------------------------------------------------------------------------- input dispatch

// The ISRs append events and advance interrupt_input_data_index; this drains everything that
// arrived since the last pass, in order, in task context.
void FSM::HandleUserInputs(void)
{
    while (_fsmInputDataIndex != interrupt_input_data_index)
    {
        volatile button_press_data* input = get_circular_buffer_data(_fsmInputDataIndex);

        switch (input->opcode)
        {
            case ROT_EN_TICKS: HandleRotaryEncoderInput(input->positive); break;
            case ROT_EN_SW:    HandleRotaryEncoderSwInput();              break;
            case BTN_BACK:     HandleButtonBackInput();                   break;
            case BTN_SELECT:   HandleButtonSelectInput();                 break;
            case BTN_BRAKE:    HandleButtonBrakeInput(input->positive);   break;
            default:                                                      break;
        }

        _fsmInputDataIndex = (_fsmInputDataIndex + 1) % USER_INPUT_CIRCULAR_BUFFER_SIZE;
    }
}


// ---------------------------------------------------------------------------- rotary encoder

void FSM::HandleRotaryEncoderInput(bool positiveTick)
{
    switch (_state.mainState)
    {
        case State::MainDynoState::IDLE:
            break;

        case State::MainDynoState::SETTINGS_MENU:
            HandleRotaryEncoderInSettings(positiveTick);
            break;

        case State::MainDynoState::IN_SESSION:
            AdjustBrakeDutyCycle(positiveTick);
            break;
    }
}

void FSM::HandleRotaryEncoderInSettings(bool positiveTick)
{
    switch (_state.settingsState)
    {
        // The three pages form a ring; a tick steps one page along it in the tick's direction.
        case State::SettingsState::SD_LOGGING_OPTION_DISPLAYED:
            if (positiveTick) ShowPidEnablePage();
            else ShowDesiredRpmPage();
            break;

        case State::SettingsState::PID_ENABLE_DISPLAYED:
            if (positiveTick) ShowDesiredRpmPage();
            else ShowSdLoggingPage();
            break;

        case State::SettingsState::PID_DESIRED_RPM_DISPLAYED:
            if (positiveTick) ShowSdLoggingPage();
            else ShowPidEnablePage();
            break;

        // Inside the editor a tick changes the digit under the cursor rather than the page.
        case State::SettingsState::PID_DESIRED_RPM_EDIT:
            AdjustDesiredRpm(positiveTick);
            ShowDesiredRpmEditor(false);
            break;

        // Unreachable -- the toggle settings have no edit screen (see State::SettingsState).
        case State::SettingsState::SD_LOGGING_OPTION_EDIT:
        case State::SettingsState::PID_ENABLE_EDIT:
        default:
            break;
    }
}

// The encoder's push switch does nothing yet. It was wired up to start a session, which did not
// work reliably, and the brake button does that job instead.
void FSM::HandleRotaryEncoderSwInput(void)
{
}


// ---------------------------------------------------------------------------- back / select

void FSM::HandleButtonBackInput(void)
{
    // BACK is inert on the idle screen and during a session; only the menu has somewhere to go.
    if (_state.mainState == State::MainDynoState::SETTINGS_MENU)
    {
        HandleButtonBackInSettings();
    }
}

void FSM::HandleButtonBackInSettings()
{
    switch (_state.settingsState)
    {
        // From any settings page, BACK leaves the menu.
        case State::SettingsState::SD_LOGGING_OPTION_DISPLAYED:
        case State::SettingsState::PID_ENABLE_DISPLAYED:
        case State::SettingsState::PID_DESIRED_RPM_DISPLAYED:
            ShowIdleScreen();
            break;

        // In the editor, BACK walks the digit cursor left; off the left end it leaves the editor.
        case State::SettingsState::PID_DESIRED_RPM_EDIT:
            if (StepDesiredRpmDigit(-1)) ShowDesiredRpmPage();
            else ShowDesiredRpmEditor(false);
            break;

        // Unreachable -- the toggle settings have no edit screen (see State::SettingsState).
        case State::SettingsState::SD_LOGGING_OPTION_EDIT:
        case State::SettingsState::PID_ENABLE_EDIT:
        default:
            break;
    }
}

void FSM::HandleButtonSelectInput(void)
{
    switch (_state.mainState)
    {
        case State::MainDynoState::IDLE:
            ShowSdLoggingPage();  // opens the settings menu on its first page
            break;

        case State::MainDynoState::SETTINGS_MENU:
            HandleButtonSelectInSettings();
            break;

        case State::MainDynoState::IN_SESSION:
            // SELECT arms and disarms the PID loop, and only when the menu option allows it.
            // With the option off there is nothing to switch: the brake is the only actuator
            // the encoder drives.
            if (_pidOptionToggleableEnabled) _pidEnabled = !_pidEnabled;
            break;
    }
}

void FSM::HandleButtonSelectInSettings()
{
    switch (_state.settingsState)
    {
        // A toggle is applied and redrawn on the page itself; there is no edit screen to enter.
        case State::SettingsState::SD_LOGGING_OPTION_DISPLAYED:
            _sdLoggingEnabled = !_sdLoggingEnabled;
            ShowSdLoggingPage();
            break;

        case State::SettingsState::PID_ENABLE_DISPLAYED:
            _pidOptionToggleableEnabled = !_pidOptionToggleableEnabled;
            ShowPidEnablePage();
            break;

        case State::SettingsState::PID_DESIRED_RPM_DISPLAYED:
            ShowDesiredRpmEditor(true);
            break;

        // In the editor, SELECT walks the digit cursor right; off the right end it leaves.
        case State::SettingsState::PID_DESIRED_RPM_EDIT:
            if (StepDesiredRpmDigit(+1)) ShowDesiredRpmPage();
            else ShowDesiredRpmEditor(false);
            break;

        // Unreachable -- the toggle settings have no edit screen (see State::SettingsState).
        case State::SettingsState::SD_LOGGING_OPTION_EDIT:
        case State::SettingsState::PID_ENABLE_EDIT:
        default:
            break;
    }
}


// ---------------------------------------------------------------------------- brake

// The brake button is the session switch: held means running, released means stopped, from
// whichever screen the UI happens to be on.
void FSM::HandleButtonBrakeInput(bool isEnabled)
{
    if (isEnabled)
    {
        // Disarmed means this press is the one that was already being made when the board came
        // up, so it is not a decision to start a session -- and starting one here would drive the
        // BPM the moment power returned, with nobody having asked for it since the reset. The
        // release below is what turns the button back into a control.
        if (!_brakeArmed)
        {
            return;
        }
        ShowSessionScreen();
    }
    else
    {
        // Released: whatever was held through the reset has been let go, so the next press is a
        // deliberate one and is allowed to start a session.
        _brakeArmed = true;
        ShowIdleScreen();
    }
}


// ---------------------------------------------------------------------------- value editing

void FSM::AdjustBrakeDutyCycle(bool positiveTick)
{
    const float increment = positiveTick ? 0.01f : -0.01f;

    // Stop the brake knob where the BPM task stops. BPM::SetDutyCycle clamps every request to
    // this same envelope, so a UI that ran to 1.0 spent its last few ticks changing the number
    // on screen and nothing else -- the LCD read 100 against a ceiling of 95. Both sides read
    // the pair through sysconfig_get_duty_cycle_limits, which also orders it: a host may leave
    // min above max, and std::clamp is undefined if its bounds are crossed.
    float minDutyCycle;
    float maxDutyCycle;
    sysconfig_get_duty_cycle_limits(&minDutyCycle, &maxDutyCycle);

    _desiredManualBpmDutyCycle =
        std::clamp(_desiredManualBpmDutyCycle + increment, minDutyCycle, maxDutyCycle);
}

// How much one encoder tick moves the desired RPM, given which digit the cursor is on.
int FSM::DesiredRpmDigitIncrement() const
{
    switch (_state.desiredRpmUnitsState)
    {
        case State::DesiredRpmUnitsState::TEN_THOUSAND: return 10000;
        case State::DesiredRpmUnitsState::THOUSAND:     return 1000;
        case State::DesiredRpmUnitsState::HUNDRED:      return 100;
        case State::DesiredRpmUnitsState::TEN:          return 10;
        case State::DesiredRpmUnitsState::ONE:          return 1;
        default:                                        return 0;
    }
}

void FSM::AdjustDesiredRpm(bool positiveTick)
{
    const int increment = DesiredRpmDigitIncrement();

    _desiredRpm = std::max(0, _desiredRpm + (positiveTick ? increment : -increment));
}

// Moves the digit cursor by one place. Returns true when it wrapped past an end of the number,
// which is how the editor is left -- SELECT walks off the last digit, BACK off the first.
bool FSM::StepDesiredRpmDigit(int direction)
{
    constexpr int digitCount = static_cast<int>(State::DesiredRpmUnitsState::NUM_STATES);

    const int next = (static_cast<int>(_state.desiredRpmUnitsState) + direction + digitCount)
                     % digitCount;

    _state.desiredRpmUnitsState = static_cast<State::DesiredRpmUnitsState>(next);

    return (direction > 0) ? (next == 0) : (next == digitCount - 1);
}


// ---------------------------------------------------------------------------- screens

void FSM::ShowIdleScreen()
{
    _state.mainState = State::MainDynoState::IDLE;

    ClearDisplay();

    WriteText(0, 6, "DYNO");
    WriteText(1, 2, "PRESS SELECT");
}

void FSM::ShowSdLoggingPage()
{
    _state.mainState = State::MainDynoState::SETTINGS_MENU;
    _state.settingsState = State::SettingsState::SD_LOGGING_OPTION_DISPLAYED;

    ClearDisplay();

    WriteText(0, 3, "SD LOGGING");
    ShowEnabledDisabled(_sdLoggingEnabled);
}

void FSM::ShowPidEnablePage()
{
    _state.mainState = State::MainDynoState::SETTINGS_MENU;
    _state.settingsState = State::SettingsState::PID_ENABLE_DISPLAYED;

    ClearDisplay();

    WriteText(0, 2, "PID LOGGING");
    ShowEnabledDisabled(_pidOptionToggleableEnabled);
}

// The second row shared by both toggle pages.
void FSM::ShowEnabledDisabled(bool enabled)
{
    if (enabled) WriteText(1, 4, "ENABLED");
    else WriteText(1, 4, "DISABLED");
}

void FSM::ShowDesiredRpmPage()
{
    _state.mainState = State::MainDynoState::SETTINGS_MENU;
    _state.settingsState = State::SettingsState::PID_DESIRED_RPM_DISPLAYED;
    _state.desiredRpmUnitsState = State::DesiredRpmUnitsState::INIT_STATE;

    ClearDisplay();

    WriteText(0, 2, "PID DES RPM");

    char buffer[6];
    snprintf(buffer, sizeof(buffer), "%5d", _desiredRpm);

    WriteText(1, 5, buffer);
}

// Same page with the cursor's step size alongside the value, so the user can see which digit a
// tick will move. Entering from the display page clears first; redraws while editing do not,
// because the layout does not change.
void FSM::ShowDesiredRpmEditor(bool clearDisplay)
{
    _state.mainState = State::MainDynoState::SETTINGS_MENU;
    _state.settingsState = State::SettingsState::PID_DESIRED_RPM_EDIT;

    if (clearDisplay) ClearDisplay();

    WriteText(0, 2, "PID DES RPM");

    char buffer[12];
    snprintf(buffer, sizeof(buffer), "%5d %5d", _desiredRpm, DesiredRpmDigitIncrement());

    WriteText(1, 2, buffer);
}

void FSM::ShowSessionScreen()
{
    _state.mainState = State::MainDynoState::IN_SESSION;

    // Deliberately 0 rather than the envelope's floor: nothing has been commanded yet, and the
    // SessionController sends START_PWM the moment this value differs from what it last sent --
    // so starting at a non-zero floor would engage the brake on session entry, unasked. The
    // first encoder tick moves into the envelope.
    _desiredManualBpmDutyCycle = 0.0f;

    ClearDisplay();

    // The values are filled in by the SessionController through the Display* methods below.
    // Two measured quantities and the drive mode. Torque and power used to sit here, but the
    // device no longer derives them -- the host does, from these same measurements.
    //        col: 0123456789012345
    WriteText(0, 0, "n:     0 rpm    ");
    WriteText(1, 0, "F:    0.00 N    ");
}


// ---------------------------------------------------------------------------- display fields

void FSM::DisplayRpm(float rpm)
{
    char buf[6];
    uint32_t value = static_cast<uint32_t>(std::round(rpm));
    // uint32_t is unsigned long on this target, but not everywhere the file is compiled.
    snprintf(buf, sizeof(buf), "%5lu", static_cast<unsigned long>(value));

    WriteText(0, 3, buf);
}

void FSM::DisplayForce(float force)
{
    char buf[7];
    float value = std::round(force * 100.0) / 100.0;
    snprintf(buf, sizeof(buf), "%6.2f", value);

    // %6.2f is 6 chars wide, at cols 2-7: clear of the "F:" label and of the drive-mode
    // field at col 12, so neither can be overwritten however large the reading gets.
    WriteText(1, 2, buf);
}

void FSM::DisplayPIDEnabled()
{
    if (_pidEnabled) WriteText(1, 12, "PIDE");
    else WriteText(1, 12, "PIDD");
}

void FSM::DisplayManualBPMDutyCycle()
{
    char buf[5];
    uint8_t value = static_cast<uint8_t>(std::round(_desiredManualBpmDutyCycle * 100.0));
    snprintf(buf, sizeof(buf), "B%3u", value);

    WriteText(1, 12, buf);
}

void FSM::ClearDisplay()
{
    // The LCD task ignores the string for CLEAR_DISPLAY. It is empty rather than null because
    // AddToLumexLCDMessageQueue copies it unconditionally.
    AddToLumexLCDMessageQueue(CLEAR_DISPLAY, 0, 0, "", 0);
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


// ---------------------------------------------------------------------------- getters

State FSM::GetState() const
{
    return _state;
}

bool FSM::GetSDLoggingEnabledStatus() const
{
    return _sdLoggingEnabled;
}

// The PID loop runs only when the menu option allows it and it has been switched on in-session.
bool FSM::GetPIDEnabledModeStatus() const
{
    return _pidOptionToggleableEnabled && _pidEnabled;
}

bool FSM::GetPIDOptionToggleableEnabledStatus() const
{
    return _pidOptionToggleableEnabled;
}

bool FSM::GetInSessionStatus() const
{
    return _state.mainState == State::MainDynoState::IN_SESSION;
}

float FSM::GetDesiredBpmDutyCycle() const
{
    return _desiredManualBpmDutyCycle;
}

float FSM::GetDesiredRpm() const
{
    return _desiredRpm;
}

float FSM::GetDesiredAngularVelocity() const
{
    return _desiredRpm * 2 * M_PI / 60;
}
