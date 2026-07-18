using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dyno.Core.Messages;
using Dyno.Core.Protocol;

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

    /// <summary>A framed STM32 → PC record exactly as v5 firmware emits it:
    /// <c>[SOF][usb_msg_header_t][payload][crc16]</c>.</summary>
    public static byte[] Message<T>(usb_msg_type_t type, task_offset_t offset, in T payload)
        where T : struct => MessageRaw(type, offset, ToBytes(payload));

    /// <summary>Same envelope over arbitrary payload bytes (odd sizes, empty payloads).</summary>
    public static byte[] MessageRaw(usb_msg_type_t type, task_offset_t offset, byte[] body)
    {
        var header = new usb_msg_header_t
        {
            msg_type = type,
            task_offset = offset,
            payload_len = (uint)body.Length,
        };
        byte[] inner = [.. ToBytes(header), .. body];
        var crc = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(crc, UsbFrame.Crc16(inner));
        var sof = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(sof, UsbFrame.Sof);
        return [.. sof, .. inner, .. crc];
    }
}
