using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Dyno.Core.Plotting;

namespace Dyno.App.ViewModels;

/// <summary>
/// One plottable telemetry channel: its identity (name, unit, series color), its sample history,
/// and whether its strip is currently shown. The color is fixed per channel — toggling others on
/// or off never repaints a survivor.
/// </summary>
public partial class PlotChannelViewModel : ObservableObject
{
    /// <summary>~33 s of history at the force sensor's fastest realistic rate (1 kHz); other
    /// channels sample slower and so keep proportionally more time than the plot window shows.</summary>
    private const int Capacity = 32768;

    public string Name { get; }
    public string Unit { get; }
    public IBrush Stroke { get; }
    public TimeSeriesBuffer Buffer { get; } = new(Capacity);

    public string Title => Unit.Length == 0 ? Name : $"{Name} ({Unit})";

    /// <summary>Whether this channel's strip is shown. The buffer records either way, so toggling
    /// a channel on mid-session shows its history, not a line starting at the toggle.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>Right edge of the plotted window, advanced by the page's frame timer. Held on the
    /// channel (same value on every strip) purely so each strip can bind to its own DataContext.</summary>
    [ObservableProperty]
    private double _anchorTime;

    public PlotChannelViewModel(string name, string unit, Color color, bool visibleByDefault)
    {
        Name = name;
        Unit = unit;
        Stroke = new SolidColorBrush(color);
        _isVisible = visibleByDefault;
    }
}
