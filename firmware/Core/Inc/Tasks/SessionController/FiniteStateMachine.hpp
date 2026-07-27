#ifndef INC_TASKS_SESSION_CONTROLLER_FINITESTATEMACHINE_HPP_
#define INC_TASKS_SESSION_CONTROLLER_FINITESTATEMACHINE_HPP_

#include <algorithm>

#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>

#include "cmsis_os2.h"

#include "MessagePassing/messages_private.h"

#include "input_manager_interrupts.h"

// Where the user interface is.
//
//   IDLE ---------------- SELECT ---------------> SETTINGS_MENU
//     ^<------------------ BACK -------------------/
//
//   IN_SESSION is entered from anywhere by holding the brake button and left by releasing it;
//   it is the only state in which the board drives anything or streams over USB.
//
// SETTINGS_MENU has its own ring of pages, walked with the rotary encoder:
//
//   SD_LOGGING_OPTION_DISPLAYED <-> PID_ENABLE_DISPLAYED <-> PID_DESIRED_RPM_DISPLAYED <-> (wraps)
//
// SELECT on the two toggle pages flips the setting and redraws in place. SELECT on the
// desired-RPM page opens PID_DESIRED_RPM_EDIT, where a cursor (DesiredRpmUnitsState) picks
// which decimal digit the encoder changes; walking the cursor off either end leaves the editor.
struct State
{
    enum class MainDynoState
    {
        INIT_STATE = 0,
        IDLE = 0,
        SETTINGS_MENU,
        IN_SESSION
    };

    enum class SettingsState
    {
        INIT_STATE = 0,
        SD_LOGGING_OPTION_DISPLAYED = 0,
        // Nothing ever enters SD_LOGGING_OPTION_EDIT or PID_ENABLE_EDIT: a toggle is applied on
        // the display page itself, so those two settings have no edit screen. They are kept only
        // so the enumerators below hold their values.
        SD_LOGGING_OPTION_EDIT,
        PID_ENABLE_DISPLAYED,
        PID_ENABLE_EDIT,
        PID_DESIRED_RPM_DISPLAYED,
        PID_DESIRED_RPM_EDIT
    };

    // Which decimal digit of the desired RPM the encoder currently edits.
    enum class DesiredRpmUnitsState
    {
        INIT_STATE = 0,
        TEN_THOUSAND = 0,
        THOUSAND,
        HUNDRED,
        TEN,
        ONE,
        NUM_STATES
    };

    MainDynoState mainState;
    SettingsState settingsState;
    DesiredRpmUnitsState desiredRpmUnitsState;
};


class FSM
{
public:
    FSM(osMessageQueueId_t sessionControllerToDisplayHandle);

    // Drains everything the input ISRs have queued since the last call and applies it.
    void HandleUserInputs();

    // Fields the SessionController refreshes on the in-session screen. Each records the value
    // and reposts the whole screen state; the driver works out what actually moved.
    void DisplayRpm(float rpm);
    void DisplayForce(float force);
    void DisplayPIDEnabled();
    void DisplayManualBPMDutyCycle();

    // What the SessionController acts on
    State GetState() const;
    bool GetSDLoggingEnabledStatus() const;
    bool GetPIDEnabledModeStatus() const;
    bool GetPIDOptionToggleableEnabledStatus() const;

    bool GetInSessionStatus() const;

    float GetDesiredBpmDutyCycle() const;

    float GetDesiredRpm() const;
    float GetDesiredAngularVelocity() const;

private:
    // --- Screens. Each sets the state it represents and reposts it.
    void ShowIdleScreen();
    void ShowSdLoggingPage();
    void ShowPidEnablePage();
    void ShowDesiredRpmPage();
    void ShowDesiredRpmEditor();
    void ShowSessionScreen();

    // --- One handler per input, dispatched from HandleUserInputs.
    void HandleRotaryEncoderInput(bool positiveTick);
    void HandleRotaryEncoderSwInput();
    void HandleButtonBackInput();
    void HandleButtonSelectInput();
    void HandleButtonBrakeInput(bool isEnabled);

    // --- The settings menu's share of those handlers.
    void HandleRotaryEncoderInSettings(bool positiveTick);
    void HandleButtonBackInSettings();
    void HandleButtonSelectInSettings();

    // --- Editing values
    void AdjustBrakeDutyCycle(bool positiveTick);
    void AdjustDesiredRpm(bool positiveTick);
    int DesiredRpmDigitIncrement() const;
    bool StepDesiredRpmDigit(int direction);

    // Posts the whole of what is on screen: which screen, and every value any screen shows.
    // The FSM no longer formats anything -- a 16x2 character LCD and a 320x240 TFT want
    // completely different layouts of the same facts, so laying out is the driver's job and
    // this is the seam between them. Which panel is listening is a compile-time choice.
    void PostDisplayState();

    // Maps the FSM's own state pair onto the screen id the drivers switch on.
    display_screen_id CurrentScreen() const;

    osMessageQueueId_t _toDisplayHandle;

    State _state;

    // Settings, edited from the menu.
    bool _sdLoggingEnabled;
    bool _pidOptionToggleableEnabled;
    int _desiredRpm;

    // Session state. Whether a session is running is _state.mainState and nothing else --
    // see GetInSessionStatus.
    bool _pidEnabled;
    float _desiredManualBpmDutyCycle;

    // Newest readings the SessionController has handed over. Held because every post carries
    // the whole screen state, so a force update still has to say what the RPM is.
    float _angularVelocity;
    float _force;

    // Whether a brake press may start a session. Cleared when the button is already held as this
    // FSM comes up, and set again by the release that follows -- see HandleButtonBrakeInput.
    bool _brakeArmed;

    // How far this FSM has drained the input ISRs' circular buffer.
    uint32_t _fsmInputDataIndex;
};


#endif // INC_TASKS_SESSION_CONTROLLER_FINITESTATEMACHINE_HPP_
