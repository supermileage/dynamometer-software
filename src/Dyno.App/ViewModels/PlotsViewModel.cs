using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dyno.Core.Export;
using Dyno.Core.Plotting;

namespace Dyno.App.ViewModels;

/// <summary>
/// The Plots page: a user-built set of graphs showing telemetry channels over a whole run.
/// </summary>
/// <remarks>
/// Time comes from the device, not from the host: every sample is placed at its own hardware
/// timestamp (TIM2 at 1 MHz), measured from the first sample of the run, so the x axis starts at 0
/// and reads in elapsed seconds. Host-side scheduling jitter therefore cannot smear the trace, and
/// a stop/start shows the real time that passed in between rather than collapsing it.
///
/// The buffers deliberately survive a session ending: stopping freezes the plot where it is (the
/// right edge is the newest sample, which stops advancing), and a later session appends after the
/// gap at its own timestamps. <see cref="Clear"/> is the only thing that empties them.
///
/// Samples are recorded from <see cref="MainWindowViewModel.Apply"/> on the UI thread — the same
/// gate the numeric readouts use, so the plots record exactly what the readouts showed. Rendering
/// reads the buffers on the UI thread too, so nothing here locks.
///
/// Series colors are the dataviz reference palette's dark-mode categorical slots in fixed order,
/// validated (CVD separation, normal-vision floor, ≥3:1 contrast) against this app's #171A20
/// card surface. Identity never rides on color alone: every strip is titled.
/// </remarks>
public partial class PlotsViewModel : ObservableObject
{
    /// <summary>
    /// True while the device is set to stream its synthetic diagnostic data (the runtime parameter
    /// <c>USB_MOCK_MESSAGES</c>) instead of reading its sensors. The plots then show counters, not
    /// measurements, and there is nothing about the traces themselves that would give that away —
    /// which is the whole reason the page says so, in the one place the numbers are being read.
    /// </summary>
    [ObservableProperty]
    private bool _isShowingMockData;

    private readonly DispatcherTimer _frameTimer;

    /// <summary>Folds the device counter's 71.6-minute rollovers into continuous seconds.</summary>
    private readonly TimestampUnwrapper _clock = new();

    /// <summary>Unwrapped device ticks of the first sample of the run. Every recorded time is
    /// measured from here, which is what puts the left edge of every graph at 0 — and keeping it
    /// in ticks rather than seconds means a buffer time can be turned back into the exact device
    /// timestamp it came from. Null until the first sample arrives.</summary>
    private ulong? _originTicks;

    public PlotChannelViewModel AngularVelocity { get; } =
        new("Angular velocity", "rad/s", Color.Parse("#3987E5"));

    public PlotChannelViewModel AngularVelocityGeared { get; } =
        new("Angular velocity, geared", "rad/s", Color.Parse("#008300"));

    public PlotChannelViewModel AngularAcceleration { get; } =
        new("Angular acceleration", "rad/s²", Color.Parse("#D55181"));

    public PlotChannelViewModel Force { get; } = new("Force", "N", Color.Parse("#C98500"));

    public PlotChannelViewModel Torque { get; } = new("Torque", "N·m", Color.Parse("#199E70"));

    public PlotChannelViewModel TorqueGeared { get; } =
        new("Torque, geared", "N·m", Color.Parse("#D95926"));

    public PlotChannelViewModel Power { get; } = new("Power", "W", Color.Parse("#9085E9"));

    public PlotChannelViewModel DutyCycle { get; } =
        new("BPM duty cycle", "", Color.Parse("#E66767"));

    public IReadOnlyList<PlotChannelViewModel> Channels { get; }

    /// <summary>The graphs, top to bottom. Starts with one rather than empty — a blank plots page
    /// teaches nothing about what the page can do — and with one rather than several, so the first
    /// graph opens at full height and Add builds up from there.</summary>
    public ObservableCollection<PlotGraphViewModel> Graphs { get; } = new();

    public PlotsViewModel()
    {
        Channels =
        [
            AngularVelocity,
            AngularVelocityGeared,
            AngularAcceleration,
            Force,
            Torque,
            TorqueGeared,
            Power,
            DutyCycle,
        ];

        // Angular velocity: the one channel measured directly rather than derived, so it is the
        // one that shows something on a board with nothing calibrated yet.
        Graphs.Add(new PlotGraphViewModel(Channels, AngularVelocity, RemoveGraph));

        // ~30 fps redraw driver. The window's right edge is the newest sample anywhere, not the
        // wall clock: while data streams the plots scroll live, and when the session stops they
        // freeze holding the run instead of scrolling it off the screen.
        _frameTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(33),
            DispatcherPriority.Background,
            (_, _) => AdvanceAnchor()
        );
        _frameTimer.Start();
    }

    /// <summary>Adds a graph of the first channel no other graph is showing yet — the likeliest
    /// reason to press Add — falling back to the first channel once everything is on screen.</summary>
    [RelayCommand]
    private void AddGraph()
    {
        var channel = Channels.FirstOrDefault(c => !Graphs.Any(g => g.Shows(c))) ?? Channels[0];
        Graphs.Add(new PlotGraphViewModel(Channels, channel, RemoveGraph));
    }

    private void RemoveGraph(PlotGraphViewModel graph) => Graphs.Remove(graph);

    private void AdvanceAnchor()
    {
        double latest = LatestRecordedTime();
        foreach (var channel in Channels)
        {
            channel.AnchorTime = latest; // no-op notification when unchanged
            // The axis spans the whole run, so anything recorded is on screen.
            channel.HasData = channel.Buffer.Count > 0;
        }
    }

    /// <summary>A new session began. Deliberately does nothing: the previous run stays on screen
    /// and this one is appended after it, separated by however much time actually passed. Use
    /// <see cref="Clear"/> to start over.</summary>
    public void OnSessionStarted() { }

    /// <summary>
    /// A new device link began — which, unlike a new session, does discard everything.
    /// </summary>
    /// <remarks>
    /// Appending across a session works because both runs are stamped from one counter, so the gap
    /// between them is real elapsed time. A reconnect breaks that: the board reboots when the link
    /// drops (a reflash, a re-plug, a reset), TIM2 restarts from 0, and the new samples are
    /// measured from an origin that belongs to the previous boot. What they plot at is meaningless
    /// either way — and concretely they arrive *behind* everything already recorded, which
    /// <see cref="Elapsed"/> floors at 0, so they would replay on top of the old run instead of
    /// following it. That breaks the non-decreasing order the buffers, the CSV export and the
    /// window search all depend on. There is no origin that reconciles two boots, so the run on
    /// screen ends with the link that produced it.
    /// </remarks>
    public void OnLinkStarted() => Clear();

    /// <summary>Whether there is anything to export yet — nothing has been recorded until at least
    /// one channel holds a sample.</summary>
    public bool HasRecordedData => Channels.Any(c => c.Buffer.Count > 0);

    /// <summary>The channels as export columns, in the order they are listed on the page. Column
    /// names are snake_case rather than the display titles, so the header is usable as a
    /// identifier in whatever reads the file.</summary>
    public IReadOnlyList<ExportChannel> ExportChannels() =>
        [
            new("angular_velocity_rad_s", AngularVelocity.Buffer),
            new("angular_velocity_geared_rad_s", AngularVelocityGeared.Buffer),
            new("angular_acceleration_rad_s2", AngularAcceleration.Buffer),
            new("force_n", Force.Buffer),
            new("torque_nm", Torque.Buffer),
            new("torque_geared_nm", TorqueGeared.Buffer),
            new("power_w", Power.Buffer),
            new("bpm_duty_cycle", DutyCycle.Buffer),
        ];

    /// <summary>Writes everything recorded so far as a sparse CSV. Returns the row count.</summary>
    /// <remarks>
    /// The time column is the device's own timestamp, exactly as it arrived — the same value the
    /// raw telemetry log records as <c>device_ts</c>, so the two files can be matched row for row.
    /// Buffer times are elapsed seconds (what the plots draw), so each is converted back; the
    /// rounding is exact, since a tick offset is a whole number a double represents precisely at
    /// these magnitudes. It wraps with the device's 32-bit counter, every ~71.6 minutes.
    /// </remarks>
    public int WriteExport(TextWriter writer) =>
        TelemetryExporter.Write(writer, ExportChannels(), "device_ts", DeviceTimestampOf);

    /// <summary>The raw device timestamp a buffer time came from.</summary>
    private string DeviceTimestampOf(double elapsedSeconds)
    {
        ulong ticks =
            (_originTicks ?? 0)
            + (ulong)Math.Round(elapsedSeconds * TimestampUnwrapper.TicksPerSecond);
        return ((uint)(ticks & 0xFFFF_FFFF)).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Discards every recorded sample and restarts the time origin, so the next sample
    /// begins a fresh run at 0.</summary>
    [RelayCommand]
    private void Clear()
    {
        foreach (var channel in Channels)
        {
            channel.Buffer.Clear();
        }
        _clock.Reset();
        _originTicks = null;
        AdvanceAnchor();
    }

    /// <summary>
    /// How far behind the newest recorded sample a new one may be stamped before it is read as a
    /// different time base rather than as ordinary lag.
    /// </summary>
    /// <remarks>
    /// The two things being told apart are orders of magnitude apart, so the threshold only has to
    /// sit between them. Lag is bounded by how often the slowest sensor task produces a sample —
    /// the BPM channel's newest point is legitimately older than the encoder's, and that is normal,
    /// not a fault. A counter that restarted, meanwhile, throws time back by the whole run so far.
    /// A second is comfortably above the first and far below the second.
    /// </remarks>
    private const double TimeBaseRestartToleranceSeconds = 1.0;

    /// <summary>Raised when a sample's timestamp showed the device's counter had restarted and the
    /// run on screen was therefore dropped. Carries nothing: the page has already started over, and
    /// this exists so the app can say so in the event log rather than silently losing a trace.</summary>
    public event Action? TimeBaseRestarted;

    /// <summary>The newest time recorded on any channel; 0 while nothing has been recorded.</summary>
    private double LatestRecordedTime()
    {
        double latest = 0;
        foreach (var channel in Channels)
        {
            if (channel.Buffer.Count > 0)
            {
                latest = Math.Max(latest, channel.Buffer.LatestTime);
            }
        }
        return latest;
    }

    /// <summary>Seconds since the run began for a raw device timestamp, adopting the first sample
    /// seen as the origin.</summary>
    /// <remarks>
    /// A sample stamped before the newest one already recorded is either ordinary lag between the
    /// sensor tasks, or a device whose counter went back to the start — a board that reset, was
    /// re-flashed, or began stamping from something other than the timer the run was measured
    /// against. The first is fine and is plotted where it falls. The second is a new time base, and
    /// there is no origin that reconciles it with the run already on screen (the same reason
    /// <see cref="OnLinkStarted"/> discards one), so the run ends here and a fresh one begins at 0.
    ///
    /// Handling it is not cosmetic. Left alone, the old code floored the difference at 0 and handed
    /// that to the buffers, which require non-decreasing times — <c>CopyWindow</c> binary-searches
    /// on it, the envelope decimator's output bound depends on it, and the CSV export merges on it.
    /// A debug build asserts and takes the process down with it; a release build just quietly draws
    /// and exports a run that doubles back on itself.
    /// </remarks>
    private double Elapsed(uint deviceTimestamp)
    {
        double seconds = Measure(deviceTimestamp);
        if (seconds + TimeBaseRestartToleranceSeconds < LatestRecordedTime())
        {
            Clear();
            seconds = Measure(deviceTimestamp);
            TimeBaseRestarted?.Invoke();
        }
        return seconds;
    }

    /// <summary>Places one raw timestamp against the current origin, adopting it as the origin if
    /// the run has none yet.</summary>
    private double Measure(uint deviceTimestamp)
    {
        ulong ticks = _clock.ToTicks(deviceTimestamp);
        _originTicks ??= ticks;
        double seconds = ticks / TimestampUnwrapper.TicksPerSecond;
        double originSeconds = _originTicks.Value / TimestampUnwrapper.TicksPerSecond;
        // Tasks are read in turn, so a sample can arrive stamped a little before the one that
        // opened the run; clamp rather than plot a negative time.
        return Math.Max(0, seconds - originSeconds);
    }

    public void RecordOpticalEncoder(
        uint timestamp,
        float angularVelocity,
        float angularAcceleration,
        double gearRatio
    )
    {
        double now = Elapsed(timestamp);
        AngularVelocity.Buffer.Add(now, angularVelocity);
        AngularVelocityGeared.Buffer.Add(now, (float)(angularVelocity * gearRatio));
        AngularAcceleration.Buffer.Add(now, angularAcceleration);
    }

    public void RecordForce(uint timestamp, float force) =>
        Force.Buffer.Add(Elapsed(timestamp), force);

    /// <summary>Torque and power as derived on this PC (see <c>DerivedQuantities</c>), stamped with
    /// the force sample that produced them so they line up with the measurement they came from
    /// rather than with a separate device task's clock. Force-clocked throughout, which is what
    /// keeps these times non-decreasing — <c>TimeSeriesBuffer</c> requires it.</summary>
    public void RecordDerived(uint timestamp, float torque, float torqueGeared, float power)
    {
        double now = Elapsed(timestamp);
        Torque.Buffer.Add(now, torque);
        TorqueGeared.Buffer.Add(now, torqueGeared);
        Power.Buffer.Add(now, power);
    }

    public void RecordDutyCycle(uint timestamp, float dutyCycle) =>
        DutyCycle.Buffer.Add(Elapsed(timestamp), dutyCycle);
}
