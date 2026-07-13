using Dyno.Core.Messages;

namespace Dyno.Core.Protocol;

/// <summary>
/// A decoded message from the STM32 → PC stream. Consumers pattern-match on the concrete
/// record (e.g. in a UI <c>switch</c>) to read the typed payload.
/// </summary>
public abstract record DeviceMessage;

public sealed record OpticalEncoderSample(optical_encoder_output_data Data) : DeviceMessage;

public sealed record ForceSensorSample(task_offset_t Source, forcesensor_output_data Data)
    : DeviceMessage;

public sealed record BpmSample(bpm_output_data Data) : DeviceMessage;

public sealed record TaskMonitorSample(task_monitor_output_data Data) : DeviceMessage;

/// <summary>An error or a warning; <see cref="DecodedError.IsWarning"/> distinguishes them.</summary>
public sealed record DeviceFault(DecodedError Error, uint Timestamp) : DeviceMessage;

/// <summary>
/// The firmware's <c>USB_MSG_EVENT</c> device-ready announcement. Emitted repeatedly until the
/// host answers with <c>USB_CMD_ACK</c>; carries the firmware's protocol version for the
/// handshake's wire-format check.
/// </summary>
public sealed record DeviceReady(usb_device_ready_event Data) : DeviceMessage;

/// <summary>
/// The firmware's <c>USB_MSG_EVENT</c> session start/stop announcement. Sent on every transition
/// and once more right after the handshake (even with nothing changed), so a host that connects to
/// a steady board still learns whether it is idle or mid-session. Sensor samples only stream while
/// <see cref="InSession"/> holds.
/// </summary>
public sealed record SessionState(session_state_event Data) : DeviceMessage
{
    public bool InSession => Data.in_session != 0;

    public uint Timestamp => Data.timestamp;
}

/// <summary>Reply to a host command, correlated to its request by <c>msg_id</c>. <paramref
/// name="Source"/> is the module that actually applied it — for a routed command that is the
/// owning task, not the USB controller that relayed it.</summary>
public sealed record CommandResponse(task_offset_t Source, usb_response_data_t Data)
    : DeviceMessage;

/// <summary>A well-formed header whose (type, task_offset, length) we don't decode yet.</summary>
public sealed record UnknownMessage(usb_msg_header_t Header, byte[] Payload) : DeviceMessage;
