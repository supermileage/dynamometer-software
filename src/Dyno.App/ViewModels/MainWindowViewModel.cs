using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dyno.Core;
using Dyno.Core.Derived;
using Dyno.Core.Diagnostics;
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
public partial class MainWindowViewModel : ObservableObject, IDeviceLinkGate
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<task_offset_t, TaskMonitorRow> _taskRows = new();
    private DeviceClient? _client;
    private TelemetryLogWorker? _telemetry;

    /// <summary>The stable name of the board the current link is on, captured when it connected.
    /// This is what lets a reconnect follow it to whatever node it comes back as; null when the
    /// platform offers no such name, and the watcher then waits for the old node instead.</summary>
    private PortAlias? _alias;

    /// <summary>Cancels a reconnect that is still waiting for a board. Anything the user does to
    /// the link by hand supersedes the watcher, so both connect and disconnect cancel it.</summary>
    private CancellationTokenSource? _relink;

    /// <summary>Whether this link has heard the device state its session yet, so the first report
    /// can be worded as the answer it is rather than as a change. Per link, hence cleared on
    /// teardown rather than once at startup.</summary>
    private bool _sessionStateReported;

    public ObservableCollection<string> Ports { get; } = new();
    public ObservableCollection<TaskMonitorRow> Tasks { get; } = new();
    public ObservableCollection<string> Events { get; } = new();

    // ---- Log panel tabs ------------------------------------------------------------------------
    // The bottom panel holds one tab per stream it can show. Two today — the Errors/Events log, and
    // the Console (build/flash output, which used to live on the Firmware page). Each is a
    // LogTabViewModel over an existing collection, so the panel's chrome is indifferent to what a
    // tab actually is, and a third stream is a fourth constructor call rather than new plumbing.

    public ObservableCollection<LogTabViewModel> LogTabs { get; } = new();

    [ObservableProperty]
    private LogTabViewModel _selectedLogTab = null!;

    // ---- Event log placement -------------------------------------------------------------------
    // The log is a window onto the link, not a feature of the Home page: a sysconfig write is
    // rejected, or the board drops off, while you are on SysConfig or Firmware — which is exactly
    // when you are changing something. So it lives in the window rather than in a page, and the
    // three states below are the ways it can sit there.

    /// <summary>Docked: the log takes its own strip at the foot of the window and the page shrinks
    /// to fit above it. Nothing is ever covered.</summary>
    public bool IsEventLogDocked => IsEventLogVisible && IsEventLogPinned;

    /// <summary>Floating: the log hovers over the foot of the page instead of shortening it. The
    /// dense pages (SysConfig, Firmware) are worth their full height, and there the log is something
    /// you glance at rather than read.</summary>
    public bool IsEventLogFloating => IsEventLogVisible && !IsEventLogPinned;

    /// <summary>Collapsed to a one-line bar. Not gone: it still says what the last event was, and
    /// counts what you missed — a hidden log that silently swallowed an error would be worse than
    /// no log.</summary>
    public bool IsEventLogCollapsed => !IsEventLogVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEventLogDocked))]
    [NotifyPropertyChangedFor(nameof(IsEventLogFloating))]
    [NotifyPropertyChangedFor(nameof(IsEventLogCollapsed))]
    private bool _isEventLogVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEventLogDocked))]
    [NotifyPropertyChangedFor(nameof(IsEventLogFloating))]
    [NotifyPropertyChangedFor(nameof(PinTooltip))]
    private bool _isEventLogPinned = true;

    /// <summary>Height of the log, in either state — dragged by the grip along its top edge.</summary>
    [ObservableProperty]
    private double _eventLogHeight = 200;

    public string PinTooltip =>
        IsEventLogPinned
            ? "Pinned to the bottom — the page ends above the log. Unpin to let the log float over it instead."
            : "Floating over the page. Pin it to give the log its own strip, so it never covers anything.";

    /// <summary>What arrived while the log was collapsed, so the bar can say there is something to
    /// come back for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissedEvents))]
    [NotifyPropertyChangedFor(nameof(MissedEventsText))]
    private int _missedEventCount;

    /// <summary>True when any of the missed events was an error or a warning — the difference
    /// between "22 pings happened" and "something went wrong while you weren't looking".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MissedEventsText))]
    private bool _missedAProblem;

    public bool HasMissedEvents => MissedEventCount > 0;

    public string MissedEventsText =>
        $"{MissedEventCount} new{(MissedAProblem ? " — including a problem" : string.Empty)}";

    /// <summary>The newest line, shown on the collapsed bar.</summary>
    [ObservableProperty]
    private string _latestEvent = "Nothing logged yet";

    [RelayCommand]
    private void ShowEventLog()
    {
        IsEventLogVisible = true;
        MissedEventCount = 0;
        MissedAProblem = false;
    }

    [RelayCommand]
    private void HideEventLog() => IsEventLogVisible = false;

    [RelayCommand]
    private void ToggleEventLogPin() => IsEventLogPinned = !IsEventLogPinned;

    /// <summary>The SysConfig page: runtime device parameters (SQLite-persisted, pushed over
    /// USB) plus the compile-time header editor. It reaches the device link through the getter,
    /// so it always talks to the current client; the sample-rate control on that page binds to
    /// this view model directly for the same reason.</summary>
    public SysConfigViewModel SysConfig { get; }

    /// <summary>The Firmware page: build the firmware (with whatever compile-time settings SysConfig
    /// has saved) and flash it. It reads those settings from <see cref="SysConfig"/> through
    /// delegates rather than holding the page itself — all it needs is the answer to "what would a
    /// build bake in, and is anything not saved yet".</summary>
    public FirmwareViewModel Firmware { get; }

    /// <summary>The Plots page: per-channel scrolling strip charts, fed from <see cref="Apply"/>
    /// under the same session gate as the numeric readouts.</summary>
    public PlotsViewModel Plots { get; } = new();

    /// <summary>The Settings page: application preferences (today, the plots' y-axis ranges).
    /// Constructed in the constructor because it edits <see cref="Plots"/>'s channels.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Which sidebar page is showing. A single value (not a flag per page) so exactly
    /// one page is ever active; the per-page bools below exist only for IsVisible bindings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomePage))]
    [NotifyPropertyChangedFor(nameof(IsPlotsPage))]
    [NotifyPropertyChangedFor(nameof(IsSysConfigPage))]
    [NotifyPropertyChangedFor(nameof(IsFirmwarePage))]
    [NotifyPropertyChangedFor(nameof(IsSettingsPage))]
    private AppPage _currentPage = AppPage.Home;

    public bool IsHomePage => CurrentPage == AppPage.Home;
    public bool IsPlotsPage => CurrentPage == AppPage.Plots;
    public bool IsSysConfigPage => CurrentPage == AppPage.SysConfig;
    public bool IsFirmwarePage => CurrentPage == AppPage.Firmware;
    public bool IsSettingsPage => CurrentPage == AppPage.Settings;

    [RelayCommand]
    private void Navigate(AppPage page)
    {
        CurrentPage = page;
        if (page == AppPage.Firmware)
        {
            // Cheap, and it means the page can never show a stale answer to "what will this build".
            Firmware.Refresh();
        }
        else if (page == AppPage.SysConfig)
        {
            // Same reasoning, and what the Reload button used to be for: the firmware's headers are
            // read at startup and can change under the app (a pull, a branch switch, an edit
            // elsewhere), so re-read them on the way in rather than making the user ask.
            SysConfig.RefreshFromDisk();
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string? _selectedPort;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
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
    [NotifyPropertyChangedFor(nameof(AngularVelocityGeared))]
    private double _angularVelocity;

    [ObservableProperty]
    private double _angularAcceleration;

    [ObservableProperty]
    private double _force;

    [ObservableProperty]
    private double _dutyCycle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TorqueGeared))]
    private double _torque;

    [ObservableProperty]
    private double _power;

    /// <summary>Gear ratio from the SysConfig page's PC Constants. Nothing on the wire carries it
    /// and no firmware reads it — it is this app's own number, applied to the geared readouts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AngularVelocityGeared))]
    private double _gearRatio = 1.0;

    /// <summary>Derives torque and power here rather than taking them from the device, so the
    /// constants behind them stay editable and a past run can be recomputed after a correction.</summary>
    private readonly DerivedQuantities _derived = new();

    /// <summary>Re-reads the PC Constants and applies them to the derivation, so an Apply on the
    /// SysConfig page changes the next sample rather than waiting for a reconnect.</summary>
    private void RefreshPcConstants()
    {
        GearRatio = SysConfig.PcConstant(SysConfigViewModel.GearRatioName);
        _derived.GearRatio = GearRatio;
        _derived.MomentOfInertiaKgM2 = SysConfig.PcConstant(SysConfigViewModel.MomentOfInertiaName);
        _derived.ForceLeverArmM = SysConfig.PcConstant(SysConfigViewModel.ForceLeverArmName);
    }

    /// <summary>What the mock-stream setting was last seen at, so a change can be told from an
    /// Apply that left it alone. Null until the first read, which is not a change.</summary>
    private bool? _mockDataInUse;

    /// <summary>
    /// Puts the fabricated-data warning on the Plots page in step with the mock-stream setting, and
    /// throws away everything derived from the old setting when it flips.
    /// Driven from the saved value rather than from anything the device reports, because that is
    /// what the host pushes after every handshake — so it is also what any connected board is
    /// running, and it is right before a board is even connected.
    /// </summary>
    /// <remarks>
    /// Real and fabricated samples are not the same measurement of anything, so nothing derived
    /// from one may survive into the other: the run on the Plots page, the held force/ω the
    /// derivation pairs across streams, the live readouts, and the telemetry CSV all start over.
    /// The CSV is rolled rather than cleared — it is a record, and the honest form of "these rows
    /// are not comparable" is two files rather than one file with a seam in it.
    /// </remarks>
    private void RefreshMockDataWarning()
    {
        bool mock = SysConfig.RuntimeValue(sysconfig_param_t.SYSCFG_USB_MOCK_MESSAGES) != 0;
        Plots.IsShowingMockData = mock;

        bool changed = _mockDataInUse is { } previous && previous != mock;
        _mockDataInUse = mock;
        if (!changed)
        {
            return;
        }

        Plots.OnDataSourceChanged();
        _derived.Reset();
        ClearTelemetry();

        // New worker before the old one is disposed: the read loop may be mid-Enqueue on the
        // reference it already has, and a disposed worker would count that row as dropped and
        // report the CSV as falling behind — which it is not.
        if (_telemetry is { } previousLog)
        {
            StartTelemetryLog();
            previousLog.Dispose();
        }

        AddEvent(
            mock
                ? "[WARN] mock data enabled — cleared the run, the readouts and the telemetry CSV. "
                    + "Nothing from here on is a measurement"
                : "[SESS] mock data disabled — cleared the fabricated run, the readouts and the "
                    + "telemetry CSV; what follows is measured"
        );
    }

    /// <summary>Torque with the gear ratio applied. Derived alongside the sensed torque rather than
    /// multiplied here, so the readout and the plotted channel are the same number.</summary>
    [ObservableProperty]
    private double _torqueGeared;

    public double AngularVelocityGeared =>
        DerivedQuantities.GearVelocity(AngularVelocity, GearRatio);

    [ObservableProperty]
    private uint _lastTimestamp;

    public MainWindowViewModel(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        Settings = new SettingsViewModel(Plots);
        // Losing a run is worth a line. It happens without the link dropping, so nothing else in
        // the log would account for the traces having emptied themselves.
        Plots.TimeBaseRestarted += () =>
            AddEvent(
                "[WARN] the device's timestamp counter restarted — the board most likely reset. "
                    + "The run on the Plots page could not be continued across that, so a new one "
                    + "started; the telemetry CSV is unaffected"
            );
        SysConfig = new SysConfigViewModel(() => _client);
        // The connect-time restore is the one device write the user never asked for, and the only
        // one they cannot see happen on the page they're not looking at.
        SysConfig.DeviceSyncLogged += line => Dispatcher.UIThread.Post(() => AddEvent(line));
        // The geared readouts and the derived torque/power use the SysConfig page's PC Constants.
        // Its constructor has already loaded the headers (so the event for that firing is gone);
        // read once now, then follow reloads and applied edits.
        SysConfig.PcConstantsChanged += () => Dispatcher.UIThread.Post(RefreshPcConstants);
        RefreshPcConstants();
        // Same shape, for the one runtime parameter that changes what the plotted numbers *are*
        // rather than how they are produced. Read once now (the value is restored from the
        // database in SysConfig's constructor), then follow every Apply.
        SysConfig.RuntimeSettingsChanged += () => Dispatcher.UIThread.Post(RefreshMockDataWarning);
        RefreshMockDataWarning();
        Firmware = new FirmwareViewModel(
            SysConfig.CompileTimeOverrides,
            () => SysConfig.PendingCount,
            this
        );

        var eventsTab = new LogTabViewModel(
            "Errors / Events",
            Events,
            colorize: true,
            newestFirst: true,
            "Nothing logged yet.",
            BuildEventReport,
            ClearEvents
        );
        var consoleTab = new LogTabViewModel(
            "Console",
            Firmware.Output,
            colorize: false,
            newestFirst: false,
            "Nothing has run yet. Build and flash output appears here, exactly as the tools print it.",
            lines => string.Join(Environment.NewLine, lines),
            Firmware.Output.Clear
        );
        LogTabs.Add(eventsTab);
        LogTabs.Add(consoleTab);
        SelectedLogTab = eventsTab;

        // The Firmware page no longer shows its own output, so a build the user just started would
        // otherwise vanish. Surface the Console the moment one runs — whichever page they are on,
        // and whether or not the panel was open.
        Firmware.OutputStarted += () =>
            Dispatcher.UIThread.Post(() =>
            {
                SelectedLogTab = consoleTab;
                IsEventLogVisible = true;
            });

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
        // A connect the user drove supersedes whatever the reconnect watcher was waiting for —
        // including the case where they got bored and picked the port themselves.
        CancelRelink();
        await ConnectCoreAsync();
    }

    /// <summary>Connect proper, without disturbing a reconnect in progress — which is what lets the
    /// watcher call it as its own last step rather than cancelling itself.</summary>
    private async Task ConnectCoreAsync()
    {
        try
        {
            var connection = new SerialConnection(SelectedPort!);
            // TEMP DIAGNOSTIC (16-byte head-loss investigation): set DYNO_RAW_CAPTURE to a path to
            // record the raw serial chunks, then replay them with `Dyno.App --replay <path>`.
            // Unset (the normal case) this is null and nothing is recorded.
            var rawCapture = RawCapture.FromEnvironment(out string? captureProblem);
            var client = new DeviceClient(
                connection,
                _loggerFactory.CreateLogger<DeviceClient>(),
                rawCapture
            );
            if (rawCapture is not null)
            {
                AddEvent(
                    "[DIAG] recording raw serial chunks — replay with "
                        + "`dotnet Dyno.App.dll --replay <path>`"
                );
            }
            else if (captureProblem is not null)
            {
                AddEvent(
                    $"[WARN] raw capture was requested but could not start — {captureProblem}"
                );
            }
            client.MessageReceived += OnMessage;
            client.Handshaked += OnHandshaked;
            client.ProtocolMismatch += OnProtocolMismatch;
            client.ConnectionLost += OnConnectionLost;
            client.HandshakeTimedOut += OnHandshakeTimedOut;
            client.HeartbeatAcked += OnHeartbeatAcked;
            client.SessionStateChanged += OnSessionStateChanged;
            client.CommandSent += OnCommandSent;
            client.CommandFailed += OnCommandFailed;
            client.StreamResynced += OnStreamResynced;
            client.BatchMisaccounted += OnBatchMisaccounted;
            _client = client;
            ConnectionStatus = $"Connecting to {SelectedPort}…";
            // Before Start, not after: Start launches the read loop, and the await below yields the
            // UI thread, so the first samples of the new link can be applied while we are still
            // inside it. Dropping the previous run afterwards would mean they had already been
            // appended to it, against a time origin from the board's previous boot.
            Plots.OnLinkStarted();
            // Opening the serial port starts blocking I/O that, on a Linux USB-CDC device, can
            // stall for a noticeable time — so it must not run on the UI thread or the whole app
            // appears to hang. Off-load it and only mark connected once the port is actually open.
            await Task.Run(client.Start);
            IsConnected = true;
            // Only now the port is known to have opened: CreateFile creates the file, so doing this
            // first would leave a stray empty CSV behind every failed attempt — and the reconnect
            // watcher retries often enough to bury logs/ in them.
            StartTelemetryLog();
            // Captured now, while the board is demonstrably on this node: it is the only thing that
            // will still identify it once the node is gone.
            _alias = PortAlias.For(SelectedPort!);
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
                await TearDownAsync(client);
            }
            _telemetry?.Dispose();
            _telemetry = null;
        }
    }

    /// <summary>Opens this link's telemetry CSV. Separate from the connect path only so it can run
    /// after the port has opened rather than before — see the call site.</summary>
    private void StartTelemetryLog()
    {
        _telemetry = new TelemetryLogWorker(
            TelemetryLogger.CreateFile(
                Path.Combine("logs", $"telemetry-{DateTime.Now:yyyyMMdd-HHmmss}.csv")
            )
        );
        _telemetry.RowsDropped += n =>
            Dispatcher.UIThread.Post(() =>
                AddEvent(
                    $"[WARN] telemetry CSV fell behind — {n} row{(n == 1 ? "" : "s")} not "
                        + "written (the stream, plots and readouts are unaffected)"
                )
            );
    }

    private bool CanDisconnect => IsConnected;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task Disconnect()
    {
        // Disconnecting by hand is an answer to "should this link exist", so it also calls off a
        // watcher that was busy trying to bring one back.
        CancelRelink();
        ConnectionStatus = "Disconnecting…";
        await ReleaseLinkAsync();
        ConnectionStatus = "Disconnected";
    }

    /// <summary>Tears the link down and returns the window to its disconnected state, without
    /// saying why — the caller sets the status, because "you pressed Disconnect", "the board is
    /// being flashed" and "the board vanished" want to read differently.</summary>
    private async Task ReleaseLinkAsync()
    {
        var client = _client;
        _client = null;
        if (client is not null)
        {
            Detach(client); // no OnMessage runs after this, so the read loop can't touch the VM/log
            if (!await TearDownAsync(client))
            {
                AddEvent(
                    "[WARN] the serial port would not close — the device was most likely unplugged "
                        + "or re-flashed while connected. Abandoned it; reconnecting is safe."
                );
            }
        }
        // Disposed after the client so the read loop can't write a row into a closed file.
        _telemetry?.Dispose();
        _telemetry = null;
        IsConnected = false;
        IsSessionActive = false;
        _sessionStateReported = false;
        ClearTelemetry();
    }

    // ---- Reconnecting to a board that left the bus ----------------------------------------------

    /// <summary>How long to keep looking for a board before giving up on it. Generous, because the
    /// wait it has to cover is a flash — programming plus verify plus reset — not just a re-plug.</summary>
    private static readonly TimeSpan RelinkWindow = TimeSpan.FromMinutes(2);

    /// <summary>How often to look for the board. Fast enough to feel immediate; the check is a
    /// couple of readlinks, so the cost of asking is nil.</summary>
    private static readonly TimeSpan RelinkPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Waited out after the node appears, before opening it. udev creates the node as soon
    /// as the interface enumerates, which is before the firmware is ready to talk — and on a box
    /// without this project's udev rule, ModemManager grabs the fresh port and probes it with AT
    /// commands for tens of seconds. A failed open is retried on the next tick regardless; this
    /// just stops the first attempt landing in the worst of it.</summary>
    private static readonly TimeSpan RelinkSettleDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Waits for the board to come back and links to it again, following it to whatever device node
    /// it re-enumerates as. Runs on the UI thread throughout (every await resumes on it), so it
    /// touches VM state directly; the returned task completes when the link is back or the window
    /// has expired, which is what makes it awaitable by the flash gate.
    /// </summary>
    private async Task RelinkAsync(string waiting, CancellationToken external = default)
    {
        var alias = _alias;
        var previous = SelectedPort;
        CancelRelink();
        // Linked, so the watcher answers both to the link's own controls and to whoever asked for
        // it — the Firmware page cancels the reconnect it started through its own Cancel button.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        _relink = cts;
        var token = cts.Token;

        try
        {
            await ReleaseLinkAsync();
            ConnectionStatus = waiting;
            AddEvent(
                alias is null
                    ? $"[INFO] waiting for a board on {previous}. This machine exposes no stable "
                        + "name for it, so a board that comes back on a different port will have to "
                        + "be reconnected by hand — install scripts/udev/99-dyno-cdc.rules to fix that."
                    : $"[INFO] waiting for the board to come back (following {alias.Path})"
            );

            var deadline = DateTime.UtcNow + RelinkWindow;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(RelinkPollInterval, token);

                // An alias names the board, so it is the whole answer when there is one — including
                // its silence, which means the board is still off the bus. Falling back to the old
                // node then would be strictly worse than waiting: /dev/ttyACM0 is just the lowest
                // free number, so whatever turns up under it next need not be this board at all.
                // Only with no alias is that guess the best available.
                var node = alias is not null ? alias.CurrentNode() : Reappeared(previous);
                if (node is null)
                {
                    continue;
                }

                await Task.Delay(RelinkSettleDelay, token);
                RefreshPorts();
                SelectedPort = node;
                await ConnectCoreAsync();
                if (IsConnected)
                {
                    AddEvent(
                        node == previous
                            ? $"[OK  ] board is back on {node}; link re-established"
                            : $"[OK  ] board came back as {node} (was {previous}); link re-established"
                    );
                    return;
                }
                // ConnectCoreAsync has already logged why. Most often the port exists but is not
                // ready yet, so the next tick is a real retry rather than a repeat of a lost cause —
                // which is also why its "Connect failed" status must not be left standing as if this
                // had stopped trying.
                ConnectionStatus = waiting;
            }

            ConnectionStatus = "Board did not come back";
            AddEvent(
                $"[ERR ] gave up after {RelinkWindow.TotalSeconds:F0} s waiting for the board — "
                    + "reconnect by hand once it is back"
            );
        }
        catch (OperationCanceledException)
        {
            // Whoever cancelled has usually already said what happens instead — a connect, a
            // disconnect — and owns the status. Testing for our own line rather than overwriting
            // unconditionally is what keeps this from clobbering theirs; if it is still standing,
            // nobody did, and it would sit there describing a wait that has stopped.
            if (ConnectionStatus == waiting)
            {
                ConnectionStatus = "Disconnected";
            }
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Reconnect failed";
            AddEvent($"[ERR ] reconnect gave up: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_relink, cts))
            {
                _relink = null;
            }
            cts.Dispose();
        }
    }

    /// <summary>The port name back if the OS is listing it again, else null.</summary>
    private static string? Reappeared(string? port) =>
        port is not null
        && SerialConnection.AvailablePorts().Contains(port, StringComparer.OrdinalIgnoreCase)
            ? port
            : null;

    /// <summary>Whether the node the link is bound to has left the OS's list — the difference
    /// between a board that is merely not answering and one that is no longer there.</summary>
    private bool PortHasVanished() => SelectedPort is not null && Reappeared(SelectedPort) is null;

    private void CancelRelink()
    {
        var cts = _relink;
        _relink = null;
        cts?.Cancel();
    }

    // ---- IDeviceLinkGate: handing the board to a programming tool and taking it back ------------

    /// <inheritdoc/>
    public async Task<bool> SuspendAsync()
    {
        if (!IsConnected)
        {
            return false;
        }
        CancelRelink();
        ConnectionStatus = "Releasing the link…";
        await ReleaseLinkAsync();
        ConnectionStatus = "Link released for flashing";
        AddEvent("[INFO] link released so the board can be programmed");
        return true;
    }

    /// <inheritdoc/>
    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        RelinkAsync("Waiting for the board to restart…", cancellationToken);

    /// <summary>How long a teardown gets to close the port before it is abandoned.</summary>
    private static readonly TimeSpan TeardownBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Closes a client's port and read loop off the UI thread, without letting a close that never
    /// returns hang the caller. A port whose device has gone — unplugged, or re-enumerated by a
    /// flash — can block in the kernel's tty close indefinitely, so the wait is bounded and an
    /// overrunning close is abandoned. Dispose cancels the read and heartbeat loops before it
    /// touches the port, so what leaks is one thread blocked on a device that no longer exists;
    /// that is far cheaper than the alternative, which is a window stuck on "Disconnecting…" with
    /// Connect disabled and no way back short of restarting the app. Returns false if abandoned.
    /// </summary>
    /// <remarks>
    /// Nothing here touches the UI, and the continuation deliberately does not go back to the
    /// dispatcher: <see cref="Shutdown"/> blocks on this from the UI thread, and a captured context
    /// would post the continuation to the very dispatcher that is waiting for it — closing the
    /// window would deadlock instead of exiting.
    /// </remarks>
    private static async Task<bool> TearDownAsync(DeviceClient client)
    {
        var teardown = Task.Run(client.Dispose);
        if (
            await Task.WhenAny(teardown, Task.Delay(TeardownBudget)).ConfigureAwait(false)
            != teardown
        )
        {
            return false;
        }
        // Observe the outcome so a throwing Dispose surfaces here rather than as an unobserved
        // task exception — and, either way, cannot leave the caller's state half torn down.
        return teardown.IsCompletedSuccessfully;
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
        client.CommandSent -= OnCommandSent;
        client.CommandFailed -= OnCommandFailed;
        client.StreamResynced -= OnStreamResynced;
        client.BatchMisaccounted -= OnBatchMisaccounted;
    }

    private void OnHandshaked()
    {
        // Re-apply the saved sysconfig to the board on every handshake (first connect and link
        // recoveries alike): it keeps settings only in RAM, so until this lands we have no idea what
        // it is running — defaults if it rebooted, a previous session's values if it never did.
        // Fire-and-forget off the UI thread; SysConfig reports the outcome itself.
        if (_client is { } client)
        {
            _ = SysConfig.ResyncDeviceAsync(client);
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
            // The device states the session state after every ack, so the first one to arrive on a
            // link is an answer to "what is this board doing", not a report that anything changed —
            // and it is the only thing that turns an assumed-idle board into a checked one. Saying
            // "session stopped" for it would describe a stop that never happened.
            bool firstReport = !_sessionStateReported;
            _sessionStateReported = true;
            IsSessionActive = active;
            if (active)
            {
                // Plots keep the *finished* run on screen (unlike the readouts, a frozen trace
                // still reads as history, not as a live value) — so the moment to drop it is when
                // the next run starts, not when this one ends.
                Plots.OnSessionStarted();
            }
            else
            {
                ClearTelemetry();
            }
            AddEvent(
                (firstReport, active) switch
                {
                    (true, true) =>
                        "[SESS] board is already running a session — streaming live data",
                    (true, false) => "[SESS] board is idle — no session running",
                    (false, true) => "[SESS] session started",
                    (false, false) => "[SESS] session stopped",
                }
            );
        });

    /// <summary>Resets the live readouts to their empty state, so nothing from a finished session
    /// (or a dropped link, or a switch to or from mock data) is left on screen looking current.
    /// </summary>
    private void ClearTelemetry()
    {
        AngularVelocity = 0;
        AngularAcceleration = 0;
        Force = 0;
        DutyCycle = 0;
        // The derived three as well as the measured ones. They were missed here, so a disconnect
        // blanked the sensor readouts while leaving torque and power lit at their last values —
        // the half of the panel most likely to be read as still current.
        Torque = 0;
        TorqueGeared = 0;
        Power = 0;
        LastTimestamp = 0;
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
            // A board that is merely unresponsive still owns its device node, and the client keeps
            // pinging it, so a link that recovers on its own needs nothing from us. A node that has
            // gone means the board re-enumerated: the handle we hold refers to nothing, no amount of
            // pinging can revive it, and the board is very likely already back under another name.
            if (PortHasVanished())
            {
                AddEvent(
                    $"[ERR ] {SelectedPort} is gone from the bus — the board reset, was re-flashed "
                        + "or was unplugged"
                );
                _ = RelinkAsync("Board disappeared — waiting for it to come back…");
                return;
            }
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
        // Before anything else, or a watcher mid-tick opens a fresh port on the way out.
        CancelRelink();
        var client = _client;
        _client = null;
        if (client is not null)
        {
            Detach(client);
            // Blocking the UI thread is fine here and only here: the window is going away, and
            // TearDownAsync is bounded and context-free (see its remarks), so this returns within
            // the teardown budget whether or not the port ever closes.
            TearDownAsync(client).Wait();
        }
        _telemetry?.Dispose();
        _telemetry = null;
    }

    private void OnMessage(DeviceMessage message)
    {
        // Runs on the DeviceClient read-loop thread — which must never wait on anything, or the
        // OS serial buffer overflows and the stream loses bytes. Both handoffs here are
        // constant-time: the CSV row goes to the telemetry worker's queue (its own thread owns
        // the file), and the UI mutation is posted to the dispatcher.
        _telemetry?.Enqueue(message);
        Dispatcher.UIThread.Post(() => Apply(message));
    }

    /// <summary>Publishes a derived reading to the readouts and the plots. Null while the
    /// derivation is still waiting for its first force or encoder sample — better to show nothing
    /// than a torque computed against a load cell that has not reported yet.</summary>
    private void ApplyDerived(DerivedSample? derived)
    {
        if (derived is not { } d)
        {
            return;
        }
        Torque = d.Torque;
        TorqueGeared = d.TorqueGeared;
        Power = d.Power;
        Plots.RecordDerived(d.Timestamp, d.Torque, d.TorqueGeared, d.Power);
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
                Plots.RecordOpticalEncoder(
                    s.Data.timestamp,
                    s.Data.angular_velocity,
                    s.Data.angular_acceleration,
                    GearRatio
                );
                // Updates the held ω/α only; torque and power are clocked off force so they stay
                // on one device clock (see DerivedQuantities).
                _derived.OnEncoder(s.Data.angular_velocity, s.Data.angular_acceleration);
                break;
            case ForceSensorSample s:
                Force = s.Data.force;
                Plots.RecordForce(s.Data.timestamp, s.Data.force);
                ApplyDerived(_derived.OnForce(s.Data.timestamp, s.Data.force));
                break;
            case BpmSample s:
                DutyCycle = s.Data.duty_cycle;
                Plots.RecordDutyCycle(s.Data.timestamp, s.Data.duty_cycle);
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
                AddEvent(Describe(f));
                break;
            case CommandResponse r when IsLinkAck(r):
                // The handshake ack and the 5s keep-alive that follows it share this opcode. Their
                // outcome is already reported through Handshaked / ConnectionLost, so logging each
                // one would just push real events out of the (capped) list every few seconds.
                break;
            case CommandResponse { Matched: true, Request: null }:
                // A command whose sender asked not to announce it: the sysconfig restore, which
                // writes the whole catalog on every handshake and reports itself as one summary
                // line. The ack is real and its command did apply — it just isn't news on its own,
                // and a page of them would bury whatever else the log was holding.
                break;
            case CommandResponse r:
                AddEvent(Describe(r));
                break;
            case UnknownMessage u:
                // A frame whose header passed the parser's plausibility check but whose payload
                // then fit no known record. Two things do that: a firmware sending something this
                // host has no decoder for, or — far more often — bytes lost in the device→host
                // stream, which is unframed (no start marker, no CRC), so a window onto the middle
                // of one record can pass for the start of another.
                AddEvent(
                    $"[?   ] undecoded {u.Header.msg_type} from {Friendly(u.Header.task_offset)} "
                        + $"with a {u.Header.payload_len}-byte payload — no decoder for it, or bytes "
                        + "were dropped and the parser is resyncing"
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

    /// <summary>The Errors/Events tab's Clear action (see <see cref="LogTabViewModel"/>). Also
    /// resets the collapsed bar, since there is no longer a latest event or a missed one.</summary>
    private void ClearEvents()
    {
        Events.Clear();
        LatestEvent = "Nothing logged yet";
        MissedEventCount = 0;
        MissedAProblem = false;
    }

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

    /// <summary>Appends a line to the event log. Public so the views can report the outcome of
    /// something the user started there — the CSV export says where the file went.</summary>
    public void AddEvent(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {text}";
        Events.Insert(0, line);
        LatestEvent = line;

        if (IsEventLogCollapsed)
        {
            MissedEventCount++;
            // Errors and warnings are the reason to reopen the log; pings are not.
            MissedAProblem |=
                text.StartsWith("[ERR", StringComparison.Ordinal)
                || text.StartsWith("[WARN", StringComparison.Ordinal);
        }

        // A [PING] every 5s is 12 lines a minute of routine traffic, so the cap is what decides how
        // far back a real error stays reachable: 200 would bury one within ~15 minutes of idling.
        const int cap = 1000;
        while (Events.Count > cap)
        {
            Events.RemoveAt(Events.Count - 1);
        }
    }

    /// <summary>Bytes were thrown away to realign the stream — which means bytes were lost getting
    /// here. Reported as a warning rather than as the undecodable frames it used to produce: those
    /// frames were never sent by anything, and reading them as messages (from "task 252", say) told
    /// the user about a device that does not exist instead of about a link that is dropping data.
    /// The line carries the evidence: which records the loss sat between and the skipped bytes
    /// themselves, so a cut-off record can be identified instead of guessed at.</summary>
    private void OnStreamResynced(ResyncDetails d) =>
        Dispatcher.UIThread.Post(() =>
        {
            string after = d.LastGoodHeader is { } g
                ? $"after {g.msg_type}/{Friendly(g.task_offset)} ({g.payload_len} B)"
                : "at stream start";
            string hex = Convert.ToHexString(d.SkippedBytes);
            if (d.BytesDropped > d.SkippedBytes.Length)
            {
                hex += $"… (+{d.BytesDropped - d.SkippedBytes.Length} more)";
            }
            AddEvent(
                $"[WARN] dropped {d.BytesDropped} byte{(d.BytesDropped == 1 ? "" : "s")} to resync "
                    + $"the device stream — {after}, resumed at {d.NextHeader.msg_type}/"
                    + $"{Friendly(d.NextHeader.task_offset)} ({d.NextHeader.payload_len} B); "
                    + $"skipped: {hex}"
            );
        });

    /// <summary>A CDC transfer that did not add up against the trailer closing it. This is the line
    /// that says which side of the link lost the bytes a [WARN] resync reports: a shortfall means
    /// the device framed and submitted bytes that never landed, a sequence gap means a whole
    /// transfer vanished, and a resync with neither means the device framed the record wrong in the
    /// first place. Deliberately worded as the conclusion rather than the raw counts — the numbers
    /// are here to be read by whoever is chasing the desync, not decoded.</summary>
    private void OnBatchMisaccounted(BatchAccounting b) =>
        Dispatcher.UIThread.Post(() =>
            AddEvent(
                b.MissingTransfers > 0
                    ? $"[WARN] {b.MissingTransfers} USB transfer{(b.MissingTransfers == 1 ? "" : "s")} "
                        + $"never arrived before batch #{b.Sequence} — the device sent them, the host "
                        + "never saw them"
                    : $"[WARN] USB batch #{b.Sequence} arrived {b.Shortfall} byte"
                        + $"{(b.Shortfall == 1 ? "" : "s")} short ({b.ObservedBytes} of "
                        + $"{b.DeclaredBytes} B) — the bytes left the firmware and were lost below it"
            )
        );

    /// <summary>A command going out. Logged from the client rather than from each call site so the
    /// sysconfig writes pushed on a handshake — which no button press announces — show up too.
    /// Fires on whichever thread sent the command (the UI thread for a button, a background one for
    /// the handshake re-push), hence the marshalling.</summary>
    private void OnCommandSent(string request) =>
        Dispatcher.UIThread.Post(() => AddEvent($"[CMD ] {request} — sent, awaiting the device"));

    /// <summary>A command that never got an answer. Worth a line of its own: the device applying a
    /// value and the device never hearing about it are the same silence in a log that only prints
    /// replies, and they are not the same thing to a user watching the dyno.</summary>
    private void OnCommandFailed(string request, Exception ex) =>
        Dispatcher.UIThread.Post(() =>
            AddEvent(
                ex is TimeoutException
                    ? $"[ERR ] {request} — no reply from the device; it may or may not have applied"
                    : $"[ERR ] {request} — could not be sent: {ex.Message}"
            )
        );

    /// <summary>
    /// One line for a command's ack, answering the only question the log is asked of it: did the
    /// device do the thing, or not. The reply names neither — it carries an opcode and a msg_id,
    /// which say nothing to a reader — so the wording leans on the request the client matched it to
    /// (<see cref="CommandResponse.Request"/>), and a rejection is filed as an error rather than
    /// left to be spotted in a status code. The raw form is the fallback for an ack that matched no
    /// command at all (a duplicate, or one that outlived its command's timeout); those numbers are
    /// all there is to say about it, and it is the one case where they're worth printing.
    /// </summary>
    private static string Describe(CommandResponse r)
    {
        var status = (usb_response_status_t)r.Data.status;
        if (r.Request is not { } request)
        {
            // msg_id is just the counter the host stamps on each command so the reply can be paired
            // back to it — there is nothing in it to decode, and saying whose number it was is the
            // most it can tell anyone. The opcode does have a meaning, and it is per-task.
            return $"[RSP ] {Friendly(r.Source)} {CommandOpcodes.Name(r.Source, r.Data.opcode)} — "
                + $"{status}, but this answers host request #{r.Data.msg_id}, which nothing was "
                + "waiting for (a duplicate ack, or one that outlived its command)";
        }

        return status == usb_response_status_t.USB_RSP_OK
            ? $"[RSP ] {request} — applied by the device"
            : $"[ERR ] {request} — REJECTED by the device ({status}); it still holds its previous value";
    }

    /// <summary>True for a reply to the USB controller's own <c>USB_CMD_ACK</c> — the handshake or
    /// one of the heartbeats that keeps checking the device is still there.</summary>
    private static bool IsLinkAck(CommandResponse r) =>
        r.Source == task_offset_t.TASK_OFFSET_USB_CONTROLLER
        && r.Data.opcode == (ushort)usb_controller_command_t.USB_CMD_ACK;

    private static string Friendly(task_offset_t offset) =>
        offset.ToString().Replace("TASK_OFFSET_", string.Empty);

    /// <summary>
    /// One line for a fault the board reported: which task, which fault, when, and what it means.
    /// Both the identifier and the sentence come from <see cref="ErrorCatalog"/> — that is, from
    /// the firmware's schema — so a fault added there arrives here explained, rather than as a
    /// number somebody has to go and look up. A code this build has never heard of (a board on
    /// newer firmware) still gets its number and its task, which is everything that is actually
    /// known about it.
    /// </summary>
    private static string Describe(DeviceFault f)
    {
        var severity = f.Error.IsWarning ? "WARN" : "ERR ";
        var task = Friendly(f.Error.Task);
        if (ErrorCatalog.Find(f.Error.Raw) is not { } fault)
        {
            return $"[{severity}] {task} #{f.Error.Number} @ {f.Timestamp} — no description in "
                + "this build; the board may be running newer firmware than this app";
        }

        // Most fault names begin with their own task's name, which the line has already said.
        // Printing both reads as a stutter ("FORCE_SENSOR_ADS1115 FORCE_SENSOR_ADS1115_INIT_
        // FAILURE"), so the repeat comes off — leaving the name still whole for anyone grepping
        // the firmware, since what is left is the tail of it.
        var name = fault.Name.StartsWith($"{task}_", StringComparison.Ordinal)
            ? fault.Name[(task.Length + 1)..]
            : fault.Name;
        return $"[{severity}] {task} {name} @ {f.Timestamp} — {fault.Description}";
    }
}
