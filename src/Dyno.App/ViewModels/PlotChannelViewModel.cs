using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Dyno.Core.Plotting;

namespace Dyno.App.ViewModels;

/// <summary>
/// One plottable telemetry channel: its identity (name, unit, series color) and its sample
/// history. The color is fixed per channel — which graphs currently show it never repaints it.
/// Channels are the fixed vocabulary the user builds graphs from; the graphs themselves are
/// <see cref="PlotGraphViewModel"/>s.
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

    /// <summary>Right edge of the plotted window, advanced by the page's frame timer. Held on the
    /// channel (same value on every one) purely so each graph can bind to its own DataContext.</summary>
    [ObservableProperty]
    private double _anchorTime;

    public PlotChannelViewModel(string name, string unit, Color color)
    {
        Name = name;
        Unit = unit;
        Stroke = new SolidColorBrush(color);
    }
}
