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

#include "Tasks/OpticalSensor/encoder_math.h"

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
//   PID_ENABLE_DISPLAYED <-> PID_DESIRED_RPM_DISPLAYED <-> (wraps)
//
// SELECT on the toggle page flips the setting and redraws in place. SELECT on the
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
        PID_ENABLE_DISPLAYED = 0,
        // Nothing ever enters PID_ENABLE_EDIT: a toggle is applied on the display page itself,
        // so that setting has no edit screen. It is kept only so the enumerators below hold
        // their values.
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
    void DisplayAngularVelocity(float angularVelocity);
    void DisplayForce(float force);

    // Extra in-session detail. Only the ILI9341 panel has room to show these; the character
    // panel discards them (see the DisplayDriver concept), so they are always sent and it
    // costs nothing to record them here.
    void DisplayAngularAcceleration(float angularAcceleration);
    void DisplayPIDEnabled();
    void DisplayManualBPMDutyCycle();

    // Sets the commanded brake duty cycle from a host command. Returns false, and changes
    // nothing, unless a session is running: the brake is never actuated outside one, however
    // the request arrives. The value is clamped to the same envelope the encoder is.
    bool SetHostBrakeDutyCycle(float dutyCycle);

    // Redraws the current screen if one of the two settings it can show has been changed by
    // somebody other than this FSM. That means the host: USB_CMD_SET_SYSCONFIG is applied by
    // the USB task straight into the store (it is plain RAM -- no queue, no wake-up), so
    // nothing tells this class the value moved.
    //
    // Every other path to the panel is an event this FSM handles, and each of those reposts on
    // its way through, which is why a menu page drawn by the encoder is always current. A host
    // write has no such event, so the SessionController calls this once per pass and it polls
    // instead. Cheap: it compares two words and posts nothing when they match.
    void RefreshHostEditedSettings();

    // What the SessionController acts on
    State GetState() const;
    bool GetPIDEnabledModeStatus() const;
    bool GetPIDOptionToggleableEnabledStatus() const;

    bool GetInSessionStatus() const;

    float GetDesiredBpmDutyCycle() const;

    uint32_t GetDesiredRpm() const;
    float GetDesiredAngularVelocity() const;

private:
    // --- Screens. Each sets the state it represents and reposts it.
    void ShowIdleScreen();
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

    // The two settings this menu edits are NOT members: they live in the sysconfig store as
    // SYSCFG_PID_ENABLE and SYSCFG_PID_DESIRED_RPM, because the host can write them over USB
    // too and the two editors have to be editing the same value. A cached copy here would be
    // the thing that goes stale -- the panel would show what the encoder last set while the
    // PID ran on what the host last pushed. So the getters below read the store, and the
    // handlers write it; see the note in Config/config.h for how this pairs with the
    // compile-time PID_CONTROLLER_TASK_ENABLE.
    //
    // These two are the exception that proves it, and they are not copies of the settings: they
    // record what the last PostDisplayState *carried*, so RefreshHostEditedSettings can tell
    // that a host write has left the panel showing something else. Nothing reads them as a
    // setting -- every read of the settings themselves still goes to the store.
    bool _postedPidOptionEnabled;
    uint32_t _postedDesiredRpm;

    // Session state. Whether a session is running is _state.mainState and nothing else --
    // see GetInSessionStatus.
    bool _pidEnabled;
    float _desiredManualBpmDutyCycle;

    // Newest readings the SessionController has handed over. Held because every post carries
    // the whole screen state, so a force update still has to say what the RPM is. Stored as
    // RPM: the conversion from the encoder's rad/s happens on the way in.
    float _rpm;
    float _force;

    // Session detail, derived here rather than by a panel so both get the same numbers.
    // Peak force is the largest magnitude seen since the session started -- a pull and a push
    // are both loads on the rig -- and both reset on entry to a session, not on exit, so the
    // screen keeps showing the last run's figures until a new one begins.
    float _angularAcceleration;
    float _peakForce;
    uint32_t _sessionStartTimestamp;

    // Whether a brake press may start a session. Cleared when the button is already held as this
    // FSM comes up, and set again by the release that follows -- see HandleButtonBrakeInput.
    bool _brakeArmed;

    // How far this FSM has drained the input ISRs' circular buffer.
    uint32_t _fsmInputDataIndex;
};


#endif // INC_TASKS_SESSION_CONTROLLER_FINITESTATEMACHINE_HPP_
