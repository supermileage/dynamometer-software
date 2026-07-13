using Dyno.Core.Messages;
using Dyno.Core.Protocol;
using Xunit;

namespace Dyno.Core.Tests;

public class StreamParserTests
{
    private static (StreamParser parser, List<DeviceMessage> received) NewParser()
    {
        var parser = new StreamParser();
        var received = new List<DeviceMessage>();
        parser.MessageReceived += received.Add;
        return (parser, received);
    }

    [Fact]
    public void Parses_OpticalEncoderStream()
    {
        var (parser, received) = NewParser();
        var payload = new optical_encoder_output_data
        {
            timestamp = 1234,
            angular_velocity = 3.5f,
            raw_value = 42,
            angular_acceleration = -1.25f,
        };

        parser.Append(
            Wire.Message(
                usb_msg_type_t.USB_MSG_STREAM,
                task_offset_t.TASK_OFFSET_OPTICAL_ENCODER,
                payload
            )
        );

        var sample = Assert.IsType<OpticalEncoderSample>(Assert.Single(received));
        Assert.Equal(1234u, sample.Data.timestamp);
        Assert.Equal(3.5f, sample.Data.angular_velocity);
        Assert.Equal(42u, sample.Data.raw_value);
        Assert.Equal(-1.25f, sample.Data.angular_acceleration);
    }

    [Fact]
    public void Reassembles_MessageSplitAcrossAppends()
    {
        var (parser, received) = NewParser();
        byte[] message = Wire.Message(
            usb_msg_type_t.USB_MSG_STREAM,
            task_offset_t.TASK_OFFSET_BPM_CONTROLLER,
            new bpm_output_data
            {
                timestamp = 1,
                duty_cycle = 0.5f,
                raw_value = 0,
            }
        );

        parser.Append(message.AsSpan(0, 5));
        Assert.Empty(received); // not enough bytes yet

        parser.Append(message.AsSpan(5));
        var sample = Assert.IsType<BpmSample>(Assert.Single(received));
        Assert.Equal(0.5f, sample.Data.duty_cycle);
    }

    [Fact]
    public void Parses_TwoMessagesBackToBack()
    {
        var (parser, received) = NewParser();
        byte[] a = Wire.Message(
            usb_msg_type_t.USB_MSG_STREAM,
            task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADC,
            new forcesensor_output_data
            {
                timestamp = 1,
                force = 10f,
                raw_value = 100,
            }
        );
        byte[] b = Wire.Message(
            usb_msg_type_t.USB_MSG_STREAM,
            task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115,
            new forcesensor_output_data
            {
                timestamp = 2,
                force = 20f,
                raw_value = 200,
            }
        );

        parser.Append([.. a, .. b]);

        Assert.Equal(2, received.Count);
        Assert.All(received, m => Assert.IsType<ForceSensorSample>(m));
        Assert.Equal(
            task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADC,
            ((ForceSensorSample)received[0]).Source
        );
        Assert.Equal(
            task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115,
            ((ForceSensorSample)received[1]).Source
        );
    }

    [Fact]
    public void Decodes_ErrorMessage()
    {
        var (parser, received) = NewParser();
        uint code =
            (uint)task_offset_t.TASK_OFFSET_BPM_CONTROLLER
            | (uint)bpm_task_error_ids.ERROR_BPM_PWM_START_FAILURE;

        parser.Append(
            Wire.Message(
                usb_msg_type_t.USB_MSG_ERROR,
                task_offset_t.TASK_OFFSET_BPM_CONTROLLER,
                new task_error_data { timestamp = 99, error_code = code }
            )
        );

        var fault = Assert.IsType<DeviceFault>(Assert.Single(received));
        Assert.False(fault.Error.IsWarning);
        Assert.Equal(task_offset_t.TASK_OFFSET_BPM_CONTROLLER, fault.Error.Task);
        Assert.Equal(99u, fault.Timestamp);
    }

    [Fact]
    public void Decodes_DeviceReadyEvent()
    {
        var (parser, received) = NewParser();

        parser.Append(
            Wire.Message(
                usb_msg_type_t.USB_MSG_EVENT,
                task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                new usb_device_ready_event
                {
                    protocol_version = MessageConstants.USB_PROTOCOL_VERSION,
                }
            )
        );

        var ready = Assert.IsType<DeviceReady>(Assert.Single(received));
        Assert.Equal(MessageConstants.USB_PROTOCOL_VERSION, ready.Data.protocol_version);
    }

    [Theory]
    [InlineData(1u, true)]
    [InlineData(0u, false)]
    public void Decodes_SessionStateEvent(uint inSession, bool expected)
    {
        var (parser, received) = NewParser();

        parser.Append(
            Wire.Message(
                usb_msg_type_t.USB_MSG_EVENT,
                task_offset_t.TASK_OFFSET_SESSION_CONTROLLER,
                new session_state_event { timestamp = 4321, in_session = inSession }
            )
        );

        var session = Assert.IsType<SessionState>(Assert.Single(received));
        Assert.Equal(expected, session.InSession);
        Assert.Equal(4321u, session.Timestamp);
    }

    [Fact]
    public void Rejects_HostToDeviceMessageType_AsImplausible()
    {
        var (parser, received) = NewParser();

        // COMMAND is a host→device type and must never appear on the inbound stream; its header is
        // treated as garbage (not decoded to an UnknownMessage as it would be if merely "in range").
        parser.Append(
            Wire.Message(
                usb_msg_type_t.USB_MSG_COMMAND,
                task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                new usb_cmd_header_t { opcode = 0, msg_id = 1 }
            )
        );

        Assert.Empty(received);
    }

    [Fact]
    public void Resyncs_AfterLeadingGarbageByte()
    {
        var (parser, received) = NewParser();
        byte[] message = Wire.Message(
            usb_msg_type_t.USB_MSG_STREAM,
            task_offset_t.TASK_OFFSET_OPTICAL_ENCODER,
            new optical_encoder_output_data
            {
                timestamp = 7,
                angular_velocity = 1f,
                raw_value = 0,
                angular_acceleration = 0f,
            }
        );

        // A stray 0xEE ahead of a valid record makes the first header window implausible;
        // the parser should drop the byte, resync, and still decode the record.
        parser.Append([0xEE, .. message]);

        var sample = Assert.IsType<OpticalEncoderSample>(Assert.Single(received));
        Assert.Equal(7u, sample.Data.timestamp);
    }
}
