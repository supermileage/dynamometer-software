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
    public void ADerivedQuantityStreamIsNoLongerRecognised()
    {
        // Protocol v6 withdrew session_controller_output_data: torque and power are the host's to
        // derive. A frame still claiming that task offset is foreign traffic, and must surface as
        // Unknown rather than being decoded into something plausible.
        var (parser, received) = NewParser();

        parser.Append(
            Wire.Message(
                usb_msg_type_t.USB_MSG_STREAM,
                task_offset_t.TASK_OFFSET_SESSION_CONTROLLER,
                new bpm_output_data
                {
                    timestamp = 5678,
                    duty_cycle = 2.5f,
                    raw_value = 0,
                }
            )
        );

        Assert.IsType<UnknownMessage>(Assert.Single(received));
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
    public void AValidFrame_OfAHostToDeviceType_SurfacesAsUnknown_NotAsADesync()
    {
        var (parser, received) = NewParser();

        // COMMAND is a host→device type that should never arrive inbound — but this frame's CRC
        // is valid, so *something* really sent it. That's worth surfacing as an undecoded frame,
        // not silently eating as if it were line noise.
        parser.Append(
            Wire.Message(
                usb_msg_type_t.USB_MSG_COMMAND,
                task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                new usb_cmd_header_t { opcode = 0, msg_id = 1 }
            )
        );

        var unknown = Assert.IsType<UnknownMessage>(Assert.Single(received));
        Assert.Equal(usb_msg_type_t.USB_MSG_COMMAND, unknown.Header.msg_type);
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
    public void UnframedRecordBytes_AreGarbageNow_AndTheFrameBehindThemStillArrives()
    {
        var (parser, received) = NewParser();

        // A whole v4-style record (bare header + payload, no SOF, no CRC) ahead of a real frame —
        // what a v4 firmware would send, or what a window onto lost bytes looks like. None of it
        // carries a SOF, so all of it is skipped and the framed record behind it still decodes.
        var bare = new usb_msg_header_t
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

        parser.Append([.. Wire.ToBytes(bare), 1, 2, 3, 4, .. real]);

        var sample = Assert.IsType<OpticalEncoderSample>(Assert.Single(received));
        Assert.Equal(9u, sample.Data.timestamp);
    }

    [Fact]
    public void ACorruptedFrame_FailsItsCrc_AndIsReportedAsLoss_NotDecoded()
    {
        var parser = new StreamParser();
        var received = new List<DeviceMessage>();
        var resyncs = new List<ResyncDetails>();
        parser.MessageReceived += received.Add;
        parser.Resynced += resyncs.Add;

        byte[] corrupted = Wire.Message(
            usb_msg_type_t.USB_MSG_STREAM,
            task_offset_t.TASK_OFFSET_OPTICAL_ENCODER,
            new optical_encoder_output_data { timestamp = 9, angular_velocity = 3.5f }
        );
        corrupted[^5] ^= 0xFF; // flip a payload byte: the CRC no longer matches
        byte[] good = Wire.Message(
            usb_msg_type_t.USB_MSG_STREAM,
            task_offset_t.TASK_OFFSET_OPTICAL_ENCODER,
            new optical_encoder_output_data { timestamp = 10 }
        );

        parser.Append([.. corrupted, .. good]);

        // The corrupted frame must not become a message — a wrong velocity shown as real data is
        // worse than a gap — and its full length is reported as loss.
        var sample = Assert.IsType<OpticalEncoderSample>(Assert.Single(received));
        Assert.Equal(10u, sample.Data.timestamp);
        Assert.Equal(corrupted.Length, Assert.Single(resyncs).BytesDropped);
    }

    [Fact]
    public void ARecordWeHaveNoDecoderFor_IsStillSurfaced()
    {
        var (parser, received) = NewParser();

        // A CRC-valid frame this host has no decoder for (a STATUS frame — nothing reads those
        // yet). That is worth showing, and it is exactly what must not be confused with a desync.
        parser.Append(
            Wire.MessageRaw(
                usb_msg_type_t.USB_MSG_STATUS,
                task_offset_t.TASK_OFFSET_PID_CONTROLLER,
                [1, 2, 3, 4]
            )
        );

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
