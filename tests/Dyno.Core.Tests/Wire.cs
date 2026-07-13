using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dyno.Core.Messages;

namespace Dyno.Core.Tests;

/// <summary>Helpers to synthesize on-the-wire bytes for the parser tests.</summary>
internal static class Wire
{
    public static byte[] ToBytes<T>(in T value)
        where T : struct
    {
        var bytes = new byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(bytes, in value);
        return bytes;
    }

    /// <summary>An unframed STM32 → PC record: <c>usb_msg_header_t</c> + payload bytes.</summary>
    public static byte[] Message<T>(usb_msg_type_t type, task_offset_t offset, in T payload)
        where T : struct
    {
        byte[] body = ToBytes(payload);
        var header = new usb_msg_header_t
        {
            msg_type = type,
            task_offset = offset,
            payload_len = (uint)body.Length,
        };
        return [.. ToBytes(header), .. body];
    }
}
