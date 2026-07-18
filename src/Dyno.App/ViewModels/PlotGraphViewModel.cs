using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Dyno.App.ViewModels;

/// <summary>
/// One graph on the Plots page: which channel it shows and how tall it is. Graphs are created and
/// removed freely; the channels (and their recorded history) live on independently of them, so
/// switching a graph to another channel — or adding a second graph of the same one — costs
/// nothing and arrives with history.
/// </summary>
public partial class PlotGraphViewModel : ObservableObject
{
    public const double MinHeight = 80;
    public const double MaxHeight = 600;

    private readonly Action<PlotGraphViewModel> _remove;

    /// <summary>The channel picker's choices — every channel, shared by reference from the page.</summary>
    public IReadOnlyList<PlotChannelViewModel> Channels { get; }

    [ObservableProperty]
    private PlotChannelViewModel _channel;

    /// <summary>Plot-area height in pixels, dragged via the grip under the graph. This is what
    /// "bigger than that one" means here: heights are independent, the page scrolls.</summary>
    [ObservableProperty]
    private double _plotHeight = 150;

    public PlotGraphViewModel(
        IReadOnlyList<PlotChannelViewModel> channels,
        PlotChannelViewModel channel,
        Action<PlotGraphViewModel> remove
    )
    {
        Channels = channels;
        _channel = channel;
        _remove = remove;
    }

    public void Resize(double delta) =>
        PlotHeight = Math.Clamp(PlotHeight + delta, MinHeight, MaxHeight);

    [RelayCommand]
    private void Remove() => _remove(this);
}
