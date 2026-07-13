using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dyno.Core.Messages;

namespace Dyno.Core.Protocol;

/// <summary>
/// Host side of the framed PC → STM32 command envelope:
/// <c>[uint16 SOF][usb_msg_header_t][payload][uint16 crc16]</c>, little-endian, with the
/// CRC over header+payload. CRC parameters and SOF come from the generated
/// <see cref="MessageConstants"/> so they always match the firmware.
/// </summary>
public static class UsbFrame
{
    public static readonly int HeaderSize = Unsafe.SizeOf<usb_msg_header_t>();
    public static readonly int CommandHeaderSize = Unsafe.SizeOf<usb_cmd_header_t>();
    public const ushort Sof = unchecked((ushort)MessageConstants.USB_FRAME_SOF);

    /// <summary>CRC-16/CCITT-FALSE — a direct port of the firmware's <c>usb_frame_crc16</c>.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = unchecked((ushort)MessageConstants.USB_FRAME_CRC_INIT);
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc =
                    (crc & 0x8000) != 0
                        ? (ushort)((crc << 1) ^ MessageConstants.USB_FRAME_CRC_POLY)
                        : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    /// <summary>
    /// Builds a framed command/config frame. The payload is a <see cref="usb_cmd_header_t"/>
    /// (opcode + msg_id) followed by <paramref name="body"/>. Returns the bytes to write.
    /// </summary>
    public static byte[] BuildCommandFrame(
        task_offset_t target,
        usb_msg_type_t type,
        ushort opcode,
        ushort msgId,
        ReadOnlySpan<byte> body
    )
    {
        int payloadLen = CommandHeaderSize + body.Length;
        var header = new usb_msg_header_t
        {
            msg_type = type,
            task_offset = target,
            payload_len = (uint)payloadLen,
        };
        var cmd = new usb_cmd_header_t { opcode = opcode, msg_id = msgId };

        var frame = new byte[sizeof(ushort) + HeaderSize + payloadLen + sizeof(ushort)];
        var span = frame.AsSpan();

        BinaryPrimitives.WriteUInt16LittleEndian(span, Sof);
        MemoryMarshal.Write(span.Slice(sizeof(ushort), HeaderSize), in header);
        MemoryMarshal.Write(span.Slice(sizeof(ushort) + HeaderSize, CommandHeaderSize), in cmd);
        body.CopyTo(span.Slice(sizeof(ushort) + HeaderSize + CommandHeaderSize, body.Length));

        // CRC covers header + payload only (SOF and the CRC field itself are excluded).
        ushort crc = Crc16(span.Slice(sizeof(ushort), HeaderSize + payloadLen));
        BinaryPrimitives.WriteUInt16LittleEndian(
            span.Slice(sizeof(ushort) + HeaderSize + payloadLen),
            crc
        );
        return frame;
    }
}
