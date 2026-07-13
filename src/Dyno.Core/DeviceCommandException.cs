using Dyno.Core.Messages;

namespace Dyno.Core;

/// <summary>
/// Thrown when the firmware acknowledges a command with a non-OK status: the RESPONSE arrived
/// (so this is <b>not</b> a timeout) but the target module rejected or failed to apply the
/// command. Raised only by calls that opt into strict checking (<c>throwOnError: true</c>).
/// </summary>
public sealed class DeviceCommandException : Exception
{
    public ushort Opcode { get; }
    public ushort MsgId { get; }
    public usb_response_status_t Status { get; }

    public DeviceCommandException(usb_response_data_t response)
        : base(
            $"command opcode {response.opcode} (msg_id {response.msg_id}) rejected: {(usb_response_status_t)response.status}"
        )
    {
        Opcode = response.opcode;
        MsgId = response.msg_id;
        Status = (usb_response_status_t)response.status;
    }
}
