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

    /// <summary>One CDC transfer as v7 firmware emits it: the given records followed by the
    /// <see cref="usb_tx_batch_trailer"/> closing them, whose <c>batch_len</c> counts itself.</summary>
    public static byte[] Batch(uint sequence, params byte[][] records)
    {
        // The trailer's own framed size is fixed, but deriving it beats asserting it: a change to
        // the envelope or the struct then flows through instead of breaking every batch test.
        int trailerLen = Trailer(sequence, 0).Length;
        int total = records.Sum(r => r.Length) + trailerLen;
        return [.. records.SelectMany(r => r), .. Trailer(sequence, (uint)total)];
    }

    public static byte[] Trailer(uint sequence, uint length) =>
        Message(
            usb_msg_type_t.USB_MSG_STATUS,
            task_offset_t.TASK_OFFSET_USB_CONTROLLER,
            new usb_tx_batch_trailer { batch_seq = sequence, batch_len = length }
        );

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
