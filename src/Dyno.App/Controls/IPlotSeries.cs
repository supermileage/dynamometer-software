using Avalonia.Media;
using Dyno.Core.Plotting;

namespace Dyno.App.Controls;

/// <summary>
/// What <see cref="TimeSeriesPlot"/> needs to draw one series: its samples, its color, and its
/// y-axis policy (a fixed range from Settings, or autoscale). Each series is normalized to its
/// <em>own</em> range on the shared vertical, which is how channels with different units overlay
/// without a dual axis: shapes and timing compare; magnitudes read off whichever channel the
/// graph's axis selector names.
/// </summary>
public interface IPlotSeries
{
    TimeSeriesBuffer Buffer { get; }
    IBrush Stroke { get; }
    string Title { get; }

    /// <summary>True: fit this series' scale to its visible data each frame. False: use the
    /// fixed <see cref="AxisMin"/>/<see cref="AxisMax"/>; data outside is clipped.</summary>
    bool AutoScale { get; }
    double AxisMin { get; }
    double AxisMax { get; }
}
