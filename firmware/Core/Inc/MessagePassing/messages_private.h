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

// Opcodes for controlling the Lumex LCD display from the session controller
typedef enum : uint32_t
{
    CLEAR_DISPLAY = 0,   // Clear the entire display
    WRITE_TO_DISPLAY = 1   // Write a string to a specific location on the display
} session_controller_to_lumex_lcd_opcode;

DYNO_STATIC_ASSERT(sizeof(session_controller_to_lumex_lcd_opcode) == 4, "Size of session_controller_to_lumex_lcd_opcode must be 4 bytes");

// Message sent from the session controller to the Lumex LCD
typedef struct {
    session_controller_to_lumex_lcd_opcode op;   // Operation to perform on the display
    uint32_t row;   // Row number on the LCD
    uint32_t column;   // Column number on the LCD
    size_t size;
    char display_string[SESSION_CONTROLLER_TO_LUMEX_LCD_MSG_STRING_SIZE];   // String to write (if WRITE_TO_DISPLAY)
} session_controller_to_lumex_lcd;

DYNO_STATIC_ASSERT(sizeof(session_controller_to_lumex_lcd) >= offsetof(session_controller_to_lumex_lcd, display_string) + sizeof(((session_controller_to_lumex_lcd *)0)->display_string), "Size of session_controller_to_lumex_lcd must be correct");

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
