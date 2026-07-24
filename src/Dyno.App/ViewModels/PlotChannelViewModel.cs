using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Dyno.App.Controls;
using Dyno.Core.Plotting;

namespace Dyno.App.ViewModels;

/// <summary>
/// One plottable telemetry channel: its identity (name, unit, series color) and its sample
/// history. The color is fixed per channel — which graphs currently show it never repaints it.
/// Channels are the fixed vocabulary the user builds graphs from; the graphs themselves are
/// <see cref="PlotGraphViewModel"/>s.
/// </summary>
public partial class PlotChannelViewModel : ObservableObject, IPlotSeries
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

    /// <summary>Whether this channel has any sample inside the plotted window. Drives the legend's
    /// "no data" marking: without it a channel that never streamed looks exactly like one whose
    /// line is simply off-range or hidden under another. Updated by the page's frame timer.</summary>
    [ObservableProperty]
    private bool _hasData;

    // ---- Applied y-axis range (Settings > Plots) -----------------------------------------------
    // These are the *applied* values every graph of this channel renders with — the Settings page
    // stages edits in its own editor and writes here only on Apply, which is what makes Apply the
    // moment the plots change.

    /// <summary>True (the default) = the y-axis fits the visible data each frame. False = the
    /// fixed <see cref="AxisMin"/>/<see cref="AxisMax"/> range below; data outside it is clipped.</summary>
    [ObservableProperty]
    private bool _autoScale = true;

    [ObservableProperty]
    private double _axisMin;

    [ObservableProperty]
    private double _axisMax = 100;

    public PlotChannelViewModel(string name, string unit, Color color)
    {
        Name = name;
        Unit = unit;
        Stroke = new SolidColorBrush(color);
    }
}
