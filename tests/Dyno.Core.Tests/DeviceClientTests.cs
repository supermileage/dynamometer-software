using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using Dyno.Core;
using Dyno.Core.Messages;
using Dyno.Core.Protocol;
using Dyno.Core.Serial;
using Xunit;

namespace Dyno.Core.Tests;

public class DeviceClientTests
{
    /// <summary>
    /// In-memory full-duplex serial fake: the device→host direction is a <see cref="Pipe"/> the
    /// test pushes bytes into (read by the client's read loop), and host→device writes are
    /// captured for inspection.
    /// </summary>
    private sealed class FakeSerial : ISerialConnection
    {
        private readonly Pipe _deviceToHost = new();

        public string PortName => "FAKE";
        public bool IsOpen { get; private set; }
        public Stream BaseStream { get; }

        /// <summary>Invoked synchronously on each host→device write with the frame bytes.</summary>
        public Action<byte[]>? OnWrite;
        public List<byte[]> Writes { get; } = new();

        public FakeSerial() => BaseStream = _deviceToHost.Reader.AsStream();

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void Write(ReadOnlySpan<byte> data)
        {
            byte[] copy = data.ToArray();
            Writes.Add(copy);
            OnWrite?.Invoke(copy);
        }

        /// <summary>Push device→host bytes; the client's read loop picks them up next iteration.</summary>
        public void DeviceSend(ReadOnlySpan<byte> data) =>
            _deviceToHost.Writer.WriteAsync(data.ToArray()).AsTask().GetAwaiter().GetResult();

        public void Dispose() { }
    }

    private static usb_cmd_header_t ReadCommandHeader(byte[] frame)
    {
        // Framed command: [u16 SOF][usb_msg_header_t][usb_cmd_header_t][body][u16 crc].
        var span = frame.AsSpan();
        return MemoryMarshal.Read<usb_cmd_header_t>(
            span.Slice(sizeof(ushort) + UsbFrame.HeaderSize, UsbFrame.CommandHeaderSize)
        );
    }

    private static usb_msg_header_t ReadMessageHeader(byte[] frame) =>
        MemoryMarshal.Read<usb_msg_header_t>(frame.AsSpan(sizeof(ushort), UsbFrame.HeaderSize));

    [Fact]
    public void Handshakes_OnDeviceReady_AndAcksWithMatchingVersion()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        using var handshaked = new ManualResetEventSlim();
        client.Handshaked += () => handshaked.Set();

        // When the host writes its ACK, reply with the firmware's RESPONSE so the round-trip closes.
        serial.OnWrite = frame =>
        {
            var cmd = ReadCommandHeader(frame);
            serial.DeviceSend(
                Wire.Message(
                    usb_msg_type_t.USB_MSG_RESPONSE,
                    task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                    new usb_response_data_t
                    {
                        opcode = cmd.opcode,
                        msg_id = cmd.msg_id,
                        status = (uint)usb_response_status_t.USB_RSP_OK,
                    }
                )
            );
        };

        client.Start();
        serial.DeviceSend(
            Wire.Message(
                usb_msg_type_t.USB_MSG_EVENT,
                task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                new usb_device_ready_event
                {
                    protocol_version = MessageConstants.USB_PROTOCOL_VERSION,
                }
            )
        );

        Assert.True(handshaked.Wait(TimeSpan.FromSeconds(5)), "handshake did not complete");
        Assert.True(client.IsHandshaked);

        // The host's first write is a framed USB_CMD_ACK to the USB controller carrying its version.
        var msgHeader = ReadMessageHeader(serial.Writes[0]);
        var cmdHeader = ReadCommandHeader(serial.Writes[0]);
        Assert.Equal(usb_msg_type_t.USB_MSG_COMMAND, msgHeader.msg_type);
        Assert.Equal(task_offset_t.TASK_OFFSET_USB_CONTROLLER, msgHeader.task_offset);
        Assert.Equal((ushort)usb_controller_command_t.USB_CMD_ACK, cmdHeader.opcode);

        // body == host protocol version
        var body = serial
            .Writes[0]
            .AsSpan(
                sizeof(ushort) + UsbFrame.HeaderSize + UsbFrame.CommandHeaderSize,
                sizeof(uint)
            );
        Assert.Equal(MessageConstants.USB_PROTOCOL_VERSION, BitConverter.ToUInt32(body));
    }

    /// <summary>Pushes the device-ready announce that kicks off the host's handshake.</summary>
    private static void AnnounceReady(FakeSerial serial) =>
        serial.DeviceSend(
            Wire.Message(
                usb_msg_type_t.USB_MSG_EVENT,
                task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                new usb_device_ready_event
                {
                    protocol_version = MessageConstants.USB_PROTOCOL_VERSION,
                }
            )
        );

    /// <summary>A client whose heartbeat runs fast enough to assert on in a test.</summary>
    private static DeviceClient FastHeartbeatClient(FakeSerial serial) =>
        new(serial)
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(150),
            HeartbeatMissesBeforeLost = 2,
        };

    [Fact]
    public void Handshake_ProbesADeviceThatNeverAnnounces_AsOnAReconnect()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial)
        {
            HandshakeProbeInterval = TimeSpan.FromMilliseconds(100),
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
        };

        // A board that is already _appReady from a previous connect: it answers commands but never
        // announces device-ready again (the firmware only announces while un-acked, and nothing
        // clears that flag when the host goes away). This is the reconnect case.
        ReplyWithStatus(serial, usb_response_status_t.USB_RSP_OK);

        using var handshaked = new ManualResetEventSlim();
        client.Handshaked += () => handshaked.Set();

        client.Start();

        Assert.True(
            handshaked.Wait(TimeSpan.FromSeconds(5)),
            "an unannounced (already-acked) device was never handshaked"
        );
        Assert.True(client.IsHandshaked);

        // It was the host that opened its mouth: an unsolicited USB_CMD_ACK to the USB controller.
        var msgHeader = ReadMessageHeader(serial.Writes[0]);
        var cmdHeader = ReadCommandHeader(serial.Writes[0]);
        Assert.Equal(task_offset_t.TASK_OFFSET_USB_CONTROLLER, msgHeader.task_offset);
        Assert.Equal((ushort)usb_controller_command_t.USB_CMD_ACK, cmdHeader.opcode);
    }

    [Fact]
    public void Handshake_IsNotProbed_WhenTheDeviceAnnouncesItself()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial)
        {
            HandshakeProbeInterval = TimeSpan.FromSeconds(30), // never due within this test
        };
        ReplyWithStatus(serial, usb_response_status_t.USB_RSP_OK);

        using var handshaked = new ManualResetEventSlim();
        client.Handshaked += () => handshaked.Set();

        client.Start();
        AnnounceReady(serial);

        // A freshly-booted board announces every 200ms, so it handshakes long before the first
        // probe would be due: the probe is a fallback, not the normal path.
        Assert.True(handshaked.Wait(TimeSpan.FromSeconds(5)), "handshake did not complete");
    }

    [Fact]
    public void Heartbeat_ReportsEachAnsweredPing_WithItsRoundTrip()
    {
        using var serial = new FakeSerial();
        using var client = FastHeartbeatClient(serial);
        ReplyWithStatus(serial, usb_response_status_t.USB_RSP_OK);

        using var pinged = new CountdownEvent(3);
        var roundTrips = new ConcurrentQueue<TimeSpan>();
        client.HeartbeatAcked += rtt =>
        {
            roundTrips.Enqueue(rtt);
            if (!pinged.IsSet)
            {
                pinged.Signal();
            }
        };

        client.Start();
        AnnounceReady(serial);

        Assert.True(pinged.Wait(TimeSpan.FromSeconds(5)), "answered pings were not reported");
        Assert.All(roundTrips, rtt => Assert.True(rtt >= TimeSpan.Zero));
    }

    [Fact]
    public void Handshake_TimesOut_WhenNothingAnswersEvenAProbe()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial)
        {
            HandshakeProbeInterval = TimeSpan.FromMilliseconds(50),
            CommandTimeout = TimeSpan.FromMilliseconds(50),
            HandshakeTimeout = TimeSpan.FromMilliseconds(150),
        };

        using var timedOut = new ManualResetEventSlim();
        client.HandshakeTimedOut += () => timedOut.Set();

        // Opening a port succeeds against anything that enumerates, so a wedged (or wrong) board
        // looks exactly like this: it neither announces itself nor answers when asked.
        client.Start();

        Assert.True(
            timedOut.Wait(TimeSpan.FromSeconds(5)),
            "a silent device was never reported as a handshake timeout"
        );
        Assert.False(client.IsHandshaked);
        Assert.NotEmpty(serial.Writes); // it was asked (probed) before being written off
    }

    [Fact]
    public void Handshake_DoesNotTimeOut_WhenTheDeviceAnnouncesInTime()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial)
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(200),
        };
        ReplyWithStatus(serial, usb_response_status_t.USB_RSP_OK);

        using var handshaked = new ManualResetEventSlim();
        using var timedOut = new ManualResetEventSlim();
        client.Handshaked += () => handshaked.Set();
        client.HandshakeTimedOut += () => timedOut.Set();

        client.Start();
        AnnounceReady(serial);

        Assert.True(handshaked.Wait(TimeSpan.FromSeconds(5)), "handshake did not complete");
        // Wait out the deadline: a handshake that already succeeded must not then be called late.
        Assert.False(timedOut.Wait(TimeSpan.FromMilliseconds(500)), "timed out after handshaking");
    }

    [Fact]
    public void Handshake_DoesNotAlsoReportATimeout_WhenTheVersionMismatched()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial)
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(150),
        };

        using var mismatched = new ManualResetEventSlim();
        using var timedOut = new ManualResetEventSlim();
        client.ProtocolMismatch += _ => mismatched.Set();
        client.HandshakeTimedOut += () => timedOut.Set();

        client.Start();
        serial.DeviceSend(
            Wire.Message(
                usb_msg_type_t.USB_MSG_EVENT,
                task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                new usb_device_ready_event
                {
                    protocol_version = MessageConstants.USB_PROTOCOL_VERSION + 99,
                }
            )
        );

        Assert.True(mismatched.Wait(TimeSpan.FromSeconds(5)), "mismatch was not reported");
        // The mismatch is a definitive answer the caller has already been given; following it with
        // a timeout would report the same failure twice under two different names.
        Assert.False(
            timedOut.Wait(TimeSpan.FromMilliseconds(500)),
            "a refused version was also reported as a timeout"
        );
    }

    [Fact]
    public void Heartbeat_KeepsPollingTheDevice_AfterTheHandshake()
    {
        using var serial = new FakeSerial();
        using var client = FastHeartbeatClient(serial);

        // The handshake ack plus three keep-alive pings: the link keeps asking whether the device
        // is still there rather than assuming an open port means a live device.
        using var polled = new CountdownEvent(4);
        serial.OnWrite = frame =>
        {
            var msg = ReadMessageHeader(frame);
            var cmd = ReadCommandHeader(frame);
            if (
                msg.task_offset != task_offset_t.TASK_OFFSET_USB_CONTROLLER
                || cmd.opcode != (ushort)usb_controller_command_t.USB_CMD_ACK
            )
            {
                return;
            }
            if (!polled.IsSet)
            {
                polled.Signal();
            }
            serial.DeviceSend(
                Wire.Message(
                    usb_msg_type_t.USB_MSG_RESPONSE,
                    task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                    new usb_response_data_t
                    {
                        opcode = cmd.opcode,
                        msg_id = cmd.msg_id,
                        status = (uint)usb_response_status_t.USB_RSP_OK,
                    }
                )
            );
        };

        client.Start();
        AnnounceReady(serial);

        Assert.True(polled.Wait(TimeSpan.FromSeconds(5)), "the link stopped polling the device");
        Assert.True(client.IsHandshaked);
    }

    [Fact]
    public void Heartbeat_ReportsConnectionLost_WhenTheDeviceStopsAnswering()
    {
        using var serial = new FakeSerial();
        using var client = FastHeartbeatClient(serial);

        // Answer the handshake, then go silent — a device that died (or was unplugged behind a
        // still-open port) simply stops acking, and no telemetry is due to give it away.
        int acks = 0;
        serial.OnWrite = frame =>
        {
            var cmd = ReadCommandHeader(frame);
            if (Interlocked.Increment(ref acks) > 1)
            {
                return;
            }
            serial.DeviceSend(
                Wire.Message(
                    usb_msg_type_t.USB_MSG_RESPONSE,
                    task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                    new usb_response_data_t
                    {
                        opcode = cmd.opcode,
                        msg_id = cmd.msg_id,
                        status = (uint)usb_response_status_t.USB_RSP_OK,
                    }
                )
            );
        };

        using var lost = new ManualResetEventSlim();
        client.ConnectionLost += () => lost.Set();

        client.Start();
        AnnounceReady(serial);

        Assert.True(lost.Wait(TimeSpan.FromSeconds(5)), "a silent device was never reported lost");
        Assert.False(client.IsHandshaked);
    }

    [Fact]
    public void Heartbeat_RestoresTheLink_WhenTheDeviceAnswersAgain()
    {
        using var serial = new FakeSerial();
        using var client = FastHeartbeatClient(serial);

        // Answer, go silent long enough to be declared lost, then answer again: the poll keeps
        // running through the outage, so the returning device is picked up without a reconnect.
        var silent = true;
        serial.OnWrite = frame =>
        {
            var cmd = ReadCommandHeader(frame);
            if (Volatile.Read(ref silent) && cmd.msg_id > 1)
            {
                return; // msg_id 1 is the handshake; drop the pings that follow it
            }
            serial.DeviceSend(
                Wire.Message(
                    usb_msg_type_t.USB_MSG_RESPONSE,
                    task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                    new usb_response_data_t
                    {
                        opcode = cmd.opcode,
                        msg_id = cmd.msg_id,
                        status = (uint)usb_response_status_t.USB_RSP_OK,
                    }
                )
            );
        };

        using var lost = new ManualResetEventSlim();
        using var handshaked = new CountdownEvent(2); // initial handshake, then the recovery
        client.ConnectionLost += () => lost.Set();
        client.Handshaked += () =>
        {
            if (!handshaked.IsSet)
            {
                handshaked.Signal();
            }
        };

        client.Start();
        AnnounceReady(serial);

        Assert.True(lost.Wait(TimeSpan.FromSeconds(5)), "a silent device was never reported lost");
        Volatile.Write(ref silent, false); // the device comes back

        Assert.True(
            handshaked.Wait(TimeSpan.FromSeconds(5)),
            "the returning device was never picked back up"
        );
        Assert.True(client.IsHandshaked);
    }

    [Fact]
    public void RefusesHandshake_OnVersionMismatch_AndDoesNotAck()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        using var mismatched = new ManualResetEventSlim();
        uint? reported = null;
        client.ProtocolMismatch += v =>
        {
            reported = v;
            mismatched.Set();
        };

        client.Start();
        serial.DeviceSend(
            Wire.Message(
                usb_msg_type_t.USB_MSG_EVENT,
                task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                new usb_device_ready_event
                {
                    protocol_version = MessageConstants.USB_PROTOCOL_VERSION + 99,
                }
            )
        );

        Assert.True(mismatched.Wait(TimeSpan.FromSeconds(5)), "mismatch was not reported");
        // The announcement carries the device's version, so the caller learns which one it speaks.
        Assert.Equal(MessageConstants.USB_PROTOCOL_VERSION + 99, reported);
        Assert.False(client.IsHandshaked);
        Assert.Empty(serial.Writes); // never acked an incompatible device
    }

    [Fact]
    public void ReceivesTelemetry_ThroughReadLoop_RaisingDecodedMessages()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        var received = new ConcurrentQueue<DeviceMessage>();
        using var gotBoth = new CountdownEvent(2);
        client.MessageReceived += m =>
        {
            received.Enqueue(m);
            gotBoth.Signal();
        };

        client.Start();

        // A telemetry stream sample...
        byte[] encoder = Wire.Message(
            usb_msg_type_t.USB_MSG_STREAM,
            task_offset_t.TASK_OFFSET_OPTICAL_ENCODER,
            new optical_encoder_output_data
            {
                timestamp = 55,
                angular_velocity = 2.5f,
                raw_value = 3,
                angular_acceleration = 0.5f,
            }
        );

        // ...and an error frame, delivered split across two writes so the client-side buffering
        // (read loop → parser) has to reassemble a record that spans reads, not just the parser.
        uint code =
            (uint)task_offset_t.TASK_OFFSET_BPM_CONTROLLER
            | (uint)bpm_task_error_ids.ERROR_BPM_PWM_STOP_FAILURE;
        byte[] fault = Wire.Message(
            usb_msg_type_t.USB_MSG_ERROR,
            task_offset_t.TASK_OFFSET_BPM_CONTROLLER,
            new task_error_data { timestamp = 77, error_code = code }
        );

        serial.DeviceSend(encoder);
        serial.DeviceSend(fault.AsSpan(0, 6));
        serial.DeviceSend(fault.AsSpan(6));

        Assert.True(gotBoth.Wait(TimeSpan.FromSeconds(5)), "did not receive both messages");

        Assert.Equal(2, received.Count);
        Assert.True(received.TryDequeue(out var first));
        Assert.True(received.TryDequeue(out var second));

        var sample = Assert.IsType<OpticalEncoderSample>(first);
        Assert.Equal(55u, sample.Data.timestamp);
        Assert.Equal(2.5f, sample.Data.angular_velocity);
        Assert.Equal(0.5f, sample.Data.angular_acceleration);

        var decodedFault = Assert.IsType<DeviceFault>(second);
        Assert.False(decodedFault.Error.IsWarning);
        Assert.Equal(task_offset_t.TASK_OFFSET_BPM_CONTROLLER, decodedFault.Error.Task);
        Assert.Equal(
            (uint)bpm_task_error_ids.ERROR_BPM_PWM_STOP_FAILURE,
            decodedFault.Error.Number
        );
        Assert.Equal(77u, decodedFault.Timestamp);
    }

    /// <summary>Replies to any host command with the given status, echoing opcode/msg_id.</summary>
    private static void ReplyWithStatus(FakeSerial serial, usb_response_status_t status)
    {
        serial.OnWrite = frame =>
        {
            var msg = ReadMessageHeader(frame);
            if (msg.msg_type != usb_msg_type_t.USB_MSG_COMMAND)
            {
                return;
            }
            var cmd = ReadCommandHeader(frame);
            serial.DeviceSend(
                Wire.Message(
                    usb_msg_type_t.USB_MSG_RESPONSE,
                    msg.task_offset,
                    new usb_response_data_t
                    {
                        opcode = cmd.opcode,
                        msg_id = cmd.msg_id,
                        status = (uint)status,
                    }
                )
            );
        };
    }

    [Fact]
    public void ReadLoop_SurvivesAThrowingConsumer_AndKeepsDelivering()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        using var gotSecond = new ManualResetEventSlim();
        var delivered = new ConcurrentQueue<uint>();
        bool threwOnce = false;
        client.MessageReceived += m =>
        {
            if (m is not OpticalEncoderSample s)
            {
                return;
            }
            if (!threwOnce)
            {
                threwOnce = true;
                throw new InvalidOperationException("boom on the first sample");
            }
            delivered.Enqueue(s.Data.timestamp);
            gotSecond.Set();
        };

        client.Start();

        // Both records may coalesce into one read (one parser Append). The first consumer throws;
        // the link must still decode and deliver the second rather than dying or replaying the first.
        serial.DeviceSend(
            Wire.Message(
                usb_msg_type_t.USB_MSG_STREAM,
                task_offset_t.TASK_OFFSET_OPTICAL_ENCODER,
                new optical_encoder_output_data { timestamp = 1 }
            )
        );
        serial.DeviceSend(
            Wire.Message(
                usb_msg_type_t.USB_MSG_STREAM,
                task_offset_t.TASK_OFFSET_OPTICAL_ENCODER,
                new optical_encoder_output_data { timestamp = 2 }
            )
        );

        Assert.True(
            gotSecond.Wait(TimeSpan.FromSeconds(5)),
            "read loop died after a consumer threw"
        );
        Assert.True(client.IsRunning);
        Assert.True(delivered.TryDequeue(out uint ts));
        Assert.Equal(2u, ts);
    }

    [Fact]
    public async Task SendCommand_PropagatesWriteFailure_AndDoesNotLeakThePendingSlot()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);
        serial.OnWrite = _ => throw new IOException("port write failed");

        client.Start();

        await Assert.ThrowsAsync<IOException>(() =>
            client.SendCommandAsync(
                task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115,
                (ushort)0,
                [0],
                timeout: TimeSpan.FromMilliseconds(200)
            )
        );

        // If the failed write had leaked its pending slot, the next command's reply (which echoes a
        // fresh msg_id) would still correlate and complete; a clean slate means this round-trips.
        serial.OnWrite = null;
        ReplyWithStatus(serial, usb_response_status_t.USB_RSP_OK);
        var response = await client.SendCommandAsync(
            task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115,
            (ushort)0,
            [0]
        );
        Assert.Equal((uint)usb_response_status_t.USB_RSP_OK, response.status);
    }

    [Fact]
    public async Task SendCommand_ReturnsNonOkStatus_WhenNotStrict()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);
        ReplyWithStatus(serial, usb_response_status_t.USB_RSP_MALFORMED);

        client.Start();
        // Default (throwOnError: false) surfaces the status to the caller instead of throwing.
        var response = await client.SendCommandAsync(
            task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115,
            (ushort)0,
            [0]
        );
        Assert.Equal((uint)usb_response_status_t.USB_RSP_MALFORMED, response.status);
    }

    [Fact]
    public async Task SendCommand_RetriesOnTimeout_ThenSucceeds_WithAFreshMsgId()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        // Model a dropped completion ack: swallow the first command so it times out, then reply
        // OK to the retry. Records each command's msg_id to prove the retry uses a fresh one.
        var msgIds = new List<ushort>();
        int seen = 0;
        serial.OnWrite = frame =>
        {
            var msg = ReadMessageHeader(frame);
            if (msg.msg_type != usb_msg_type_t.USB_MSG_COMMAND)
            {
                return;
            }
            var cmd = ReadCommandHeader(frame);
            msgIds.Add(cmd.msg_id);
            if (Interlocked.Increment(ref seen) == 1)
            {
                return; // drop the first attempt -> forces the timeout
            }
            serial.DeviceSend(
                Wire.Message(
                    usb_msg_type_t.USB_MSG_RESPONSE,
                    msg.task_offset,
                    new usb_response_data_t
                    {
                        opcode = cmd.opcode,
                        msg_id = cmd.msg_id,
                        status = (uint)usb_response_status_t.USB_RSP_OK,
                    }
                )
            );
        };

        client.Start();
        var response = await client.SendCommandAsync(
            task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115,
            (ushort)0,
            [0],
            timeout: TimeSpan.FromMilliseconds(150),
            retries: 1
        );

        Assert.Equal((uint)usb_response_status_t.USB_RSP_OK, response.status);
        Assert.Equal(2, serial.Writes.Count); // original attempt + one retry
        Assert.NotEqual(msgIds[0], msgIds[1]); // the retry correlated on a fresh msg_id
    }

    [Fact]
    public async Task SendCommand_BoundsAnUnansweredCommand_ByCommandTimeout()
    {
        using var serial = new FakeSerial();
        // Never reply: every command is acked by the firmware, so silence is what CommandTimeout
        // exists to bound. The caller passes no per-call timeout, so this deadline is the one used.
        using var client = new DeviceClient(serial)
        {
            CommandTimeout = TimeSpan.FromMilliseconds(150),
        };

        client.Start();
        var started = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.SendCommandAsync(task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115, (ushort)0, [0])
        );

        // Well inside the 2s default, so the property is what bounded the wait, not a literal.
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(1),
            $"waited {started.Elapsed}, so CommandTimeout was not honoured"
        );
    }

    [Fact]
    public async Task SendCommand_CallerCancellation_SurfacesCanceled_AndIsNotRetried()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);
        // Never reply, so only the cancellation can end the wait.
        client.Start();

        using var cts = new CancellationTokenSource();
        var task = client.SendCommandAsync(
            task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115,
            (ushort)0,
            [0],
            timeout: TimeSpan.FromSeconds(5),
            retries: 3,
            cancellationToken: cts.Token
        );
        cts.Cancel();

        // Cancellation is a definitive stop, not a timeout: it must surface as canceled, and the
        // retry budget must not consume it (only the one original write went out).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Single(serial.Writes);
    }

    /// <summary>Pushes a session start/stop announcement from the device.</summary>
    private static void AnnounceSession(FakeSerial serial, bool inSession) =>
        serial.DeviceSend(
            Wire.Message(
                usb_msg_type_t.USB_MSG_EVENT,
                task_offset_t.TASK_OFFSET_SESSION_CONTROLLER,
                new session_state_event { timestamp = 7, in_session = inSession ? 1u : 0u }
            )
        );

    [Fact]
    public void TracksSessionState_FromDeviceAnnouncements()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        var states = new BlockingCollection<bool>();
        client.SessionStateChanged += states.Add;

        client.Start();
        Assert.False(client.IsSessionActive); // nothing known until the device says so

        AnnounceSession(serial, true);
        Assert.True(states.TryTake(out bool started, TimeSpan.FromSeconds(5)));
        Assert.True(started);
        Assert.True(client.IsSessionActive);

        AnnounceSession(serial, false);
        Assert.True(states.TryTake(out bool stopped, TimeSpan.FromSeconds(5)));
        Assert.False(stopped);
        Assert.False(client.IsSessionActive);
    }

    [Fact]
    public void RepeatedSessionAnnouncement_RaisesNoChangeEvent()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        int raised = 0;
        var seen = new BlockingCollection<bool>();
        client.SessionStateChanged += _ =>
        {
            Interlocked.Increment(ref raised);
            seen.Add(true);
        };

        client.Start();

        // The firmware re-states the session state after every host ack (its 5s keep-alive), so the
        // same value arrives over and over. Only a real transition is an event; the repeats must be
        // silent, or the UI would log a "session started" every heartbeat.
        AnnounceSession(serial, true);
        Assert.True(seen.TryTake(out _, TimeSpan.FromSeconds(5)));

        AnnounceSession(serial, true);
        AnnounceSession(serial, true);

        // Ordering: a message the client parses *after* the repeats is proof they have been handled,
        // so this cannot pass by racing ahead of them.
        var faults = new BlockingCollection<DeviceMessage>();
        client.MessageReceived += m =>
        {
            if (m is DeviceFault)
            {
                faults.Add(m);
            }
        };
        serial.DeviceSend(
            Wire.Message(
                usb_msg_type_t.USB_MSG_ERROR,
                task_offset_t.TASK_OFFSET_BPM_CONTROLLER,
                new task_error_data { timestamp = 1, error_code = 0 }
            )
        );
        Assert.True(faults.TryTake(out _, TimeSpan.FromSeconds(5)));

        Assert.Equal(1, Volatile.Read(ref raised));
        Assert.True(client.IsSessionActive);
    }

    /// <summary>
    /// A session event and the samples around it can land in a single read. The session state must
    /// therefore be applied <i>before</i> the samples that follow it are handed to consumers: the UI
    /// shows a sample only while it believes a session is running, so if the flag lagged the stream
    /// the first samples of every session would be discarded — and samples trailing a stop would be
    /// shown as live. Asserts the flag as each sample is delivered, not merely at the end.
    /// </summary>
    [Fact]
    public void SessionState_IsAppliedBeforeTheSamplesThatFollowIt()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        // (angular_velocity of the sample, IsSessionActive when it was delivered)
        var seen = new BlockingCollection<(float Velocity, bool InSession)>();
        client.MessageReceived += m =>
        {
            if (m is OpticalEncoderSample s)
            {
                seen.Add((s.Data.angular_velocity, client.IsSessionActive));
            }
        };

        client.Start();

        static byte[] Sample(float velocity) =>
            Wire.Message(
                usb_msg_type_t.USB_MSG_STREAM,
                task_offset_t.TASK_OFFSET_OPTICAL_ENCODER,
                new optical_encoder_output_data { angular_velocity = velocity }
            );

        static byte[] Session(bool inSession) =>
            Wire.Message(
                usb_msg_type_t.USB_MSG_EVENT,
                task_offset_t.TASK_OFFSET_SESSION_CONTROLLER,
                new session_state_event { in_session = inSession ? 1u : 0u }
            );

        // One contiguous read: start, a sample inside the session, stop, then a straggler framed
        // behind the stop — exactly the shape the firmware emits when a session ends mid-batch.
        serial.DeviceSend([.. Session(true), .. Sample(1f), .. Session(false), .. Sample(2f)]);

        Assert.True(seen.TryTake(out var inside, TimeSpan.FromSeconds(5)));
        Assert.Equal(1f, inside.Velocity);
        Assert.True(
            inside.InSession,
            "a sample inside the session was delivered with the flag off"
        );

        Assert.True(seen.TryTake(out var after, TimeSpan.FromSeconds(5)));
        Assert.Equal(2f, after.Velocity);
        Assert.False(
            after.InSession,
            "a sample after the stop was delivered with the flag still on"
        );
    }

    [Fact]
    public void ReadLoop_LosesNothingFromABurstOfSends()
    {
        // Bytes pile up in the port faster than the loop can take them out, and the one thing that
        // must not happen is losing any. This began as a test of the read-settle delay, at both
        // ends of that setting; the delay is gone but the invariant it was protecting is not.
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        const int samples = 20;
        var received = new ConcurrentQueue<DeviceMessage>();
        using var all = new CountdownEvent(samples);
        client.MessageReceived += m =>
        {
            received.Enqueue(m);
            all.Signal();
        };

        client.Start();
        for (uint i = 0; i < samples; i++)
        {
            serial.DeviceSend(
                Wire.Message(
                    usb_msg_type_t.USB_MSG_STREAM,
                    task_offset_t.TASK_OFFSET_OPTICAL_ENCODER,
                    new optical_encoder_output_data { timestamp = i, angular_velocity = i }
                )
            );
        }

        Assert.True(
            all.Wait(TimeSpan.FromSeconds(10)),
            $"only {received.Count} of {samples} arrived"
        );
        // In order, and each exactly once: the loop must not reorder or duplicate either.
        Assert.Equal(
            Enumerable.Range(0, samples).Select(i => (uint)i),
            received.Cast<OpticalEncoderSample>().Select(s => s.Data.timestamp)
        );
    }
}
