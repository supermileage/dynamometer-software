// AUTO-GENERATED from tools/message_gen/schema/messages_private.yaml by generate.py -- DO NOT EDIT.
// Change that schema and re-run tools/message_gen/generate.py (CI verifies they match).
#ifndef INC_MAIN_BOARD_MESSAGEPASSING_MESSAGES_PRIVATE_H_
#define INC_MAIN_BOARD_MESSAGEPASSING_MESSAGES_PRIVATE_H_

#include <stdint.h>
#include <stdbool.h>
#include <stdlib.h>
#include <stddef.h>
#include "cmsis_os2.h"
#include "Config/config.h"
#include "messages_public.h"

// Compile-time assertions: C spells them _Static_assert, C++ spells them static_assert,
// and not every compiler/libc pair maps one spelling to the other (newlib's C++ headers
// do, glibc's don't). Spelled per language under a name of our own so this header
// compiles as either language everywhere — the ARM firmware and the host-compiled unit
// tests in firmware/tests/ alike. (Benign redefinition when both generated headers are
// included: the definitions are identical.)
#ifdef __cplusplus
#define DYNO_STATIC_ASSERT(expr, msg) static_assert(expr, msg)
#else
#define DYNO_STATIC_ASSERT(expr, msg) _Static_assert(expr, msg)
#endif

#ifdef __cplusplus
extern "C" {
#endif

// Which screen the session controller's FSM is showing. The message below carries screen
// state, not draw commands: the FSM says what it is displaying and each display driver
// renders that however its panel allows. A 16x2 character LCD and a 320x240 TFT have no
// useful common drawing API -- the intersection caps the TFT at 16x2, the union is
// meaningless on the character LCD -- so the seam is here instead, at what the values mean.
typedef enum : uint32_t
{
    DISPLAY_SCREEN_IDLE = 0,   // Attract screen; SELECT opens the settings menu
    DISPLAY_SCREEN_SD_LOGGING,   // Settings: SD logging on/off
    DISPLAY_SCREEN_PID_ENABLE,   // Settings: whether the PID option may be toggled in-session
    DISPLAY_SCREEN_DESIRED_RPM,   // Settings: the desired-RPM setpoint
    DISPLAY_SCREEN_DESIRED_RPM_EDIT,   // Settings: the same setpoint with the digit cursor showing
    DISPLAY_SCREEN_SESSION   // Live readout while a session runs
} display_screen_id;

DYNO_STATIC_ASSERT(sizeof(display_screen_id) == 4, "Size of display_screen_id must be 4 bytes");

// Which decimal digit of the desired RPM the encoder is editing. Mirrors
// State::DesiredRpmUnitsState in FiniteStateMachine.hpp. Sent as the cursor position
// rather than as the step size it implies, so a driver can mark the digit itself
// instead of only printing the increment.
typedef enum : uint32_t
{
    DISPLAY_RPM_DIGIT_TEN_THOUSAND = 0,
    DISPLAY_RPM_DIGIT_THOUSAND,
    DISPLAY_RPM_DIGIT_HUNDRED,
    DISPLAY_RPM_DIGIT_TEN,
    DISPLAY_RPM_DIGIT_ONE
} display_rpm_digit;

DYNO_STATIC_ASSERT(sizeof(display_rpm_digit) == 4, "Size of display_rpm_digit must be 4 bytes");

// Everything any screen shows, sent whole on every update. Drivers diff it against the
// last one they rendered and repaint only what moved -- which is what makes a 320x240
// panel viable at all, since a full frame over SPI costs ~50-100 ms but a single field
// costs ~1-2 ms. The older protocol sent ("  1234", row 0, col 3) and left the driver
// unable to tell which quantity had changed.
typedef struct {
    display_screen_id screen;   // Which screen to render
    float rpm;   // Measured shaft speed in RPM, already converted from the encoder's rad/s
    float force;   // Measured force in N
    float bpm_duty_cycle;   // Commanded brake duty cycle, 0 - 1
    uint32_t desired_rpm;   // The PID setpoint being displayed or edited
    display_rpm_digit cursor_digit;   // Digit the encoder edits (DESIRED_RPM_EDIT only)
    bool pid_enabled;   // Whether the PID loop is armed for this session
    bool pid_option_toggleable;   // Whether the menu allows arming it; also selects the in-session drive-mode field
    bool sd_logging_enabled;   // Whether SD logging is switched on
} session_controller_to_display;

DYNO_STATIC_ASSERT(sizeof(session_controller_to_display) <= 32, "session_controller_to_display is queued 25 deep -- keep it small");

// Opcodes for controlling the BPM (Pulse Width Modulation) module from the session controller
typedef enum : uint32_t
{
    READ_FROM_PID = 0,   // read from PID Controller
    START_PWM,   // Start PWM output
    STOP_PWM   // Stop PWM output
} session_controller_to_bpm_opcode;

DYNO_STATIC_ASSERT(sizeof(session_controller_to_bpm_opcode) == 4, "Size of session_controller_to_bpm_opcode must be 4 bytes");

// Message sent from the session controller to the BPM module
typedef struct {
    session_controller_to_bpm_opcode op;   // Operation to perform on the BPM
    float new_duty_cycle_percent;   // New duty cycle percentage from 0 - 1
} session_controller_to_bpm;

DYNO_STATIC_ASSERT(sizeof(session_controller_to_bpm) == 4 + 4, "Size of session_controller_to_bpm must be 8 bytes");

// Message sent from the session controller to the PID controller
typedef struct {
    bool enable_status;   // Enable or disable the PID controller
    float desired_angular_velocity;   // Desired motor RPM setpoint
} session_controller_to_pid_controller;

DYNO_STATIC_ASSERT(sizeof(session_controller_to_pid_controller) == 4 + 4, "Size of session_controller_to_pid_controller must be 8 bytes");

// ---- USB host command routing (USB task <-> owning task) ------------------
// Largest command body the USB task will forward to a task (after the
// usb_cmd_header_t). Keep small; settings are a few bytes.

#define USB_TASK_CMD_BODY_MAX 16

// A host setting routed from the USB task straight to the owning task's command
// queue. opcode/body are the task-local command; msg_id is the host correlation id
// (0 => firmware-internal, no host ack). The target task parses body by opcode.

typedef struct {
    uint16_t opcode;
    uint16_t msg_id;
    uint8_t body[USB_TASK_CMD_BODY_MAX];
    uint8_t body_len;
} usb_task_command;

// A completion the owning task posts back (shared queue) once it has applied a
// command. The USB task drains these and frames a USB_MSG_RESPONSE to the host,
// echoing msg_id with the real status — the far end of the full-path ack. msg_id 0
// completions are dropped (internal commands the host never asked about).

typedef struct {
    task_offset_t task_offset;   // which module completed the command
    uint16_t opcode;
    uint16_t msg_id;
    uint32_t status;   // usb_response_status_t
} usb_task_completion;

#ifdef __cplusplus
}
#endif

#endif /* INC_MAIN_BOARD_MESSAGEPASSING_MESSAGES_PRIVATE_H_ */
