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
    public void Parses_SessionControllerStream()
    {
        var (parser, received) = NewParser();
        var payload = new session_controller_output_data
        {
            timestamp = 5678,
            torque = 2.5f,
            power = 31.25f,
        };

        parser.Append(
            Wire.Message(
                usb_msg_type_t.USB_MSG_STREAM,
                task_offset_t.TASK_OFFSET_SESSION_CONTROLLER,
                payload
            )
        );

        var sample = Assert.IsType<SessionControllerSample>(Assert.Single(received));
        Assert.Equal(5678u, sample.Data.timestamp);
        Assert.Equal(2.5f, sample.Data.torque);
        Assert.Equal(31.25f, sample.Data.power);
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

    [Fact]
    public void AHeaderWithAnImpossibleTaskOffset_IsDesync_NotAMessage()
    {
        var (parser, received) = NewParser();

        // Exactly what a real link produced: a RESPONSE header claiming task 252 and a 4-byte
        // payload. No task has that offset (they are 0, 0x10000, 0x20000 …) and a real
        // usb_response_data_t is 8 bytes, so these 12 bytes are a window onto the middle of some
        // other record — the tail of one that lost bytes. Believing it would cost us the 4 bytes
        // behind it too, and put a fictional message in front of the user.
        var bogus = new usb_msg_header_t
        {
            msg_type = usb_msg_type_t.USB_MSG_RESPONSE,
            task_offset = (task_offset_t)252,
            payload_len = 4,
        };
        byte[] real = Wire.Message(
            usb_msg_type_t.USB_MSG_STREAM,
            task_offset_t.TASK_OFFSET_OPTICAL_ENCODER,
            new optical_encoder_output_data { timestamp = 9 }
        );

        parser.Append([.. Wire.ToBytes(bogus), 1, 2, 3, 4, .. real]);

        // The garbage is skipped byte by byte and the record behind it still arrives.
        var sample = Assert.IsType<OpticalEncoderSample>(Assert.Single(received));
        Assert.Equal(9u, sample.Data.timestamp);
    }

    [Fact]
    public void AKnownRecordAtTheWrongSize_IsDesync_NotAMessage()
    {
        var (parser, received) = NewParser();

        // A RESPONSE is always 8 bytes (the firmware static-asserts it), so a 4-byte one is not a
        // short RESPONSE — it is not a RESPONSE. Handing it upward as an undecodable frame would be
        // reporting a message the device never sent.
        var wrongSize = new usb_msg_header_t
        {
            msg_type = usb_msg_type_t.USB_MSG_RESPONSE,
            task_offset = task_offset_t.TASK_OFFSET_USB_CONTROLLER,
            payload_len = 4,
        };
        byte[] real = Wire.Message(
            usb_msg_type_t.USB_MSG_STREAM,
            task_offset_t.TASK_OFFSET_OPTICAL_ENCODER,
            new optical_encoder_output_data { timestamp = 11 }
        );

        parser.Append([.. Wire.ToBytes(wrongSize), 1, 2, 3, 4, .. real]);

        var sample = Assert.IsType<OpticalEncoderSample>(Assert.Single(received));
        Assert.Equal(11u, sample.Data.timestamp);
    }

    [Fact]
    public void ARecordWeHaveNoDecoderFor_IsStillSurfaced()
    {
        var (parser, received) = NewParser();

        // The other reason a frame fails to decode: it is real, from a real task, and this host
        // simply has no decoder for it (a STATUS frame — nothing reads those yet). That is worth
        // showing, and it is exactly what must not be confused with a desync.
        var status = new usb_msg_header_t
        {
            msg_type = usb_msg_type_t.USB_MSG_STATUS,
            task_offset = task_offset_t.TASK_OFFSET_PID_CONTROLLER,
            payload_len = 4,
        };

        parser.Append([.. Wire.ToBytes(status), 1, 2, 3, 4]);

        var unknown = Assert.IsType<UnknownMessage>(Assert.Single(received));
        Assert.Equal(usb_msg_type_t.USB_MSG_STATUS, unknown.Header.msg_type);
        Assert.Equal(4u, unknown.Header.payload_len);
    }

    [Fact]
    public void DroppedBytes_AreReported_NotSwallowed()
    {
        var parser = new StreamParser();
        var received = new List<DeviceMessage>();
        var resyncs = new List<ResyncDetails>();
        parser.MessageReceived += received.Add;
        parser.Resynced += resyncs.Add;

        byte[] message = Wire.Message(
            usb_msg_type_t.USB_MSG_STREAM,
            task_offset_t.TASK_OFFSET_OPTICAL_ENCODER,
            new optical_encoder_output_data { timestamp = 3 }
        );

        // Three bytes of a lost record's tail ahead of a good one. The stream has no CRC and no
        // framing, so this silent loss is the only thing that ever tells anyone it happened.
        parser.Append([0xEE, 0xEE, 0xEE, .. message]);

        Assert.IsType<OpticalEncoderSample>(Assert.Single(received));
        var resync = Assert.Single(resyncs);
        Assert.Equal(3, resync.BytesDropped);
        Assert.Equal([0xEE, 0xEE, 0xEE], resync.SkippedBytes);
        Assert.Null(resync.LastGoodHeader); // nothing decoded yet: lost at stream start
        Assert.Equal(task_offset_t.TASK_OFFSET_OPTICAL_ENCODER, resync.NextHeader.task_offset);
    }

    [Fact]
    public void OneDesync_SpreadOverManyReads_IsOneWarningWithTheTotal()
    {
        var parser = new StreamParser();
        var received = new List<DeviceMessage>();
        var resyncs = new List<ResyncDetails>();
        parser.MessageReceived += received.Add;
        parser.Resynced += resyncs.Add;

        byte[] message = Wire.Message(
            usb_msg_type_t.USB_MSG_STREAM,
            task_offset_t.TASK_OFFSET_OPTICAL_ENCODER,
            new optical_encoder_output_data { timestamp = 5 }
        );

        // The serial port hands bytes over in whatever chunks it feels like, so one lost record gets
        // re-scanned across several Appends. That is one fault, and the user should be told once —
        // not once per chunk the garbage happened to be split into.
        parser.Append([0xEE, 0xEE, 0xEE, 0xEE]);
        parser.Append([0xEE, 0xEE, 0xEE, 0xEE]);
        parser.Append([0xEE, 0xEE, .. message]);

        Assert.IsType<OpticalEncoderSample>(Assert.Single(received));
        Assert.Equal(10, Assert.Single(resyncs).BytesDropped);
    }

    [Fact]
    public void ResyncDetails_NameTheRecordsAroundTheLoss()
    {
        var parser = new StreamParser();
        var resyncs = new List<ResyncDetails>();
        parser.MessageReceived += _ => { };
        parser.Resynced += resyncs.Add;

        byte[] before = Wire.Message(
            usb_msg_type_t.USB_MSG_STREAM,
            task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115,
            new forcesensor_output_data { timestamp = 1 }
        );
        byte[] after = Wire.Message(
            usb_msg_type_t.USB_MSG_STREAM,
            task_offset_t.TASK_OFFSET_OPTICAL_ENCODER,
            new optical_encoder_output_data { timestamp = 2 }
        );

        // A good force record, then garbage (a cut-off record), then a good encoder record: the
        // report should name both neighbors — that context is what makes the loss debuggable.
        parser.Append([.. before, 0xEE, 0xEE, .. after]);

        var resync = Assert.Single(resyncs);
        Assert.Equal([0xEE, 0xEE], resync.SkippedBytes);
        Assert.Equal(
            task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115,
            Assert.NotNull(resync.LastGoodHeader).task_offset
        );
        Assert.Equal(task_offset_t.TASK_OFFSET_OPTICAL_ENCODER, resync.NextHeader.task_offset);
    }
}
