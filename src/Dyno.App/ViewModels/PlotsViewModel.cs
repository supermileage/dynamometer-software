using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace Dyno.App.ViewModels;

/// <summary>
/// The Plots page: a user-built list of graphs, each showing one telemetry channel over time and
/// each independently resizable. Different channels carry different units, so a graph shows
/// exactly one — there is deliberately no shared (or dual) y-axis, and only the time axis lines
/// up across graphs.
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

    /// <summary>The graphs, top to bottom. Starts with a useful default set rather than empty —
    /// a blank plots page teaches nothing about what the page can do.</summary>
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

        foreach (var channel in new[] { AngularVelocity, Torque, Power })
        {
            Graphs.Add(new PlotGraphViewModel(Channels, channel, RemoveGraph));
        }

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
