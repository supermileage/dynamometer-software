using System.Collections.Concurrent;
using Dyno.Core.Diagnostics;
using Dyno.Core.Messages;
using Dyno.Core.Protocol;
using Dyno.Core.Serial;
using Dyno.Core.SysConfig;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dyno.Core;

/// <summary>
/// Orchestrates a live device link: owns a serial connection, pumps its bytes through a
/// <see cref="StreamParser"/> on a background loop, re-publishes decoded
/// <see cref="DeviceMessage"/>s, and sends framed commands while correlating each
/// <see cref="CommandResponse"/> back to its request by <c>msg_id</c>.
/// </summary>
public sealed class DeviceClient : IDisposable
{
    /// <summary>A command awaiting its RESPONSE. <paramref name="Description"/> is what the command
    /// was, in words; it is held here (rather than in a second dictionary) so it is discarded on
    /// exactly the paths that discard the request itself — reply, timeout, failed write, link stop.
    /// </summary>
    private sealed record PendingCommand(
        TaskCompletionSource<usb_response_data_t> Completion,
        string? Description
    );

    private readonly ISerialConnection _connection;
    private readonly ILogger<DeviceClient> _log;

    /// <summary>TEMP DIAGNOSTIC (16-byte head-loss investigation): records the bytes this loop is
    /// handed, so a fault can be replayed against them offline. Null unless capture is enabled.</summary>
    private readonly RawCapture? _rawCapture;

    private readonly StreamParser _parser = new();
    private readonly ConcurrentDictionary<ushort, PendingCommand> _pending = new();

    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private Task? _heartbeatLoop;
    private Task? _handshakeWatchdog;
    private int _nextMsgId; // pre-incremented; first id handed out is 1 (0 is firmware-reserved)

    /// <summary>Raised on the read-loop thread for every decoded message. UI consumers must
    /// marshal to their own thread (e.g. Avalonia's Dispatcher).</summary>
    public event Action<DeviceMessage>? MessageReceived;

    /// <summary>Raised once the firmware accepts the host's <c>USB_CMD_ACK</c> (protocol versions
    /// agree). The device only streams after this. Also re-raised when a link that had gone silent
    /// answers the heartbeat again. Fires on the read-loop or heartbeat thread.</summary>
    public event Action? Handshaked;

    /// <summary>Raised when the device can't speak the host's protocol version. The argument is the
    /// device's version, or <c>null</c> when it rejected the host's ack without announcing its own.
    /// The link will not stream — the host and firmware schemas are out of sync.</summary>
    public event Action<uint?>? ProtocolMismatch;

    /// <summary>Raised on every answered keep-alive ping, with the round trip it took. Lets a
    /// consumer show that the link is alive (and how responsive it is) rather than inferring it
    /// from the absence of errors. Fires on the heartbeat thread.</summary>
    public event Action<TimeSpan>? HeartbeatAcked;

    /// <summary>Raised when the heartbeat stops getting answers: the device is no longer known to
    /// be there. The link is left running and keeps polling, so a device that comes back re-raises
    /// <see cref="Handshaked"/>. Fires on the heartbeat thread.</summary>
    public event Action? ConnectionLost;

    /// <summary>Raised when <see cref="HandshakeTimeout"/> elapses after <see cref="Start"/> with no
    /// completed handshake — an open port with nothing (or nothing that speaks this protocol) on the
    /// other end. The link keeps listening, so a device that shows up late still handshakes and
    /// raises <see cref="Handshaked"/>. Fires on a timer thread.</summary>
    public event Action? HandshakeTimedOut;

    /// <summary>Raised when the device announces that a session started or stopped — on each
    /// transition, and once just after the handshake so a host that connected to a steady board
    /// learns its state without waiting for an edge. Fires on the read-loop thread.</summary>
    public event Action<bool>? SessionStateChanged;

    /// <summary>Raised as a described command goes out, with that description — the opening half of
    /// the pair that <see cref="CommandResponse.Request"/> (or <see cref="CommandFailed"/>) closes.
    /// Without it, a command the device never answers leaves no trace at all: the absence of an ack
    /// is not something a reader can notice. Undescribed traffic (the handshake ack and the
    /// heartbeats built on it) stays silent, so this reports intent, not chatter. Fires on the
    /// caller's thread; retries do not re-raise it.</summary>
    /// <summary>Raised when the parser had to throw bytes away to regain alignment, with what was
    /// skipped and which records it sat between. The device→host stream carries no CRC and no
    /// framing, so bytes lost in transit (a full ring buffer on the board, most likely) do not
    /// announce themselves — they just shift everything after them, and this is the only place
    /// that shows up. Fires on the read-loop thread.</summary>
    public event Action<ResyncDetails>? StreamResynced;

    /// <summary>Raised when a CDC transfer's bytes did not match the trailer closing it — either it
    /// arrived short, or the device's sequence skipped transfers we never saw. Where
    /// <see cref="StreamResynced"/> says the stream lost data, this says which side lost it: the
    /// device's own count of what it handed the CDC driver is the reference. Fires on the read-loop
    /// thread.</summary>
    public event Action<BatchAccounting>? BatchMisaccounted;

    public event Action<string>? CommandSent;

    /// <summary>Raised when a described command runs out of attempts without an ack — a timeout, or
    /// a write that failed outright — with the description and the reason. A command the firmware
    /// answers with a rejection is <em>not</em> a failure here: that reply arrives as a
    /// <see cref="CommandResponse"/> carrying its non-OK status, and reporting it twice would say
    /// the device both refused and ignored it. Fires on the caller's thread.</summary>
    public event Action<string, Exception>? CommandFailed;

    /// <summary>True once the device-ready handshake (<c>USB_CMD_ACK</c> → <c>USB_RSP_OK</c>)
    /// completed, and while the device keeps answering the heartbeat.</summary>
    public bool IsHandshaked { get; private set; }

    /// <summary>Null until the device has stated its session state on this link. Distinct from
    /// false: "we have not been told" and "we have been told there is no session" are different
    /// facts, and conflating them is what would let the first announcement of an idle board be
    /// de-duplicated away as though it were a repeat of something we already knew.</summary>
    private bool? _sessionActive;

    /// <summary>Whether the dyno is currently running a session, per the device's last
    /// <see cref="SessionState"/> announcement. Sensor samples only stream during a session, so
    /// this is what separates "idle" from "dead". False until the device says otherwise — including
    /// after the link is lost, when the last-known state is no longer worth trusting.</summary>
    public bool IsSessionActive => _sessionActive ?? false;

    /// <summary>Whether the device has actually told us, as opposed to us assuming idle because it
    /// has not. The firmware states the session state after every <c>USB_CMD_ACK</c>, so this turns
    /// true a beat after the handshake and stays true for the life of the link.</summary>
    public bool IsSessionStateKnown => _sessionActive is not null;

    /// <summary>How long one attempt at any command waits for its RESPONSE before it is a
    /// <see cref="TimeoutException"/>. Every host→device command is acked by the firmware, so this
    /// is what stops an unanswered one from waiting forever; a caller can override it per call, and
    /// a retry budget multiplies it (see <see cref="SendCommandAsync"/>).</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long the device has, from <see cref="Start"/>, to announce itself and finish the
    /// handshake before the host reports it as unresponsive.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the host asks an unannounced device to handshake. Longer than the
    /// firmware's 200ms announce cadence, so a freshly-booted board handshakes on its own and is
    /// never probed.</summary>
    public TimeSpan HandshakeProbeInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How often, once handshaked, the host re-pings the device to confirm it's still there.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long one heartbeat ping may go unanswered before it counts as a miss.</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Consecutive missed pings before the link is declared lost. More than one so a
    /// single dropped ack (a momentarily full completion queue) isn't read as a disconnect.</summary>
    public int HeartbeatMissesBeforeLost { get; set; } = 2;

    private int _handshakeStarted; // 0 until we first act on a device-ready announcement
    private int _protocolRefused; // 1 once a version mismatch has been reported

    public DeviceClient(
        ISerialConnection connection,
        ILogger<DeviceClient>? logger = null,
        RawCapture? rawCapture = null
    )
    {
        _connection = connection;
        _log = logger ?? NullLogger<DeviceClient>.Instance;
        _rawCapture = rawCapture;
        _parser.MessageReceived += OnParsed;
        _parser.Resynced += OnResynced;
        _parser.BatchMisaccounted += OnBatchMisaccounted;
    }

    private void OnBatchMisaccounted(BatchAccounting batch)
    {
        if (batch.MissingTransfers > 0)
        {
            _log.LogWarning(
                "{Missing} USB transfer(s) never arrived before batch {Sequence}; the device sent them and the host never saw them",
                batch.MissingTransfers,
                batch.Sequence
            );
        }
        else
        {
            _log.LogWarning(
                "USB batch {Sequence} arrived {Shortfall} bytes short ({Observed} of {Declared}); the loss is below the firmware, not in its framing",
                batch.Sequence,
                batch.Shortfall,
                batch.ObservedBytes,
                batch.DeclaredBytes
            );
        }
        BatchMisaccounted?.Invoke(batch);
    }

    private void OnResynced(ResyncDetails details)
    {
        _log.LogWarning(
            "dropped {Bytes} bytes to resync the device stream (skipped: {Skipped}); the link is losing data",
            details.BytesDropped,
            Convert.ToHexString(details.SkippedBytes)
        );
        StreamResynced?.Invoke(details);
    }

    public bool IsRunning => _readLoop is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        IsHandshaked = false;
        _sessionActive = null; // nothing is known about the new device until it announces
        Interlocked.Exchange(ref _handshakeStarted, 0);
        Interlocked.Exchange(ref _protocolRefused, 0);
        _heartbeatLoop = null;

        if (!_connection.IsOpen)
        {
            _connection.Open();
        }

        _cts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        _handshakeWatchdog = Task.Run(() => WatchHandshakeAsync(_cts.Token), _cts.Token);
        _log.LogInformation("Device link started on {Port}", _connection.PortName);
    }

    public void Stop()
    {
        _cts?.Cancel();
        // Close before joining: SerialPort.BaseStream.ReadAsync can ignore the cancellation token,
        // and closing the port is what unblocks a pending read so the loop can actually exit within
        // the wait below (rather than the wait timing out with the loop still live).
        _connection.Close();
        try
        {
            _readLoop?.Wait(TimeSpan.FromSeconds(1));
            _heartbeatLoop?.Wait(TimeSpan.FromSeconds(1));
            _handshakeWatchdog?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // loops cancelled; nothing to surface
        }
        FailPending(new OperationCanceledException("device link stopped"));
    }

    /// <summary>
    /// Sends a framed command (or config) and awaits the firmware's RESPONSE, correlated by
    /// <c>msg_id</c>. Throws <see cref="TimeoutException"/> if no matching reply arrives within
    /// <paramref name="timeout"/> (defaulting to <see cref="CommandTimeout"/>) — each attempt gets
    /// that long, so a retry budget multiplies the worst-case wait.
    /// When <paramref name="throwOnError"/> is set, a RESPONSE whose status is not
    /// <c>USB_RSP_OK</c> is treated as a failure and raised as <see cref="DeviceCommandException"/>
    /// rather than returned — so callers that only care "did it apply?" need not inspect the status.
    /// <paramref name="description"/> says what the command is in words; it rides along to the
    /// matching <see cref="CommandResponse.Request"/> so the ack can be read without decoding an
    /// opcode and a msg_id by hand.
    /// </summary>
    public async Task<usb_response_data_t> SendCommandAsync(
        task_offset_t target,
        ushort opcode,
        byte[]? body = null,
        string? description = null,
        usb_msg_type_t type = usb_msg_type_t.USB_MSG_COMMAND,
        bool throwOnError = false,
        TimeSpan? timeout = null,
        int retries = 0,
        CancellationToken cancellationToken = default
    )
    {
        // Announced once for the whole command, not once per attempt: the retries below are this
        // method's business, and a caller told "sending K_P = 2.5" three times would read it as
        // three writes.
        if (description is not null)
        {
            CommandSent?.Invoke(description);
        }

        // A routed command's ack is the owning task's completion relayed back (ProcessCompletions).
        // If that completion is dropped (e.g. its queue was momentarily full) the reply never comes
        // and the attempt times out even though the command may have applied. A bounded retry with a
        // fresh msg_id recovers the ack. Only opt in for *idempotent* commands: on a false timeout the
        // command already applied, so a retry re-applies it. A non-OK status or caller cancellation is
        // a definitive answer, not a timeout, so neither is retried.
        int attempts = 1 + Math.Max(0, retries);
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await SendOnceAsync(
                            target,
                            opcode,
                            body,
                            description,
                            type,
                            throwOnError,
                            timeout,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
                catch (TimeoutException) when (attempt < attempts)
                {
                    _log.LogWarning(
                        "no RESPONSE for opcode {Opcode} (attempt {Attempt}/{Attempts}); retrying with a fresh msg_id",
                        opcode,
                        attempt,
                        attempts
                    );
                }
            }
        }
        catch (Exception ex)
            when (description is not null
                && ex is not (OperationCanceledException or DeviceCommandException)
            )
        {
            // Out of attempts with nothing to show for it. A rejection is excluded because the
            // device did answer — that reply reports itself — and a cancellation is the caller (or
            // the link) stopping, not the device failing to reply.
            CommandFailed?.Invoke(description, ex);
            throw;
        }
    }

    private async Task<usb_response_data_t> SendOnceAsync(
        task_offset_t target,
        ushort opcode,
        byte[]? body,
        string? description,
        usb_msg_type_t type,
        bool throwOnError,
        TimeSpan? timeout,
        CancellationToken cancellationToken
    )
    {
        ushort msgId = NextMsgId();
        var tcs = new TaskCompletionSource<usb_response_data_t>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _pending[msgId] = new PendingCommand(tcs, description);

        byte[] frame = UsbFrame.BuildCommandFrame(target, type, opcode, msgId, body ?? []);
        try
        {
            _connection.Write(frame);
        }
        catch
        {
            _pending.TryRemove(msgId, out _); // don't leak the pending slot if the write failed
            throw;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout ?? CommandTimeout);
        using (
            timeoutCts.Token.Register(() =>
            {
                if (_pending.TryRemove(msgId, out var pending))
                {
                    // Distinguish the two reasons the linked token fired: a caller-requested cancel
                    // is a definitive stop (surface OperationCanceledException), whereas elapsing our
                    // own CancelAfter is a timeout (which callers may choose to retry).
                    pending.Completion.TrySetException(
                        cancellationToken.IsCancellationRequested
                            ? new OperationCanceledException(cancellationToken)
                            : new TimeoutException(
                                $"no RESPONSE for opcode {opcode} (msg_id {msgId}) within the timeout"
                            )
                    );
                }
            })
        )
        {
            var response = await tcs.Task.ConfigureAwait(false);
            if (throwOnError && response.status != (uint)usb_response_status_t.USB_RSP_OK)
            {
                throw new DeviceCommandException(response);
            }
            return response;
        }
    }

    /// <summary>
    /// Writes one runtime sysconfig parameter into the device's store and awaits the firmware's
    /// RESPONSE. <paramref name="rawValue"/> is the parameter's 32 bits (IEEE-754 bits for float
    /// parameters — see <c>SysConfigParameterDef.ToRawBits</c>). Sent as a CONFIG frame to the
    /// USB controller, which owns the store; throws <see cref="TimeoutException"/> on no reply or
    /// <see cref="DeviceCommandException"/> if the firmware rejects the value (out of range /
    /// unknown id ⇒ <c>USB_RSP_MALFORMED</c>).
    /// <paramref name="announce"/> controls whether the write reports itself through
    /// <see cref="CommandSent"/> / <see cref="CommandFailed"/>. Clear it for a bulk restore: every
    /// parameter in the catalog goes out on each handshake, and narrating all of them would bury a
    /// user's actual edit under a page of routine writes. The caller that suppressed the per-write
    /// reports is then the one that owes a summary.
    /// </summary>
    public Task<usb_response_data_t> SetSysConfigParamAsync(
        sysconfig_param_t param,
        uint rawValue,
        bool announce = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    )
    {
        byte[] body = new byte[6];
        BitConverter.TryWriteBytes(body.AsSpan(0, 2), (ushort)param);
        BitConverter.TryWriteBytes(body.AsSpan(2, 4), rawValue);
        // The reply echoes only this opcode and a msg_id, so the parameter and the value it was set
        // to are named here, while they are still known, and carried to the ack (CommandResponse.
        // Request). rawValue is the wire form; the catalog turns it back into the number a person
        // typed, which is also what the firmware will act on.
        var def = SysConfigCatalog.Get(param);
        return SendCommandAsync(
            task_offset_t.TASK_OFFSET_USB_CONTROLLER,
            (ushort)usb_controller_command_t.USB_CMD_SET_SYSCONFIG,
            body,
            description: announce ? $"sysconfig {def.Describe(def.FromRawBits(rawValue))}" : null,
            type: usb_msg_type_t.USB_MSG_CONFIG,
            throwOnError: true,
            timeout: timeout,
            // Writing a parameter value is idempotent, so a lost ack is safely re-sent.
            retries: 2,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>Hands out the next host msg_id, skipping the firmware-reserved 0 on 16-bit wrap.</summary>
    private ushort NextMsgId()
    {
        ushort id;
        do
        {
            id = (ushort)Interlocked.Increment(ref _nextMsgId);
        } while (id == 0);
        return id;
    }

    private void OnParsed(DeviceMessage message)
    {
        PendingCommand? answered = null;
        usb_response_data_t answer = default;

        switch (message)
        {
            case CommandResponse response
                when _pending.TryRemove(response.Data.msg_id, out var pending):
                (answered, answer) = (pending, response.Data);
                // Republish the reply with the request it answers attached: this is the one place
                // both halves are in hand, and a subscriber that sees only the reply cannot
                // reconstruct what was asked.
                message = response with
                {
                    Matched = true,
                    Request = pending.Description,
                };
                break;
            case DeviceReady ready:
                BeginHandshake(ready);
                break;
            case SessionState session:
                SetSessionActive(session.InSession);
                break;
        }

        LogInbound(message);

        try
        {
            MessageReceived?.Invoke(message);
        }
        catch (Exception ex)
        {
            // A consumer (event subscriber, telemetry writer) threw. Contain it here — where the
            // parser can still advance past this record — so one bad message neither kills the
            // read loop nor wedges the parser buffer replaying it. MessageReceived runs arbitrary
            // consumer code, so this is defensive, not a swallow of our own faults.
            _log.LogError(
                ex,
                "a MessageReceived consumer threw; dropping this message and continuing"
            );
        }

        // Released only now that the reply has been published — and released even if a subscriber
        // threw above, since the sender is owed its answer either way. Completing first would let
        // the sender resume (and send its next command, and announce it) while this reply was still
        // on its way to the subscribers, so a batch of writes would log its acks a beat late,
        // reading as though each one belonged to the following parameter.
        answered?.Completion.TrySetResult(answer);
    }

    /// <summary>
    /// Records the device's session state, raising <see cref="SessionStateChanged"/> only when it
    /// actually moves. The firmware re-states the state on every heartbeat ack (so a host that lost
    /// and regained the link recovers it), which means most calls here are a repeat of what we
    /// already believe — de-duplicating is what keeps that refresh silent.
    ///
    /// The first statement on a link always counts, including an idle one. Until it arrives we have
    /// only assumed a board is idle; being told so is what makes it checked, and a consumer that
    /// wants to say which of the two it is showing has no other moment to hang that on.
    /// </summary>
    private void SetSessionActive(bool active)
    {
        if (_sessionActive == active)
        {
            return;
        }

        bool firstReport = _sessionActive is null;
        _sessionActive = active;
        _log.LogInformation(
            firstReport ? "device reports it is {State}" : "device session {State}",
            firstReport ? (active ? "mid-session" : "idle") : (active ? "started" : "stopped")
        );
        SessionStateChanged?.Invoke(active);
    }

    /// <summary>
    /// Goes back to knowing nothing about the session, for when the link drops. Deliberately not
    /// <c>SetSessionActive(false)</c>: the board may well still be running a session — we simply
    /// cannot see it — and recording that as a *known* idle state would mean the restatement that
    /// arrives with the next successful ack looked like a repeat and raised nothing. Consumers are
    /// still told to stop showing a session, because one we cannot observe is one whose readings
    /// have stopped being current.
    /// </summary>
    private void ForgetSessionState()
    {
        bool wasActive = _sessionActive == true;
        _sessionActive = null;
        if (wasActive)
        {
            _log.LogInformation("session state unknown; the link is no longer answering");
            SessionStateChanged?.Invoke(false);
        }
    }

    /// <summary>
    /// Mirrors every decoded inbound message into the structured log at the level its category
    /// warrants: firmware errors at Error, firmware warnings at Warning, undecodable frames at
    /// Warning, and command responses at Debug. High-rate telemetry samples are deliberately
    /// left out — those belong in the CSV data log (<see cref="TelemetryLogger"/>), not the
    /// event log.
    /// </summary>
    private void LogInbound(DeviceMessage message)
    {
        switch (message)
        {
            case DeviceFault { Error.IsWarning: true } f:
                _log.LogWarning(
                    "device warning: {Task} #{Number} (0x{Raw:X8}) @ {Timestamp}",
                    f.Error.Task,
                    f.Error.Number,
                    f.Error.Raw,
                    f.Timestamp
                );
                break;
            case DeviceFault f:
                _log.LogError(
                    "device error: {Task} #{Number} (0x{Raw:X8}) @ {Timestamp}",
                    f.Error.Task,
                    f.Error.Number,
                    f.Error.Raw,
                    f.Timestamp
                );
                break;
            case UnknownMessage u:
                _log.LogWarning(
                    "undecoded frame: {Type}/{Task} payload_len={Length}",
                    u.Header.msg_type,
                    u.Header.task_offset,
                    u.Header.payload_len
                );
                break;
            case CommandResponse r:
                _log.LogDebug(
                    "response from {Task}: {Request} (opcode={Opcode} msg_id={MsgId}) status={Status}",
                    r.Source,
                    r.Request ?? (r.Matched ? "unannounced command" : "unmatched request"),
                    r.Data.opcode,
                    r.Data.msg_id,
                    (usb_response_status_t)r.Data.status
                );
                break;
        }
    }

    /// <summary>
    /// Answers the firmware's device-ready announcement. The version is checked against the host's
    /// before acking, so an incompatible device is refused (once — the announcement repeats every
    /// 200ms) rather than acked; the firmware won't change version mid-link, so that is terminal.
    /// </summary>
    private void BeginHandshake(DeviceReady ready)
    {
        if (ready.Data.protocol_version == MessageConstants.USB_PROTOCOL_VERSION)
        {
            _ = TryAckAsync();
            return;
        }

        if (Interlocked.Exchange(ref _protocolRefused, 1) != 0)
        {
            return; // already told the caller; don't repeat it on every announcement
        }

        _log.LogError(
            "device protocol version {Device} != host {Host}; refusing to handshake",
            ready.Data.protocol_version,
            MessageConstants.USB_PROTOCOL_VERSION
        );
        ProtocolMismatch?.Invoke(ready.Data.protocol_version);
    }

    /// <summary>
    /// Drives the handshake when the device doesn't announce itself, and bounds the wait when
    /// nothing answers at all.
    /// <para>
    /// The firmware announces device-ready only while it has <i>not</i> been acked, and it never
    /// un-acks: nothing clears its ready flag when the host goes away (it can't even see that
    /// happen — its <c>CDC_SET_CONTROL_LINE_STATE</c> handler, where DTR would tell it, is empty).
    /// So on the second and every later connect to a board that is still powered, no announcement
    /// is coming and a purely announce-driven handshake waits forever. Ask instead: <c>USB_CMD_ACK</c>
    /// is answered whatever state the USB controller is in, and re-applying it is a no-op, so an
    /// unsolicited ack both detects a live device and re-arms its streaming.
    /// </para>
    /// Probing starts one interval in, by which point a freshly-booted board (announcing every
    /// 200ms) has already handshaked on its own, so in the common case no probe is ever sent. If
    /// even the probes go unanswered by <see cref="HandshakeTimeout"/> the link is reported dead
    /// once — but probing continues, so a board that is plugged in or reset later still connects.
    /// </summary>
    private async Task WatchHandshakeAsync(CancellationToken cancellationToken)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        bool reported = false;
        try
        {
            while (true)
            {
                await Task.Delay(HandshakeProbeInterval, cancellationToken).ConfigureAwait(false);

                if (IsHandshaked || Volatile.Read(ref _protocolRefused) != 0)
                {
                    return; // handshaked, or refused for a reason the caller has been told about
                }

                await TryAckAsync().ConfigureAwait(false);

                if (!reported && !IsHandshaked && elapsed.Elapsed >= HandshakeTimeout)
                {
                    reported = true;
                    _log.LogError(
                        "no device handshake within {Timeout}: nothing announced itself and the "
                            + "device did not answer an ack; still trying",
                        HandshakeTimeout
                    );
                    HandshakeTimedOut?.Invoke();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // link stopped
        }
    }

    /// <summary>
    /// Sends <c>USB_CMD_ACK</c> and completes the handshake on <c>USB_RSP_OK</c>. Latched, so the
    /// repeating announcement and the probe loop can both call it without acking twice over; a
    /// failed attempt releases the latch so the next announcement (or probe) retries.
    /// </summary>
    private async Task TryAckAsync()
    {
        if (Interlocked.Exchange(ref _handshakeStarted, 1) != 0)
        {
            return; // an ack is already in flight, or one already succeeded
        }

        try
        {
            var response = await SendCommandAsync(
                    task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                    (ushort)usb_controller_command_t.USB_CMD_ACK,
                    BitConverter.GetBytes(MessageConstants.USB_PROTOCOL_VERSION)
                )
                .ConfigureAwait(false);

            if (response.status == (uint)usb_response_status_t.USB_RSP_OK)
            {
                IsHandshaked = true;
                _log.LogInformation(
                    "device handshake complete (protocol v{Version})",
                    MessageConstants.USB_PROTOCOL_VERSION
                );
                Handshaked?.Invoke();
                StartHeartbeat();
                return;
            }

            if (
                response.status == (uint)usb_response_status_t.USB_RSP_VERSION_MISMATCH
                && Interlocked.Exchange(ref _protocolRefused, 1) == 0
            )
            {
                // Only a probe reaches this: the announce path checks the version before acking.
                // The device rejected ours without saying what it speaks, hence the null.
                _log.LogError(
                    "device rejected host protocol v{Host}; its firmware speaks a different schema",
                    MessageConstants.USB_PROTOCOL_VERSION
                );
                ProtocolMismatch?.Invoke(null);
                return;
            }

            _log.LogError(
                "device rejected handshake: {Status}",
                (usb_response_status_t)response.status
            );
            Interlocked.Exchange(ref _handshakeStarted, 0); // let the next announce or probe retry
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "handshake ack failed; will retry");
            Interlocked.Exchange(ref _handshakeStarted, 0);
        }
    }

    /// <summary>
    /// Starts the keep-alive poll. Called only from the one handshake that succeeds (the
    /// <c>_handshakeStarted</c> latch stays set for the life of the link), so there is never a
    /// second loop; the loop then runs until <see cref="Stop"/>.
    /// </summary>
    private void StartHeartbeat()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(token), token);
    }

    /// <summary>
    /// Re-sends <c>USB_CMD_ACK</c> every <see cref="HeartbeatInterval"/> to check the device is
    /// still on the other end of the port — an open serial port says nothing about the firmware
    /// still running, and a device that streams nothing (no session, all tasks idle) is otherwise
    /// indistinguishable from one that has died. The ack is the only host→device command the USB
    /// controller answers itself, and it is idempotent (it just re-affirms the ready flag), so it
    /// doubles as the ping. Losses are tolerated up to <see cref="HeartbeatMissesBeforeLost"/>;
    /// past that the link is reported lost, but polling continues so a device that returns
    /// (re-plugged, rebooted) is picked back up without a reconnect.
    /// </summary>
    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        int misses = 0;
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await PingAsync(cancellationToken).ConfigureAwait(false))
                {
                    misses = 0;
                    if (!IsHandshaked)
                    {
                        IsHandshaked = true;
                        _log.LogInformation("device answered the heartbeat again; link restored");
                        Handshaked?.Invoke();
                    }
                    continue;
                }

                // Stop() cancels and then closes the port, so a ping in flight at that moment fails
                // on the closed port. That's teardown, not a dead device: don't count it as a miss.
                cancellationToken.ThrowIfCancellationRequested();

                misses++;
                if (IsHandshaked && misses >= HeartbeatMissesBeforeLost)
                {
                    IsHandshaked = false;
                    // A device we cannot reach is not a device we can claim is running a session:
                    // drop the belief so a consumer stops showing that session's (now frozen) data.
                    // Recovering it needs no edge from the device — the ack that answers the next
                    // heartbeat re-states the session state, so a link that comes back restores it.
                    ForgetSessionState();
                    _log.LogError(
                        "device missed {Misses} consecutive heartbeats; link presumed lost",
                        misses
                    );
                    ConnectionLost?.Invoke();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // link stopped; nothing to surface
        }
    }

    /// <summary>One heartbeat round-trip. True only on a USB_RSP_OK ack — a timeout, a write
    /// failure (port yanked) or a non-OK status all count as a miss.</summary>
    private async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var roundTrip = System.Diagnostics.Stopwatch.StartNew();
            var response = await SendCommandAsync(
                    task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                    (ushort)usb_controller_command_t.USB_CMD_ACK,
                    BitConverter.GetBytes(MessageConstants.USB_PROTOCOL_VERSION),
                    timeout: HeartbeatTimeout,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            roundTrip.Stop();

            if (response.status == (uint)usb_response_status_t.USB_RSP_OK)
            {
                HeartbeatAcked?.Invoke(roundTrip.Elapsed);
                return true;
            }

            _log.LogWarning(
                "device rejected the heartbeat: {Status}",
                (usb_response_status_t)response.status
            );
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the link is stopping, not a missed beat
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "heartbeat ping failed");
            return false;
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        Stream stream = _connection.BaseStream;
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "serial read failed; stopping read loop");
                break;
            }

            if (read > 0)
            {
                // Before the parser, so the capture holds what the driver delivered rather than
                // what we made of it — that separation is the whole point of the recording.
                _rawCapture?.Record(buffer.AsSpan(0, read));
                try
                {
                    _parser.Append(buffer.AsSpan(0, read));
                }
                catch (Exception ex)
                {
                    // Consumer callbacks are already contained in OnParsed, so reaching here means
                    // the parser itself faulted (a decode bug). Surface it and stop, rather than
                    // hot-looping on the same unconsumed bytes or faulting the task silently.
                    _log.LogError(ex, "stream parser faulted; stopping read loop");
                    break;
                }
            }
        }
    }

    private void FailPending(Exception ex)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var pending))
            {
                pending.Completion.TrySetException(ex);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _connection.Dispose();
        _cts?.Dispose();
        // After Stop(), so every chunk the read loop recorded is queued before the writer is
        // closed: a capture truncated ahead of the fault it was opened to catch is worthless.
        _rawCapture?.Dispose();
    }
}
