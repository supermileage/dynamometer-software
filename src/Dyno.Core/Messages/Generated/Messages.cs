// AUTO-GENERATED from tools/message_gen/schema/messages_public.yaml by tools/message_gen/generate.py -- DO NOT EDIT.
// Edit that schema in the firmware/ tree, then run
// tools/message_gen/generate.py (CI verifies the committed file matches the schema).
//
// Only the data contract is emitted: constants, enums and packed structs. The schema's
// C-only `code` sections (extern "C" guards, the firmware populate helper, the
// usb_frame_crc16 body) are skipped; the CRC is re-implemented in Dyno.Core/Protocol
// using the generated USB_FRAME_CRC_* / USB_FRAME_SOF constants below.
using System;
using System.Runtime.InteropServices;

namespace Dyno.Core.Messages;

/// <summary>Protocol constants (the schema's #define section).</summary>
public static class MessageConstants
{
    public const uint TASK_OFFSET_SHIFT = 16u;   // 16u
    public const uint WARNING_FLAG = 0x8000u;   // (1u << 15)
    public const uint TASK_ERROR_NUM_MASK = 0x7FFFu;   // (WARNING_FLAG - 1u)  -- bits 0..14
    public const uint TASK_OFFSET_MASK = 0xFFFF0000u;   // (0xFFFFu << TASK_OFFSET_SHIFT)  -- bits 16..31
    public const uint USB_FRAME_SOF = 0xA55Au;   // 0xA55Au
    public const uint USB_FRAME_CRC_INIT = 0xFFFFu;   // 0xFFFFu
    public const uint USB_FRAME_CRC_POLY = 0x1021u;   // 0x1021u
    public const uint USB_RX_MAX_PAYLOAD = 128u;   // 128u
    public const uint USB_PROTOCOL_VERSION = 4u;   // 4u
    public const uint SYSCFG_PARAM_COUNT = 35u;   // 35u  -- one past the highest sysconfig_param_t id; sizes the firmware store
}

// A task error/warning is reported as a single 32-bit code:
//   bits 31..16 : task offset (unique per task, see task_offset_t)
//   bit  15     : warning flag (set => warning, clear => error)
//   bits 14..0  : task-local error number
// An error code is formed by OR-ing the task offset with the error number, so the
// task id no longer needs to be sent as a separate field.

// ****************************************************
// ERRORS AND WARNINGS
// ****************************************************

/// Unique per-task offset occupying the high bits of an error code. OR-ed with a
/// task-local error number to form the error_code sent over USB.
public enum task_offset_t : uint
{
    TASK_OFFSET_NO_TASK = 0xFFFF0000,
    TASK_OFFSET_TASK_MONITOR = 0,
    TASK_OFFSET_SESSION_CONTROLLER = 0x10000,
    TASK_OFFSET_USB_CONTROLLER = 0x20000,
    TASK_OFFSET_SD_CONTROLLER = 0x30000,
    TASK_OFFSET_OPTICAL_ENCODER = 0x40000,
    TASK_OFFSET_FORCE_SENSOR_ADC = 0x50000,
    TASK_OFFSET_FORCE_SENSOR_ADS1115 = 0x60000,
    TASK_OFFSET_BPM_CONTROLLER = 0x70000,
    TASK_OFFSET_PID_CONTROLLER = 0x80000,
    TASK_OFFSET_LUMEX_LCD = 0x90000,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct task_error_data
{
    public uint timestamp;
    public uint error_code;   // task_offset | (warning ? WARNING_FLAG : 0) | error number
}

public enum session_controller_task_error_ids : uint
{
    ERROR_SESSION_CONTROLLER_TIMESTAMP_TIMER_START_FAILURE = 0,
    ERROR_SESSION_CONTROLLER_INVALID_TASK_QUEUE_POINTER = 1,
    ERROR_SESSION_CONTROLLER_INVALID_UART1_MUTEX_POINTER = 2,
}

public enum bpm_task_error_ids : uint
{
    ERROR_BPM_PWM_START_FAILURE = 0,
    ERROR_BPM_PWM_STOP_FAILURE = 1,
}

public enum lumex_lcd_task_error_ids : uint
{
    ERROR_LUMEX_LCD_TIMER_START_FAILURE = 0,
}

public enum task_monitor_task_error_ids : uint
{
    ERROR_TASK_MONITOR_INVALID_THREAD_ID_POINTER = 0,
}

public enum pid_controller_task_error_ids : uint
{
    WARNING_PID_CONTROLLER_MESSAGE_QUEUE_FULL = 0x8000,
}

public enum force_sensor_adc_task_error_ids : uint
{
    ERROR_FORCE_SENSOR_ADC_START_FAILURE = 0,
}

public enum force_sensor_ads1115_error_ids : uint
{
    ERROR_FORCE_SENSOR_ADS1115_INIT_FAILURE = 0,
    WARNING_FORCE_SENSOR_ADS1115_TRIGGER_CONVERSION_FAILURE = 0x8000,
    WARNING_FORCE_SENSOR_ADS1115_GET_CONVERSION_FAILURE = 0x8001,
    WARNING_FORCE_SENSOR_ADS1115_CONFIG_FAILURE = 0x8002,
}

// ****************************************************
// USB AND PUBLIC MESSAGES
// ****************************************************

public enum usb_msg_type_t : uint
{
    USB_MSG_INVALID = 0,
    USB_MSG_COMMAND = 1,   // PC -> STM32 (do something)
    USB_MSG_RESPONSE = 2,   // STM32 -> PC (reply to command)
    USB_MSG_EVENT = 3,   // STM32 -> PC (async event)
    USB_MSG_STREAM = 4,   // STM32 -> PC (continuous data)
    USB_MSG_CONFIG = 5,   // PC -> STM32 (set parameters)
    USB_MSG_STATUS = 6,   // STM32 -> PC (health / state)
    USB_MSG_ERROR = 7,   // STM32 -> PC (error report)
    USB_MSG_WARNING = 8,   // STM32 -> PC (warning report)
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct usb_msg_header_t
{
    public usb_msg_type_t msg_type;   // protocol-level intent
    public task_offset_t task_offset;   // which module owns payload
    public uint payload_len;   // bytes following header
}

// ---- Host -> device framed command envelope -------------------------------
// Inbound (PC -> STM32) frames are wrapped so the parser can resync after a ring
// overflow drops bytes mid-stream:
//   [uint16_t USB_FRAME_SOF][usb_msg_header_t header][payload bytes][uint16_t crc]
// crc is CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF) computed over the header
// bytes followed by the payload bytes (the SOF marker and crc field themselves are
// excluded). Multi-byte fields are little-endian, matching both the STM32 and the
// x86 host. The same envelope is reused for the host-side parser.

// Largest inbound payload the firmware accepts; frames claiming more are treated as
// a spurious SOF and skipped during resync.

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
// 
// v3 added the runtime sysconfig protocol (sysconfig_param_t / USB_CMD_SET_SYSCONFIG).
// A v3 host pushes its saved settings after every handshake and trusts they applied;
// against v2 firmware those pushes would be silently unknown commands and the dyno
// would run defaults while the host displays the values it believes it set.
// 
// v4 added session_controller_output_data (torque / power stream). A v4 host expects
// those readouts during a session; against v3 firmware they would simply never arrive
// and torque/power would sit blank next to live sensor data with nothing saying why.

// Shared CRC so firmware and host compute identical checksums over a frame body.

// ---- Host command / firmware response payloads ----------------------------
// COMMAND and CONFIG frame payloads (PC -> STM32) begin with this header. The
// opcode is namespaced by the frame's task_offset (commands addressed to
// TASK_OFFSET_USB_CONTROLLER use usb_controller_command_t, and so on). msg_id is
// chosen by the host and echoed in the matching RESPONSE so a reply can be
// correlated to its request. msg_id 0 is reserved for firmware-internal commands
// that want no host ack; hosts use ids >= 1.

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct usb_cmd_header_t
{
    public ushort opcode;
    public ushort msg_id;
}

// RESPONSE frame payload (STM32 -> PC): echoes the command's opcode + msg_id and
// reports a status. Sent with task_offset set to the module that completed it, so
// the host learns both which message (msg_id) and which module (frame task_offset)
// acked. For a routed setting this is the full-path ack: it is emitted only after
// the owning task has actually applied the command, with the real result status.

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct usb_response_data_t
{
    public ushort opcode;
    public ushort msg_id;
    public uint status;   // usb_response_status_t
}

public enum usb_response_status_t : uint
{
    USB_RSP_OK = 0,
    USB_RSP_UNKNOWN_COMMAND = 1,   // opcode not recognised by the target module
    USB_RSP_MALFORMED = 2,   // payload too short / body out of range
    USB_RSP_NOT_SUPPORTED = 3,   // task_offset has no command route
    USB_RSP_DEVICE_ERROR = 4,   // target applied it but the device write failed (e.g. I2C)
    USB_RSP_QUEUE_FULL = 5,   // target task's command queue was full
    USB_RSP_VERSION_MISMATCH = 6,   // ACK protocol_version != USB_PROTOCOL_VERSION; link refused
}

/// USB-controller-local commands: frames addressed to TASK_OFFSET_USB_CONTROLLER.
public enum usb_controller_command_t : ushort
{
    USB_CMD_ACK = 0,   // host acks the device-ready announce; body = uint32 protocol_version. Firmware replies USB_RSP_OK or USB_RSP_VERSION_MISMATCH
    USB_CMD_SET_SYSCONFIG = 1,   // body = sysconfig_set_param_body; writes one runtime parameter into the sysconfig store. Applied by the USB task itself (the store is plain RAM), so the OK is still a full-path ack
}

// Device-ready announcement (STM32 -> PC): emitted as USB_MSG_EVENT with task_offset
// TASK_OFFSET_USB_CONTROLLER and repeated (~every 200ms) until the host answers with
// USB_CMD_ACK. Carries the firmware's USB_PROTOCOL_VERSION so the host can confirm the
// wire format matches before it trusts -- or acks -- the stream.

[StructLayout(LayoutKind.Sequential)]
public struct usb_device_ready_event
{
    public uint protocol_version;   // == USB_PROTOCOL_VERSION
}

// Session start/stop announcement (STM32 -> PC): emitted as USB_MSG_EVENT with task_offset
// TASK_OFFSET_SESSION_CONTROLLER whenever the dyno enters or leaves a session, and once more
// immediately after the host handshakes -- even when nothing changed. Sensor data only flows
// while a session runs, so without that post-handshake announcement a host connecting to an
// idle board could not tell "no session" from "board is dead", and one connecting mid-session
// would discard the samples it is being sent while it waits for a start it already missed.

[StructLayout(LayoutKind.Sequential)]
public struct session_state_event
{
    public uint timestamp;
    public uint in_session;   // 1 = session running, 0 = idle
}

// ---- Runtime system configuration -----------------------------------------
// The tunable quantities from Config/config.h (gains, task delays, thresholds)
// live in a RAM store (Config/sysconfig.h) seeded from those #defines at boot;
// tasks read the store every loop iteration, so a write takes effect on the
// next pass. The host owns persistence: it keeps the values on the PC and
// re-pushes them after every handshake (the board has no settings storage, so
// a reboot returns to the config.h defaults until then).
// 
// Parameter ids are wire contract: append new ones, never renumber. Compile-time
// settings (circular-buffer sizes -- static array dimensions on a heapless
// firmware -- and debug.h's task/peripheral gates) have no ids here on purpose.

/// One id per runtime-tunable parameter. The name matches the config.h #define
/// that provides its boot default. Ids are positional: append only.
public enum sysconfig_param_t : ushort
{
    SYSCFG_DISTANCE_FROM_FORCE_SENSOR_TO_CENTER_OF_SHAFT_M = 0,   // float, m
    SYSCFG_MOMENT_OF_INERTIA_KG_M2 = 1,   // float, kg·m²
    SYSCFG_K_P = 2,   // float
    SYSCFG_K_I = 3,   // float
    SYSCFG_K_D = 4,   // float
    SYSCFG_PID_MAX_OUTPUT = 5,   // float
    SYSCFG_THROTTLE_GAIN = 6,   // float
    SYSCFG_BRAKE_GAIN = 7,   // float
    SYSCFG_HORIZONTAL_BIAS = 8,   // float
    SYSCFG_VERTICAL_BIAS = 9,   // float
    SYSCFG_MIN_DUTY_CYCLE_PERCENT = 10,   // float, 0–1
    SYSCFG_MAX_DUTY_CYCLE_PERCENT = 11,   // float, 0–1
    SYSCFG_MAX_FORCE_LBF = 12,   // float, lbf
    SYSCFG_SESSIONCONTROLLER_TASK_OSDELAY = 13,   // uint32, ms
    SYSCFG_BPM_TASK_OSDELAY = 14,   // uint32, ms
    SYSCFG_FORCESENSOR_TASK_OSDELAY = 15,   // uint32, ms
    SYSCFG_FORCESENSOR_COMMAND_POLL_OSDELAY = 16,   // uint32, ms
    SYSCFG_FORCESENSOR_CONVERSION_TIMEOUT_MS = 17,   // uint32, ms
    SYSCFG_OPTICAL_ENCODER_TASK_OSDELAY = 18,   // uint32, ms
    SYSCFG_NUM_APERTURES = 19,   // uint32
    SYSCFG_PID_TASK_OSDELAY = 20,   // uint32, ms
    SYSCFG_USB_TASK_OSDELAY = 21,   // uint32, ms
    SYSCFG_USB_TX_FLUSH_MAX_RETRIES = 22,   // uint32, attempts
    SYSCFG_LCD_TASK_OSDELAY = 23,   // uint32, ms
    SYSCFG_LED_TASK_OSDELAY = 24,   // uint32, ms
    SYSCFG_TASK_WARNING_RETRY_OSDELAY = 25,   // uint32, ms
    SYSCFG_TASK_MONITOR_TASK_OSDELAY = 26,   // uint32, ms
    SYSCFG_ADS1115_RATE = 27,   // enum
    SYSCFG_ADS1115_GAIN = 28,   // enum
    SYSCFG_ADS1115_MUX = 29,   // enum
    SYSCFG_ADS1115_MODE = 30,   // enum
    SYSCFG_ADS1115_COMP_MODE = 31,   // enum
    SYSCFG_ADS1115_COMP_POL = 32,   // enum
    SYSCFG_ADS1115_COMP_LAT = 33,   // enum
    SYSCFG_ADS1115_COMP_QUE = 34,   // enum
}

// Body of USB_CMD_SET_SYSCONFIG (after the usb_cmd_header_t). raw_value carries the
// parameter's 32 bits: IEEE-754 bits for float parameters, the plain value for
// uint32 ones -- which is which is fixed per id (see sysconfig_param_t comments),
// so the store can validate range before applying.

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct sysconfig_set_param_body
{
    public ushort param_id;   // sysconfig_param_t
    public uint raw_value;   // value bits (float or uint32 per param)
}

[StructLayout(LayoutKind.Sequential)]
public struct optical_encoder_output_data
{
    public uint timestamp;   // Timestamp of the reading
    public float angular_velocity;   // Measured angular velocity
    public uint raw_value;   // In case users want to have custom implementation with it
    public float angular_acceleration;   // Measured angular acceleration
}

[StructLayout(LayoutKind.Sequential)]
public struct forcesensor_output_data
{
    public uint timestamp;
    public float force;
    public uint raw_value;
}

[StructLayout(LayoutKind.Sequential)]
public struct bpm_output_data
{
    public uint timestamp;
    public float duty_cycle;
    public uint raw_value;   // Really just padding to match the other output data types
}

// Derived dyno quantities, computed by the SessionController from the sensor streams:
// torque = I*alpha + F*r (+ mechanical losses), power = torque * omega. Streamed as
// USB_MSG_STREAM with task_offset TASK_OFFSET_SESSION_CONTROLLER while a session runs,
// so the host shows the same numbers the LCD does without re-deriving them.

[StructLayout(LayoutKind.Sequential)]
public struct session_controller_output_data
{
    public uint timestamp;
    public float torque;   // N·m
    public float power;   // W
}

[StructLayout(LayoutKind.Sequential)]
public struct task_monitor_output_data
{
    public uint timestamp;
    public task_offset_t task_offset;
    public int task_state;
    public uint free_bytes;
}

/// <summary>Wire sizes the schema asserts via static_assert; checked by the unit tests.</summary>
public static class MessageContract
{
    public static readonly (Type Type, int Size)[] ExpectedSizes = new (Type, int)[]
    {
        (typeof(task_error_data), 8),
        (typeof(task_offset_t), 4),
        (typeof(session_controller_task_error_ids), 4),
        (typeof(bpm_task_error_ids), 4),
        (typeof(lumex_lcd_task_error_ids), 4),
        (typeof(task_monitor_task_error_ids), 4),
        (typeof(pid_controller_task_error_ids), 4),
        (typeof(force_sensor_adc_task_error_ids), 4),
        (typeof(force_sensor_ads1115_error_ids), 4),
        (typeof(usb_msg_type_t), 4),
        (typeof(usb_msg_header_t), 12),
        (typeof(usb_cmd_header_t), 4),
        (typeof(usb_response_data_t), 8),
        (typeof(usb_device_ready_event), 4),
        (typeof(session_state_event), 8),
        (typeof(sysconfig_param_t), 2),
        (typeof(sysconfig_set_param_body), 6),
        (typeof(optical_encoder_output_data), 16),
        (typeof(forcesensor_output_data), 12),
        (typeof(bpm_output_data), 12),
        (typeof(session_controller_output_data), 12),
        (typeof(task_monitor_output_data), 16),
    };
}
