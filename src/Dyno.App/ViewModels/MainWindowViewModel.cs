using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dyno.Core;
using Dyno.Core.Messages;
using Dyno.Core.Protocol;
using Dyno.Core.Serial;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dyno.App.ViewModels;

/// <summary>
/// Drives the main window: serial-port selection, the device link lifecycle, and the live
/// telemetry / task-monitor / event views. Owns a <see cref="DeviceClient"/> from Dyno.Core
/// and applies its messages on the UI thread.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<task_offset_t, TaskMonitorRow> _taskRows = new();
    private DeviceClient? _client;
    private TelemetryLogger? _telemetry;

    public ObservableCollection<string> Ports { get; } = new();
    public ObservableCollection<TaskMonitorRow> Tasks { get; } = new();
    public ObservableCollection<string> Events { get; } = new();

    /// <summary>The SysConfig page: runtime device parameters (SQLite-persisted, pushed over
    /// USB) plus the compile-time header editor. It reaches the device link through the getter,
    /// so it always talks to the current client; the sample-rate control on that page binds to
    /// this view model directly for the same reason.</summary>
    public SysConfigViewModel SysConfig { get; }

    /// <summary>Which sidebar page is showing. A single value (not a flag per page) so exactly
    /// one page is ever active; the per-page bools below exist only for IsVisible bindings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomePage))]
    [NotifyPropertyChangedFor(nameof(IsSysConfigPage))]
    private AppPage _currentPage = AppPage.Home;

    public bool IsHomePage => CurrentPage == AppPage.Home;
    public bool IsSysConfigPage => CurrentPage == AppPage.SysConfig;

    [RelayCommand]
    private void Navigate(AppPage page) => CurrentPage = page;

    /// <summary>Selectable force-sensor sample rates, in ascending SPS.</summary>
    public IReadOnlyList<SampleRateChoice> SampleRates { get; } =
        Enum.GetValues<ForceSensorSampleRate>()
            .Select(r => new SampleRateChoice(r, r.ToLabel()))
            .ToList();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string? _selectedPort;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetSampleRateCommand))]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionStatus = "Disconnected";

    /// <summary>Whether the dyno is running a session, per the device's own announcement. The
    /// telemetry readouts are bound to this: outside a session the device streams no sensor data,
    /// so there is nothing truthful to show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionStatus))]
    private bool _isSessionActive;

    public string SessionStatus => IsSessionActive ? "Session running" : "No session";

    [ObservableProperty]
    private double _angularVelocity;

    [ObservableProperty]
    private double _angularAcceleration;

    [ObservableProperty]
    private double _force;

    [ObservableProperty]
    private string _forceSource = "—";

    [ObservableProperty]
    private double _dutyCycle;

    [ObservableProperty]
    private uint _lastTimestamp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetSampleRateCommand))]
    private SampleRateChoice? _selectedSampleRate;

    public MainWindowViewModel(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        SysConfig = new SysConfigViewModel(() => _client);
        SelectedSampleRate =
            SampleRates.FirstOrDefault(c => c.Value == ForceSensorSampleRate.Sps128)
            ?? SampleRates[0];
        RefreshPorts();
    }

    /// <summary>Parameterless constructor for the XAML design-time previewer.</summary>
    public MainWindowViewModel()
        : this(NullLoggerFactory.Instance) { }

    [RelayCommand]
    private void RefreshPorts()
    {
        Ports.Clear();
        foreach (
            var port in SerialConnection
                .AvailablePorts()
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        )
        {
            Ports.Add(port);
        }
        SelectedPort ??= Ports.FirstOrDefault();
    }

    private bool CanConnect => !IsConnected && !string.IsNullOrWhiteSpace(SelectedPort);

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        try
        {
            _telemetry = TelemetryLogger.CreateFile(
                Path.Combine("logs", $"telemetry-{DateTime.Now:yyyyMMdd-HHmmss}.csv")
            );
            var connection = new SerialConnection(SelectedPort!);
            var client = new DeviceClient(connection, _loggerFactory.CreateLogger<DeviceClient>());
            // TEMP DIAGNOSTIC (slow-ack investigation): 4.7s is deliberately coprime to the
            // device's 1s task-monitor cycle. The 5.000s default phase-locks to that cycle, which
            // is why idle ping round-trips came out constant (~976ms one boot, ~2ms another).
            // If delivery is quantized to the device's next transmit, these RTTs will now sweep a
            // sawtooth instead of holding constant — that shape is the diagnosis. Revert to the
            // default 5s once measured.
            client.HeartbeatInterval = TimeSpan.FromSeconds(4.7);
            client.MessageReceived += OnMessage;
            client.Handshaked += OnHandshaked;
            client.ProtocolMismatch += OnProtocolMismatch;
            client.ConnectionLost += OnConnectionLost;
            client.HandshakeTimedOut += OnHandshakeTimedOut;
            client.HeartbeatAcked += OnHeartbeatAcked;
            client.SessionStateChanged += OnSessionStateChanged;
            _client = client;
            ConnectionStatus = $"Connecting to {SelectedPort}…";
            // Opening the serial port starts blocking I/O that, on a Linux USB-CDC device, can
            // stall for a noticeable time — so it must not run on the UI thread or the whole app
            // appears to hang. Off-load it and only mark connected once the port is actually open.
            await Task.Run(client.Start);
            IsConnected = true;
            ConnectionStatus = $"Connected to {SelectedPort} — handshaking…";
            AddEvent(ConnectionStatus);
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Connect failed: {ex.Message}";
            AddEvent(ConnectionStatus);
            var client = _client;
            _client = null;
            if (client is not null)
            {
                Detach(client);
                await Task.Run(client.Dispose);
            }
            _telemetry?.Dispose();
            _telemetry = null;
        }
    }

    private bool CanDisconnect => IsConnected;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task Disconnect()
    {
        var client = _client;
        _client = null;
        ConnectionStatus = "Disconnecting…";
        if (client is not null)
        {
            Detach(client); // no OnMessage runs after this, so the read loop can't touch the VM/log
            // Dispose closes the serial port and joins the read loop. On Linux USB-CDC that close
            // is a known blocker, so run it off the UI thread to keep the app responsive.
            await Task.Run(client.Dispose);
        }
        // Disposed after the client so the read loop can't write a row into a closed file.
        _telemetry?.Dispose();
        _telemetry = null;
        IsConnected = false;
        IsSessionActive = false;
        ClearTelemetry();
        ConnectionStatus = "Disconnected";
    }

    /// <summary>Unsubscribes the VM from a client's events so no callback fires after teardown.</summary>
    private void Detach(DeviceClient client)
    {
        client.MessageReceived -= OnMessage;
        client.Handshaked -= OnHandshaked;
        client.ProtocolMismatch -= OnProtocolMismatch;
        client.ConnectionLost -= OnConnectionLost;
        client.HandshakeTimedOut -= OnHandshakeTimedOut;
        client.HeartbeatAcked -= OnHeartbeatAcked;
        client.SessionStateChanged -= OnSessionStateChanged;
    }

    private bool CanSetSampleRate => IsConnected && SelectedSampleRate is not null;

    [RelayCommand(CanExecute = nameof(CanSetSampleRate))]
    private async Task SetSampleRate()
    {
        if (_client is null || SelectedSampleRate is null)
        {
            return;
        }

        var choice = SelectedSampleRate;
        try
        {
            // Succeeds only on a USB_RSP_OK ack; a timeout or firmware rejection throws.
            await _client.SetForceSensorSampleRateAsync(choice.Value);
            AddEvent($"[CMD ] force sensor sample rate set to {choice.Label}");
        }
        catch (Exception ex)
        {
            AddEvent($"[ERR ] set sample rate failed: {ex.Message}");
        }
    }

    private void OnHandshaked()
    {
        // Re-deliver the saved sysconfig overrides on every handshake (first connect and link
        // recoveries alike): the board keeps them only in RAM, so a reboot behind a re-handshake
        // means it is running defaults until this push lands. Fire-and-forget off the UI thread;
        // SysConfig reports the outcome in its own status line.
        if (_client is { } client)
        {
            _ = SysConfig.PushSavedToDeviceAsync(client);
        }

        Dispatcher.UIThread.Post(() =>
        {
            ConnectionStatus = $"Connected to {SelectedPort}";
            AddEvent("[OK  ] handshake complete; streaming enabled");
        });
    }

    /// <summary>
    /// The dyno started or stopped a session. Sensor data only streams during one, so a stop also
    /// clears the readouts: leaving the final values on screen would show a still, plausible number
    /// for a dyno that is no longer measuring anything — indistinguishable from a live reading that
    /// happens not to be changing.
    /// </summary>
    private void OnSessionStateChanged(bool active) =>
        Dispatcher.UIThread.Post(() =>
        {
            IsSessionActive = active;
            if (!active)
            {
                ClearTelemetry();
            }
            AddEvent(active ? "[SESS] session started" : "[SESS] session stopped");
        });

    /// <summary>Resets the live readouts to their empty state, so nothing from a finished session
    /// (or a dropped link) is left on screen looking current.</summary>
    private void ClearTelemetry()
    {
        AngularVelocity = 0;
        AngularAcceleration = 0;
        Force = 0;
        DutyCycle = 0;
        LastTimestamp = 0;
        ForceSource = "—";
    }

    /// <summary>The port opened but nothing announced itself in time. Like a lost connection, this
    /// is reported rather than acted on: the link keeps listening, so a device that turns up late
    /// still handshakes and puts the status back to connected.</summary>
    private void OnHandshakeTimedOut() =>
        Dispatcher.UIThread.Post(() =>
        {
            ConnectionStatus = $"No response from {SelectedPort} — still listening…";
            AddEvent(
                "[ERR ] no device-ready announcement; check the board is running the dyno firmware "
                    + "and that this is the right port"
            );
        });

    /// <summary>The device stopped answering the keep-alive. The link object is deliberately left
    /// alive: it keeps polling, so a device that comes back re-handshakes on its own, and the user
    /// still has an enabled Disconnect if it doesn't.</summary>
    private void OnConnectionLost() =>
        Dispatcher.UIThread.Post(() =>
        {
            ConnectionStatus = $"{SelectedPort} not responding — retrying…";
            AddEvent("[ERR ] device stopped answering the heartbeat; connection lost");
        });

    private void OnProtocolMismatch(uint? deviceVersion) =>
        Dispatcher.UIThread.Post(() =>
        {
            ConnectionStatus = "Protocol mismatch — update host or firmware";
            AddEvent(
                deviceVersion is { } v
                    ? $"[ERR ] device protocol v{v} != host v{MessageConstants.USB_PROTOCOL_VERSION}; no data will stream"
                    : $"[ERR ] device rejected host protocol v{MessageConstants.USB_PROTOCOL_VERSION}; "
                        + "its firmware speaks a different schema, so no data will stream"
            );
        });

    /// <summary>Every answered keep-alive, so the link's liveness is visible in the log rather than
    /// inferred from the absence of errors.</summary>
    private void OnHeartbeatAcked(TimeSpan roundTrip) =>
        Dispatcher.UIThread.Post(() =>
            AddEvent($"[PING] device alive — acked in {roundTrip.TotalMilliseconds:F1} ms")
        );

    /// <summary>Called on app shutdown so the read loop and serial port are released. Runs the
    /// blocking close off the UI thread with a bounded wait so a stuck serial close can't freeze
    /// application exit.</summary>
    public void Shutdown()
    {
        var client = _client;
        _client = null;
        if (client is not null)
        {
            Detach(client);
            Task.Run(client.Dispose).Wait(TimeSpan.FromSeconds(2));
        }
        _telemetry?.Dispose();
        _telemetry = null;
    }

    private void OnMessage(DeviceMessage message)
    {
        // Runs on the DeviceClient read-loop thread (the single reader), so the non-thread-safe
        // TelemetryLogger is fed here rather than on the UI thread; UI mutation is marshalled.
        _telemetry?.Log(message);
        Dispatcher.UIThread.Post(() => Apply(message));
    }

    private void Apply(DeviceMessage message)
    {
        switch (message)
        {
            // Sensor samples are shown only while a session is running. The firmware already gates
            // its stream the same way, so this is belt-and-braces for the one window where the two
            // disagree: samples framed just before a session stopped can still be in flight behind
            // the stop event, and applying those would leave the readouts holding data the user has
            // just been told is over.
            case OpticalEncoderSample or ForceSensorSample or BpmSample when !IsSessionActive:
                break;
            case OpticalEncoderSample s:
                AngularVelocity = s.Data.angular_velocity;
                AngularAcceleration = s.Data.angular_acceleration;
                LastTimestamp = s.Data.timestamp;
                break;
            case ForceSensorSample s:
                Force = s.Data.force;
                ForceSource = Friendly(s.Source);
                break;
            case BpmSample s:
                DutyCycle = s.Data.duty_cycle;
                break;
            case SessionState:
                // Applied via DeviceClient.SessionStateChanged, which reports only real
                // transitions; the raw announcement repeats after every heartbeat ack and would
                // flood the log.
                break;
            case TaskMonitorSample s:
                UpsertTask(s.Data);
                break;
            case DeviceFault f:
                AddEvent(
                    $"[{(f.Error.IsWarning ? "WARN" : "ERR ")}] {Friendly(f.Error.Task)} #{f.Error.Number} @ {f.Timestamp}"
                );
                break;
            case CommandResponse r when IsLinkAck(r):
                // The handshake ack and the 5s keep-alive that follows it share this opcode. Their
                // outcome is already reported through Handshaked / ConnectionLost, so logging each
                // one would just push real events out of the (capped) list every few seconds.
                break;
            case CommandResponse r:
                AddEvent(
                    $"[RSP ] {Friendly(r.Source)} opcode={r.Data.opcode} id={r.Data.msg_id} status={(usb_response_status_t)r.Data.status}"
                );
                break;
            case UnknownMessage u:
                AddEvent(
                    $"[?   ] {u.Header.msg_type}/{Friendly(u.Header.task_offset)} len={u.Header.payload_len}"
                );
                break;
        }
    }

    private void UpsertTask(task_monitor_output_data data)
    {
        if (!_taskRows.TryGetValue(data.task_offset, out var row))
        {
            row = new TaskMonitorRow { Task = Friendly(data.task_offset) };
            _taskRows[data.task_offset] = row;
            Tasks.Add(row);
        }
        row.State = data.task_state;
        row.FreeBytes = data.free_bytes;
        row.Timestamp = data.timestamp;
    }

    [RelayCommand]
    private void ClearEvents() => Events.Clear();

    /// <summary>
    /// Renders event lines as text to paste elsewhere (a bug report, a chat). The list is shown
    /// newest-first so the latest event is under the user's eye, but a pasted log is read
    /// top-to-bottom, so the copy is flipped back into chronological order. The header carries the
    /// context a reader would otherwise have to ask for: which port, what the link state was, and
    /// which protocol version the host speaks.
    /// </summary>
    public string BuildEventReport(IEnumerable<string> lines) =>
        string.Join(
            Environment.NewLine,
            lines
                .Reverse()
                .Prepend(
                    $"# Dyno — {ConnectionStatus} — port {SelectedPort ?? "none"} — host protocol "
                        + $"v{MessageConstants.USB_PROTOCOL_VERSION} — {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
                )
        );

    private void AddEvent(string text)
    {
        Events.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {text}");
        // A [PING] every 5s is 12 lines a minute of routine traffic, so the cap is what decides how
        // far back a real error stays reachable: 200 would bury one within ~15 minutes of idling.
        const int cap = 1000;
        while (Events.Count > cap)
        {
            Events.RemoveAt(Events.Count - 1);
        }
    }

    /// <summary>True for a reply to the USB controller's own <c>USB_CMD_ACK</c> — the handshake or
    /// one of the heartbeats that keeps checking the device is still there.</summary>
    private static bool IsLinkAck(CommandResponse r) =>
        r.Source == task_offset_t.TASK_OFFSET_USB_CONTROLLER
        && r.Data.opcode == (ushort)usb_controller_command_t.USB_CMD_ACK;

    private static string Friendly(task_offset_t offset) =>
        offset.ToString().Replace("TASK_OFFSET_", string.Empty);
}
