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
    FSM(osMessageQueueId_t sessionControllerToLumexLcdHandle);

    // Drains everything the input ISRs have queued since the last call and applies it.
    void HandleUserInputs();

    // Display
    void ClearDisplay();
    void AddToLumexLCDMessageQueue(session_controller_to_lumex_lcd_opcode opcode, uint8_t row, uint8_t column, const char* display_string, size_t size);

    // Fields the SessionController refreshes on the in-session screen.
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
    // --- Screens. Each sets the state it represents and redraws the LCD for it.
    void ShowIdleScreen();
    void ShowSdLoggingPage();
    void ShowPidEnablePage();
    void ShowDesiredRpmPage();
    void ShowDesiredRpmEditor(bool clearDisplay);
    void ShowSessionScreen();
    void ShowEnabledDisabled(bool enabled);

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

    // Writes a string at (row, column), taking the length from the array itself. Every caller
    // passes either a literal or a snprintf'd fixed-width field, and in both cases the text
    // fills the array exactly, so N - 1 is the number of characters on screen.
    template <std::size_t N>
    void WriteText(uint8_t row, uint8_t column, const char (&text)[N])
    {
        AddToLumexLCDMessageQueue(WRITE_TO_DISPLAY, row, column, text, N - 1);
    }

    osMessageQueueId_t _sessionControllerToLumexLcdHandle;

    State _state;

    // Settings, edited from the menu.
    bool _sdLoggingEnabled;
    bool _pidOptionToggleableEnabled;
    int _desiredRpm;

    // Session state. Whether a session is running is _state.mainState and nothing else --
    // see GetInSessionStatus.
    bool _pidEnabled;
    float _desiredManualBpmDutyCycle;

    // Whether a brake press may start a session. Cleared when the button is already held as this
    // FSM comes up, and set again by the release that follows -- see HandleButtonBrakeInput.
    bool _brakeArmed;

    // How far this FSM has drained the input ISRs' circular buffer.
    uint32_t _fsmInputDataIndex;
};


#endif // INC_TASKS_SESSION_CONTROLLER_FINITESTATEMACHINE_HPP_
