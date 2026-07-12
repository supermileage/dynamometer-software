#ifndef INC_TASKS_SESSION_CONTROLLER_FINITESTATEMACHINE_HPP_
#define INC_TASKS_SESSION_CONTROLLER_FINITESTATEMACHINE_HPP_

#include <algorithm>

#include <cstdint>
#include <cstdio>
#include <cstring>

#include "cmsis_os2.h"

#include "MessagePassing/messages_private.h"

#include "input_manager_interrupts.h"

#ifdef __cplusplus
extern "C" {
#endif

// struct with info for the state
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
        SD_LOGGING_OPTION_EDIT,
        PID_ENABLE_DISPLAYED,
        PID_ENABLE_EDIT,
        PID_DESIRED_RPM_DISPLAYED,
        PID_DESIRED_RPM_EDIT
    };

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
    void HandleUserInputs();

    // Display related methods
    void ClearDisplay();
    void AddToLumexLCDMessageQueue(session_controller_to_lumex_lcd_opcode opcode, uint8_t row, uint8_t column, const char* display_string, size_t size);
    
    // Getters
    State GetState() const;
    bool GetSDLoggingEnabledStatus() const;
    bool GetPIDEnabledModeStatus() const;
    bool GetPIDOptionToggleableEnabledStatus() const;
    bool GetManualBpmModeStatus() const;
    bool GetManualThrottleModeStatus() const;

    bool GetInSessionStatus() const;

    float GetDesiredBpmDutyCycle() const;
    float GetDesiredThrottleDutyCycle() const;

    float GetDesiredRpm() const;
    float GetDesiredAngularVelocity() const;

    void DisplayRpm(float rpm);
    void DisplayTorque(float torque);
    void DisplayPower(float power);
    void DisplayPIDEnabled();
    void DisplayManualBPMDutyCycle();
    void DisplayManualThrottleDutyCycle();

private:
    // Methods which are called when a state change is done
    void IdleState();
    void SDLoggingOptionDisplayedSettingsState();
    void SDLoggingOptionEditSettingsState();   
    void PIDOptionDisplayedSettingsState();
    void PIDOptionEditSettingsState();
    void PIDDesiredRPMOptionDisplayedSettingsState();
    void PIDDesiredRPMOptionEditSettingsState(bool clearDisplay);
    void InSessionState();
    
    // user input handlers
    void HandleRotaryEncoderInput(bool positiveTick);
    void HandleRotaryEncoderSwInput();
    void HandleButtonBackInput();
    void HandleButtonSelectInput();
    void HandleButtonBrakeInput(bool isEnabled);

    void ConvertUserInputIntoDesiredRpm(bool positiveTick);
    int ConvertDesiredRpmUnitsStateToIncrement();

    osMessageQueueId_t _sessionControllerToLumexLcdHandle;

    State _state;
    
    
    bool _sdLoggingEnabled;
    bool _pidOptionToggleableEnabled;
    bool _pidEnabled;
    bool _throttleControlModeEnabled;
    bool _inSession;
    float _desiredManualBpmDutyCycle;
    float _desiredManualThrottleDutyCycle;
    int _desiredRpm;
    int _desiredRpmIncrement;
    uint32_t _fsmInputDataIndex;
};


#ifdef __cplusplus
}
#endif





#endif // INC_TASKS_SESSION_CONTROLLER_FINITESTATEMACHINE_HPP_

