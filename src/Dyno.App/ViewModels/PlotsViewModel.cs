using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;

namespace Dyno.App.ViewModels;

/// <summary>
/// The Plots page: a fixed set of telemetry channels, each an independently toggleable strip
/// chart over time. Different channels carry different units, so there is deliberately no shared
/// y-axis — every strip is its own small multiple and only the time axis lines up.
/// </summary>
/// <remarks>
/// Samples are recorded from <see cref="MainWindowViewModel.Apply"/> on the UI thread — the same
/// gate the numeric readouts use, so the plots record exactly what the readouts showed. Rendering
/// reads the buffers on the UI thread too, so nothing here locks.
///
/// Series colors are the dataviz reference palette's dark-mode categorical slots in fixed order,
/// validated (CVD separation, normal-vision floor, ≥3:1 contrast) against this app's #171A20
/// card surface. Identity never rides on color alone: every strip is titled.
/// </remarks>
public partial class PlotsViewModel
{
    /// <summary>Seconds of history each strip shows.</summary>
    public const double WindowSeconds = 30;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _frameTimer;

    public PlotChannelViewModel AngularVelocity { get; } =
        new("Angular velocity", "rad/s", Color.Parse("#3987E5"), visibleByDefault: true);

    public PlotChannelViewModel AngularVelocityGeared { get; } =
        new("Angular velocity, geared", "rad/s", Color.Parse("#008300"), visibleByDefault: false);

    public PlotChannelViewModel AngularAcceleration { get; } =
        new("Angular acceleration", "rad/s²", Color.Parse("#D55181"), visibleByDefault: false);

    public PlotChannelViewModel Force { get; } =
        new("Force", "N", Color.Parse("#C98500"), visibleByDefault: false);

    public PlotChannelViewModel Torque { get; } =
        new("Torque", "N·m", Color.Parse("#199E70"), visibleByDefault: true);

    public PlotChannelViewModel TorqueGeared { get; } =
        new("Torque, geared", "N·m", Color.Parse("#D95926"), visibleByDefault: false);

    public PlotChannelViewModel Power { get; } =
        new("Power", "W", Color.Parse("#9085E9"), visibleByDefault: true);

    public PlotChannelViewModel DutyCycle { get; } =
        new("BPM duty cycle", "", Color.Parse("#E66767"), visibleByDefault: false);

    public IReadOnlyList<PlotChannelViewModel> Channels { get; }

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

    private void AdvanceAnchor()
    {
        double latest = 0;
        foreach (var channel in Channels)
        {
            if (channel.Buffer.Count > 0)
            {
                latest = Math.Max(latest, channel.Buffer.LatestTime);
            }
        }
        foreach (var channel in Channels)
        {
            channel.AnchorTime = latest; // no-op notification when unchanged
        }
    }

    /// <summary>A new session began: drop the previous run so the strips start clean.</summary>
    public void OnSessionStarted()
    {
        foreach (var channel in Channels)
        {
            channel.Buffer.Clear();
        }
    }

    public void RecordOpticalEncoder(
        float angularVelocity,
        float angularAcceleration,
        double gearRatio
    )
    {
        double now = _clock.Elapsed.TotalSeconds;
        AngularVelocity.Buffer.Add(now, angularVelocity);
        AngularVelocityGeared.Buffer.Add(now, (float)(angularVelocity * gearRatio));
        AngularAcceleration.Buffer.Add(now, angularAcceleration);
    }

    public void RecordForce(float force) => Force.Buffer.Add(_clock.Elapsed.TotalSeconds, force);

    public void RecordSessionController(float torque, float power, double gearRatio)
    {
        double now = _clock.Elapsed.TotalSeconds;
        Torque.Buffer.Add(now, torque);
        TorqueGeared.Buffer.Add(now, (float)(torque * gearRatio));
        Power.Buffer.Add(now, power);
    }

    public void RecordDutyCycle(float dutyCycle) =>
        DutyCycle.Buffer.Add(_clock.Elapsed.TotalSeconds, dutyCycle);
}
