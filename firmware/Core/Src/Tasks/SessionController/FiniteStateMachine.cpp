#include "Tasks/SessionController/FiniteStateMachine.hpp"

#include "Config/sysconfig.h"

#include "TimeKeeping/timestamps.h"

FSM::FSM(osMessageQueueId_t sessionControllerToDisplayHandle) :
        _toDisplayHandle(sessionControllerToDisplayHandle),
        _state{
            State::MainDynoState::INIT_STATE,
            State::SettingsState::INIT_STATE,
            State::DesiredRpmUnitsState::INIT_STATE,
          },
        _pidEnabled(false),
        _desiredManualBpmDutyCycle(0),
        _rpm(0.0f),
        _force(0.0f),
        _angularAcceleration(0.0f),
        _peakForce(0.0f),
        _sessionStartTimestamp(0),
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
            // No explicit clear: the driver clears whenever the screen id changes, and its
            // "nothing rendered yet" state counts as a change, so coming up on the idle
            // screen still starts from a blank panel.
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
        // The two pages form a ring, so a tick lands on the other one whichever way it turned.
        // Direction starts mattering again the moment a third page is added.
        case State::SettingsState::PID_ENABLE_DISPLAYED:
            ShowDesiredRpmPage();
            break;

        case State::SettingsState::PID_DESIRED_RPM_DISPLAYED:
            ShowPidEnablePage();
            break;

        // Inside the editor a tick changes the digit under the cursor rather than the page.
        case State::SettingsState::PID_DESIRED_RPM_EDIT:
            AdjustDesiredRpm(positiveTick);
            ShowDesiredRpmEditor();
            break;

        // Unreachable -- the toggle setting has no edit screen (see State::SettingsState).
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
        case State::SettingsState::PID_ENABLE_DISPLAYED:
        case State::SettingsState::PID_DESIRED_RPM_DISPLAYED:
            ShowIdleScreen();
            break;

        // In the editor, BACK walks the digit cursor left; off the left end it leaves the editor.
        case State::SettingsState::PID_DESIRED_RPM_EDIT:
            if (StepDesiredRpmDigit(-1)) ShowDesiredRpmPage();
            else ShowDesiredRpmEditor();
            break;

        // Unreachable -- the toggle setting has no edit screen (see State::SettingsState).
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
            ShowPidEnablePage();  // opens the settings menu on its first page
            break;

        case State::MainDynoState::SETTINGS_MENU:
            HandleButtonSelectInSettings();
            break;

        case State::MainDynoState::IN_SESSION:
            // SELECT arms and disarms the PID loop, and only when the menu option allows it.
            // With the option off there is nothing to switch: the brake is the only actuator
            // the encoder drives.
            if (GetPIDOptionToggleableEnabledStatus()) _pidEnabled = !_pidEnabled;
            break;
    }
}

void FSM::HandleButtonSelectInSettings()
{
    switch (_state.settingsState)
    {
        // A toggle is applied and redrawn on the page itself; there is no edit screen to enter.
        case State::SettingsState::PID_ENABLE_DISPLAYED:
            sysconfig_set_raw(SYSCFG_PID_ENABLE,
                              GetPIDOptionToggleableEnabledStatus() ? 0u : 1u);
            ShowPidEnablePage();
            break;

        case State::SettingsState::PID_DESIRED_RPM_DISPLAYED:
            ShowDesiredRpmEditor();
            break;

        // In the editor, SELECT walks the digit cursor right; off the right end it leaves.
        case State::SettingsState::PID_DESIRED_RPM_EDIT:
            if (StepDesiredRpmDigit(+1)) ShowDesiredRpmPage();
            else ShowDesiredRpmEditor();
            break;

        // Unreachable -- the toggle setting has no edit screen (see State::SettingsState).
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

        // A press while a session is already running is not a request to start one -- it is a
        // second edge from a bouncing contact, or noise coupled into the line. ShowSessionScreen
        // is destructive (it zeroes the commanded duty cycle, wipes the peak force and restarts
        // the session clock), so re-entering it mid-run drops the brake to 0% under the user's
        // hand. Ignore it: only a real IDLE -> IN_SESSION transition may reset those.
        if (_state.mainState == State::MainDynoState::IN_SESSION)
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

// The host's equivalent of turning the brake knob. Deliberately routed through the same state
// the encoder writes, so everything downstream -- the clamp, the BPM post, the on-screen
// readout -- behaves identically whether the request came from the rig or from the PC.
bool FSM::SetHostBrakeDutyCycle(float dutyCycle)
{
    if (_state.mainState != State::MainDynoState::IN_SESSION)
    {
        return false;
    }

    float minDutyCycle;
    float maxDutyCycle;
    sysconfig_get_duty_cycle_limits(&minDutyCycle, &maxDutyCycle);

    _desiredManualBpmDutyCycle = std::clamp(dutyCycle, minDutyCycle, maxDutyCycle);

    PostDisplayState();

    return true;
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
    const int candidate = static_cast<int>(GetDesiredRpm())
                          + (positiveTick ? increment : -increment);

    // A candidate outside the accepted range is simply not applied, so the value stops at
    // either end rather than wrapping -- which is what the old std::max(0, ...) did at the
    // bottom. The range itself is deliberately not repeated here: sysconfig_set_raw rejects
    // anything outside the store's own bounds for this id, so the editor and a host write
    // are held to the same limits without a second copy of them to drift. The negative case
    // is caught before the cast, which would otherwise turn -1 into 4294967295.
    if (candidate < 0) return;

    sysconfig_set_raw(SYSCFG_PID_DESIRED_RPM, static_cast<uint32_t>(candidate));
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

    PostDisplayState();
}

void FSM::ShowPidEnablePage()
{
    _state.mainState = State::MainDynoState::SETTINGS_MENU;
    _state.settingsState = State::SettingsState::PID_ENABLE_DISPLAYED;

    PostDisplayState();
}

void FSM::ShowDesiredRpmPage()
{
    _state.mainState = State::MainDynoState::SETTINGS_MENU;
    _state.settingsState = State::SettingsState::PID_DESIRED_RPM_DISPLAYED;
    _state.desiredRpmUnitsState = State::DesiredRpmUnitsState::INIT_STATE;

    PostDisplayState();
}

// The same page with the cursor's step size shown alongside the value, so the user can see
// which digit a tick will move. It no longer takes a "clear first" flag: entering from the
// display page is a change of screen id and the driver clears on that by itself, while a
// redraw mid-edit is not and so does not clear -- exactly the old distinction, but derived
// rather than passed in.
void FSM::ShowDesiredRpmEditor()
{
    _state.mainState = State::MainDynoState::SETTINGS_MENU;
    _state.settingsState = State::SettingsState::PID_DESIRED_RPM_EDIT;

    PostDisplayState();
}

void FSM::ShowSessionScreen()
{
    _state.mainState = State::MainDynoState::IN_SESSION;

    // Reset the per-session figures on the way in, so a new run does not inherit the last
    // one's peak or clock.
    _peakForce = 0.0f;
    _sessionStartTimestamp = get_timestamp();

    // Deliberately 0 rather than the envelope's floor: nothing has been commanded yet, and the
    // SessionController sends START_PWM the moment this value differs from what it last sent --
    // so starting at a non-zero floor would engage the brake on session entry, unasked. The
    // first encoder tick moves into the envelope.
    _desiredManualBpmDutyCycle = 0.0f;

    // Every session starts disarmed, for the same reason the brake starts at 0: arming is a
    // decision made during a run, and this one had not been made yet. It used to persist --
    // nothing ever cleared it -- so a session that ended with the loop armed left it armed, and
    // the next one came up already driving the brake from the controller without a SELECT.
    _pidEnabled = false;

    PostDisplayState();
}


// ---------------------------------------------------------------------------- display fields

// Each of these records a value and reposts everything. The SessionController already calls
// them only when its reading has moved, and the driver diffs again on its side, so reposting
// the whole state costs one queue message and no panel traffic.

// Takes rad/s, because that is what the optical encoder measures and what every other consumer
// of that reading wants. The panel is the only place RPM is the right unit, so the conversion
// happens here, once, rather than in each display driver.
void FSM::DisplayAngularVelocity(float angularVelocity)
{
    _rpm = encoder_rpm(angularVelocity);
    PostDisplayState();
}

void FSM::DisplayForce(float force)
{
    _force = force;

    // Track the peak by magnitude: the rig is loaded whichever way the cell is driven.
    const float magnitude = (force < 0.0f) ? -force : force;
    if (magnitude > _peakForce)
    {
        _peakForce = magnitude;
    }

    PostDisplayState();
}

void FSM::DisplayAngularAcceleration(float angularAcceleration)
{
    _angularAcceleration = angularAcceleration;
    PostDisplayState();
}

void FSM::DisplayPIDEnabled()
{
    PostDisplayState();
}

void FSM::DisplayManualBPMDutyCycle()
{
    PostDisplayState();
}

display_screen_id FSM::CurrentScreen() const
{
    switch (_state.mainState)
    {
        case State::MainDynoState::IN_SESSION:
            return DISPLAY_SCREEN_SESSION;

        case State::MainDynoState::SETTINGS_MENU:
            switch (_state.settingsState)
            {
                case State::SettingsState::PID_DESIRED_RPM_DISPLAYED:
                    return DISPLAY_SCREEN_DESIRED_RPM;
                case State::SettingsState::PID_DESIRED_RPM_EDIT:
                    return DISPLAY_SCREEN_DESIRED_RPM_EDIT;
                // PID_ENABLE_DISPLAYED, plus the edit state nothing ever enters.
                default:
                    return DISPLAY_SCREEN_PID_ENABLE;
            }

        case State::MainDynoState::IDLE:
        default:
            return DISPLAY_SCREEN_IDLE;
    }
}

void FSM::PostDisplayState()
{
    session_controller_to_display msg;
    memset(&msg, 0, sizeof(msg));

    msg.screen                = CurrentScreen();
    msg.rpm                   = _rpm;
    msg.force                 = _force;
    msg.bpm_duty_cycle        = _desiredManualBpmDutyCycle;
    msg.desired_rpm           = GetDesiredRpm();
    msg.cursor_digit          = static_cast<display_rpm_digit>(_state.desiredRpmUnitsState);
    msg.pid_enabled           = _pidEnabled;
    msg.pid_option_toggleable = GetPIDOptionToggleableEnabledStatus();
    msg.angular_acceleration  = _angularAcceleration;
    msg.peak_force            = _peakForce;

    // Elapsed seconds, from the microsecond timestamp counter the rest of the board stamps
    // samples with. Zero outside a session: _sessionStartTimestamp is only set on entry.
    const uint32_t scale = get_timestamp_scale();
    msg.session_seconds = (_sessionStartTimestamp != 0 && scale != 0)
                          ? (get_timestamp() - _sessionStartTimestamp) / scale
                          : 0;

    osMessageQueuePut(_toDisplayHandle, &msg, 0, 0);
}


// ---------------------------------------------------------------------------- getters

State FSM::GetState() const
{
    return _state;
}

// The PID loop runs only when the menu option allows it and it has been switched on in-session.
bool FSM::GetPIDEnabledModeStatus() const
{
    return GetPIDOptionToggleableEnabledStatus() && _pidEnabled;
}

// Read straight out of the sysconfig store on every call rather than cached: the host writes
// this id over USB as well as the menu writing it, and a copy here is what would go stale.
bool FSM::GetPIDOptionToggleableEnabledStatus() const
{
    return sysconfig_get_u32(SYSCFG_PID_ENABLE) != 0u;
}

bool FSM::GetInSessionStatus() const
{
    return _state.mainState == State::MainDynoState::IN_SESSION;
}

float FSM::GetDesiredBpmDutyCycle() const
{
    return _desiredManualBpmDutyCycle;
}

uint32_t FSM::GetDesiredRpm() const
{
    return sysconfig_get_u32(SYSCFG_PID_DESIRED_RPM);
}

// RPM is what the panel and the host talk in; rad/s is what the encoder measures and what the
// PID task compares against, so the conversion happens once, here, on the way out.
float FSM::GetDesiredAngularVelocity() const
{
    return static_cast<float>(GetDesiredRpm()) * 2.0f * static_cast<float>(M_PI) / 60.0f;
}
