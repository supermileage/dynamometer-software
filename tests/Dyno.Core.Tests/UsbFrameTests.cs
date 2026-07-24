using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Dyno.Core.Messages;
using Dyno.Core.Protocol;
using Xunit;

namespace Dyno.Core.Tests;

public class UsbFrameTests
{
    [Fact]
    public void Crc16_MatchesCcittFalseCheckValue()
    {
        // The canonical CRC-16/CCITT-FALSE check value for "123456789" is 0x29B1.
        ushort crc = UsbFrame.Crc16(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal((ushort)0x29B1, crc);
    }

    [Fact]
    public void BuildCommandFrame_LaysOutSofHeaderPayloadAndValidCrc()
    {
        byte[] body = [0x05];
        byte[] frame = UsbFrame.BuildCommandFrame(
            task_offset_t.TASK_OFFSET_USB_CONTROLLER,
            usb_msg_type_t.USB_MSG_COMMAND,
            opcode: (ushort)usb_controller_command_t.USB_CMD_SET_SYSCONFIG,
            msgId: 7,
            body
        );

        int headerSize = UsbFrame.HeaderSize;
        int cmdSize = UsbFrame.CommandHeaderSize;
        int payloadLen = cmdSize + body.Length;
        var span = frame.AsSpan();

        Assert.Equal(2 + headerSize + payloadLen + 2, frame.Length);
        Assert.Equal(UsbFrame.Sof, BinaryPrimitives.ReadUInt16LittleEndian(span));

        var header = MemoryMarshal.Read<usb_msg_header_t>(span.Slice(2, headerSize));
        Assert.Equal(usb_msg_type_t.USB_MSG_COMMAND, header.msg_type);
        Assert.Equal(task_offset_t.TASK_OFFSET_USB_CONTROLLER, header.task_offset);
        Assert.Equal((uint)payloadLen, header.payload_len);

        var cmd = MemoryMarshal.Read<usb_cmd_header_t>(span.Slice(2 + headerSize, cmdSize));
        Assert.Equal((ushort)usb_controller_command_t.USB_CMD_SET_SYSCONFIG, cmd.opcode);
        Assert.Equal((ushort)7, cmd.msg_id);
        Assert.Equal(body[0], frame[2 + headerSize + cmdSize]);

        ushort expectedCrc = UsbFrame.Crc16(span.Slice(2, headerSize + payloadLen));
        ushort actualCrc = BinaryPrimitives.ReadUInt16LittleEndian(
            span.Slice(2 + headerSize + payloadLen)
        );
        Assert.Equal(expectedCrc, actualCrc);
    }
}
