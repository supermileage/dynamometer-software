using CommunityToolkit.Mvvm.ComponentModel;

namespace Dyno.App.ViewModels;

/// <summary>One channel's membership toggle on one graph — the chip in the graph's header. The
/// chips double as the graph's legend: swatch + name, always visible, so a multi-series graph
/// never identifies a line by color alone.</summary>
public partial class PlotGraphSeriesViewModel : ObservableObject
{
    private readonly Action _toggled;

    public PlotChannelViewModel Channel { get; }

    [ObservableProperty]
    private bool _isShown;

    public PlotGraphSeriesViewModel(PlotChannelViewModel channel, Action toggled)
    {
        Channel = channel;
        _toggled = toggled;
    }

    partial void OnIsShownChanged(bool value) => _toggled();
}
