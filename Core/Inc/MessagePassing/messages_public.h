// AUTO-GENERATED from tools/message_gen/schema/messages_public.yaml by generate.py -- DO NOT EDIT.
// Change that schema and re-run tools/message_gen/generate.py (CI verifies they match).
#ifndef INC_MAIN_BOARD_MESSAGEPASSING_MESSAGES_PUBLIC_H_
#define INC_MAIN_BOARD_MESSAGEPASSING_MESSAGES_PUBLIC_H_

#include <stdint.h>
#include <stddef.h>
#include <assert.h>

// A task error/warning is reported as a single 32-bit code:
//   bits 31..16 : task offset (unique per task, see task_offset_t)
//   bit  15     : warning flag (set => warning, clear => error)
//   bits 14..0  : task-local error number
// An error code is formed by OR-ing the task offset with the error number, so the
// task id no longer needs to be sent as a separate field.

#define TASK_OFFSET_SHIFT 16u

#define WARNING_FLAG (1u << 15)

#define TASK_ERROR_NUM_MASK (WARNING_FLAG - 1u)              // bits 0..14

#define TASK_OFFSET_MASK (0xFFFFu << TASK_OFFSET_SHIFT)              // bits 16..31

#ifdef __cplusplus
extern "C" {
#endif

// ****************************************************
// ERRORS AND WARNINGS
// ****************************************************

// Unique per-task offset occupying the high bits of an error code. OR-ed with a
// task-local error number to form the error_code sent over USB.
typedef enum : uint32_t
{
    TASK_OFFSET_NO_TASK = 0xFFFFu << TASK_OFFSET_SHIFT,
    TASK_OFFSET_TASK_MONITOR = 0u  << TASK_OFFSET_SHIFT,
    TASK_OFFSET_SESSION_CONTROLLER = 1u  << TASK_OFFSET_SHIFT,
    TASK_OFFSET_USB_CONTROLLER = 2u  << TASK_OFFSET_SHIFT,
    TASK_OFFSET_SD_CONTROLLER = 3u  << TASK_OFFSET_SHIFT,
    TASK_OFFSET_OPTICAL_ENCODER = 4u  << TASK_OFFSET_SHIFT,
    TASK_OFFSET_FORCE_SENSOR_ADC = 5u  << TASK_OFFSET_SHIFT,
    TASK_OFFSET_FORCE_SENSOR_ADS1115 = 6u  << TASK_OFFSET_SHIFT,
    TASK_OFFSET_BPM_CONTROLLER = 7u  << TASK_OFFSET_SHIFT,
    TASK_OFFSET_PID_CONTROLLER = 8u  << TASK_OFFSET_SHIFT,
    TASK_OFFSET_LUMEX_LCD = 9u  << TASK_OFFSET_SHIFT
} task_offset_t;

typedef struct __attribute__((packed)) {
    uint32_t timestamp;
    uint32_t error_code;   // task_offset | (warning ? WARNING_FLAG : 0) | error number
} task_error_data;

_Static_assert(sizeof(task_error_data) == 4 + 4, "Size of task_error_data must be 8 bytes");

static inline task_error_data PopulateTaskErrorDataStruct(uint32_t timestamp, task_offset_t task_offset, uint32_t error_id)
{
    task_error_data error_data;
    error_data.timestamp = timestamp;
    error_data.error_code = (uint32_t)task_offset | error_id;
    return error_data;
}

_Static_assert(sizeof(task_offset_t) == 4, "Size of task_offset_t must be 4 bytes");

typedef enum : uint32_t
{
    ERROR_SESSION_CONTROLLER_TIMESTAMP_TIMER_START_FAILURE = 0,
    ERROR_SESSION_CONTROLLER_INVALID_TASK_QUEUE_POINTER,
    ERROR_SESSION_CONTROLLER_INVALID_UART1_MUTEX_POINTER
} session_controller_task_error_ids;

_Static_assert(sizeof(session_controller_task_error_ids) == 4, "Size of session_controller_task_error_ids must be 4 bytes");

typedef enum : uint32_t
{
    ERROR_BPM_PWM_START_FAILURE = 0,
    ERROR_BPM_PWM_STOP_FAILURE
} bpm_task_error_ids;

_Static_assert(sizeof(bpm_task_error_ids) == 4, "Size of bpm_task_error_ids must be 4 bytes");

typedef enum : uint32_t
{
    ERROR_LUMEX_LCD_TIMER_START_FAILURE = 0
} lumex_lcd_task_error_ids;

_Static_assert(sizeof(lumex_lcd_task_error_ids) == 4, "Size of lumex_lcd_task_error_ids must be 4 bytes");

typedef enum : uint32_t
{
    ERROR_TASK_MONITOR_INVALID_THREAD_ID_POINTER = 0
} task_monitor_task_error_ids;

_Static_assert(sizeof(task_monitor_task_error_ids) == 4, "Size of task_monitor_task_error_ids must be 4 bytes");

typedef enum : uint32_t
{
    WARNING_PID_CONTROLLER_MESSAGE_QUEUE_FULL = WARNING_FLAG
} pid_controller_task_error_ids;

_Static_assert(sizeof(pid_controller_task_error_ids) == 4, "Size of pid_controller_task_error_ids must be 4 bytes");

typedef enum : uint32_t
{
    ERROR_FORCE_SENSOR_ADC_START_FAILURE = 0
} force_sensor_adc_task_error_ids;

_Static_assert(sizeof(force_sensor_adc_task_error_ids) == 4, "Size of force_sensor_adc_task_error_ids must be 4 bytes");

typedef enum : uint32_t
{
    ERROR_FORCE_SENSOR_ADS1115_INIT_FAILURE = 0,
    WARNING_FORCE_SENSOR_ADS1115_TRIGGER_CONVERSION_FAILURE = WARNING_FLAG,
    WARNING_FORCE_SENSOR_ADS1115_GET_CONVERSION_FAILURE
} force_sensor_ads1115_error_ids;

_Static_assert(sizeof(force_sensor_ads1115_error_ids) == 4, "Size of force_sensor_ads1115_error_ids must be 4 bytes");

// ****************************************************
// USB AND PUBLIC MESSAGES
// ****************************************************

typedef enum : uint32_t
{
    USB_MSG_INVALID = 0,

    USB_MSG_COMMAND,   // PC -> STM32 (do something)
    USB_MSG_RESPONSE,   // STM32 -> PC (reply to command)
    USB_MSG_EVENT,   // STM32 -> PC (async event)
    USB_MSG_STREAM,   // STM32 -> PC (continuous data)
    USB_MSG_CONFIG,   // PC -> STM32 (set parameters)
    USB_MSG_STATUS,   // STM32 -> PC (health / state)

    USB_MSG_ERROR,   // STM32 -> PC (error report)
    USB_MSG_WARNING   // STM32 -> PC (warning report)
} usb_msg_type_t;

_Static_assert(sizeof(usb_msg_type_t) == 4, "Size of usb_msg_type_t must be 4 bytes");

typedef struct __attribute__((packed)) {
    usb_msg_type_t msg_type;   // protocol-level intent
    task_offset_t task_offset;   // which module owns payload
    uint32_t payload_len;   // bytes following header
} usb_msg_header_t;

_Static_assert(sizeof(usb_msg_header_t) == 12, "Size of usb_msg_header_t must be 12 bytes");

// ---- Host -> device framed command envelope -------------------------------
// Inbound (PC -> STM32) frames are wrapped so the parser can resync after a ring
// overflow drops bytes mid-stream:
//   [uint16_t USB_FRAME_SOF][usb_msg_header_t header][payload bytes][uint16_t crc]
// crc is CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF) computed over the header
// bytes followed by the payload bytes (the SOF marker and crc field themselves are
// excluded). Multi-byte fields are little-endian, matching both the STM32 and the
// x86 host. The same envelope is reused for the host-side parser.

#define USB_FRAME_SOF 0xA55Au

#define USB_FRAME_CRC_INIT 0xFFFFu

#define USB_FRAME_CRC_POLY 0x1021u

// Largest inbound payload the firmware accepts; frames claiming more are treated as
// a spurious SOF and skipped during resync.

#define USB_RX_MAX_PAYLOAD 128u

// Wire-format version the device announces (usb_device_ready_event) and the host
// echoes back in its USB_CMD_ACK. Bump whenever any struct/enum below changes layout
// so a host built against an older schema is rejected at the handshake instead of
// silently mis-decoding the stream. Build-time static_asserts guard struct sizes;
// this guards the live link at runtime.
// 
// v2 added session_state_event. The bump matters in the *forward* direction: a v2 host
// shows sensor data only while it believes a session is running, so against v1 firmware
// -- which never announces session state -- it would sit blank forever. Refusing the
// handshake says why, instead of looking like a dead sensor.

#define USB_PROTOCOL_VERSION 2u

// Shared CRC so firmware and host compute identical checksums over a frame body.

static inline uint16_t usb_frame_crc16(const uint8_t *data, size_t len)
{
    uint16_t crc = USB_FRAME_CRC_INIT;
    for (size_t i = 0; i < len; ++i)
    {
        crc ^= (uint16_t)((uint16_t)data[i] << 8);
        for (int bit = 0; bit < 8; ++bit)
        {
            crc = (crc & 0x8000u) ? (uint16_t)((crc << 1) ^ USB_FRAME_CRC_POLY)
                                  : (uint16_t)(crc << 1);
        }
    }
    return crc;
}

// ---- Host command / firmware response payloads ----------------------------
// COMMAND and CONFIG frame payloads (PC -> STM32) begin with this header. The
// opcode is namespaced by the frame's task_offset (commands addressed to
// TASK_OFFSET_USB_CONTROLLER use usb_controller_command_t, and so on). msg_id is
// chosen by the host and echoed in the matching RESPONSE so a reply can be
// correlated to its request. msg_id 0 is reserved for firmware-internal commands
// that want no host ack; hosts use ids >= 1.

typedef struct __attribute__((packed)) {
    uint16_t opcode;
    uint16_t msg_id;
} usb_cmd_header_t;

_Static_assert(sizeof(usb_cmd_header_t) == 4, "Size of usb_cmd_header_t must be 4 bytes");

// RESPONSE frame payload (STM32 -> PC): echoes the command's opcode + msg_id and
// reports a status. Sent with task_offset set to the module that completed it, so
// the host learns both which message (msg_id) and which module (frame task_offset)
// acked. For a routed setting this is the full-path ack: it is emitted only after
// the owning task has actually applied the command, with the real result status.

typedef struct __attribute__((packed)) {
    uint16_t opcode;
    uint16_t msg_id;
    uint32_t status;   // usb_response_status_t
} usb_response_data_t;

_Static_assert(sizeof(usb_response_data_t) == 8, "Size of usb_response_data_t must be 8 bytes");

typedef enum : uint32_t
{
    USB_RSP_OK = 0,
    USB_RSP_UNKNOWN_COMMAND,   // opcode not recognised by the target module
    USB_RSP_MALFORMED,   // payload too short / body out of range
    USB_RSP_NOT_SUPPORTED,   // task_offset has no command route
    USB_RSP_DEVICE_ERROR,   // target applied it but the device write failed (e.g. I2C)
    USB_RSP_QUEUE_FULL,   // target task's command queue was full
    USB_RSP_VERSION_MISMATCH   // ACK protocol_version != USB_PROTOCOL_VERSION; link refused
} usb_response_status_t;

// USB-controller-local commands: frames addressed to TASK_OFFSET_USB_CONTROLLER.
typedef enum : uint16_t
{
    USB_CMD_ACK = 0   // host acks the device-ready announce; body = uint32 protocol_version. Firmware replies USB_RSP_OK or USB_RSP_VERSION_MISMATCH
} usb_controller_command_t;

// Device-ready announcement (STM32 -> PC): emitted as USB_MSG_EVENT with task_offset
// TASK_OFFSET_USB_CONTROLLER and repeated (~every 200ms) until the host answers with
// USB_CMD_ACK. Carries the firmware's USB_PROTOCOL_VERSION so the host can confirm the
// wire format matches before it trusts -- or acks -- the stream.

typedef struct {
    uint32_t protocol_version;   // == USB_PROTOCOL_VERSION
} usb_device_ready_event;

_Static_assert(sizeof(usb_device_ready_event) == 4, "Size of usb_device_ready_event must be 4 bytes");

// Session start/stop announcement (STM32 -> PC): emitted as USB_MSG_EVENT with task_offset
// TASK_OFFSET_SESSION_CONTROLLER whenever the dyno enters or leaves a session, and once more
// immediately after the host handshakes -- even when nothing changed. Sensor data only flows
// while a session runs, so without that post-handshake announcement a host connecting to an
// idle board could not tell "no session" from "board is dead", and one connecting mid-session
// would discard the samples it is being sent while it waits for a start it already missed.

typedef struct {
    uint32_t timestamp;
    uint32_t in_session;   // 1 = session running, 0 = idle
} session_state_event;

_Static_assert(sizeof(session_state_event) == 4 + 4, "Size of session_state_event must be 8 bytes");

// Force-sensor (ADS1115) commands: frames addressed to TASK_OFFSET_FORCE_SENSOR_ADS1115.
typedef enum : uint16_t
{
    FORCE_SENSOR_CMD_SET_DATA_RATE = 0   // body[0] = ADS1115_RATE_* code (0..7)
} force_sensor_command_opcode;

typedef struct {
    uint32_t timestamp;   // Timestamp of the reading
    float angular_velocity;   // Measured angular velocity
    uint32_t raw_value;   // In case users want to have custom implementation with it
    float angular_acceleration;   // Measured angular acceleration
} optical_encoder_output_data;

_Static_assert(sizeof(optical_encoder_output_data) == 4 + 4 + 4 + 4, "Size of optical_encoder_output_data must be 16 bytes");

typedef struct {
    uint32_t timestamp;
    float force;
    uint32_t raw_value;
} forcesensor_output_data;

_Static_assert(sizeof(forcesensor_output_data) == 4 + 4 + 4, "Size of forcesensor_output_data must be 12 bytes");

typedef struct {
    uint32_t timestamp;
    float duty_cycle;
    uint32_t raw_value;   // Really just padding to match the other output data types
} bpm_output_data;

_Static_assert(sizeof(bpm_output_data) == 4 + 4 + 4, "Size of bpm_output_data must be 12 bytes");

typedef struct {
    uint32_t timestamp;
    task_offset_t task_offset;
    int task_state;
    uint32_t free_bytes;
} task_monitor_output_data;

_Static_assert(sizeof(task_monitor_output_data) == 4 + 4 + 4 + 4, "Size of task_monitor_output_data must be 16 bytes");

#ifdef __cplusplus
}
#endif

#endif /* INC_MAIN_BOARD_MESSAGEPASSING_MESSAGES_PUBLIC_H_ */
