using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Dyno.App.ViewModels;

/// <summary>
/// One graph on the Plots page: which channels it overlays and which of them labels the y-axis.
/// Series carry different units, so each is drawn normalized to its own configured range
/// (Settings > Plots) or autoscale fit — never a shared or dual axis — and the numeric axis
/// belongs to the one channel picked in <see cref="AxisChannel"/>. Graphs are created and removed
/// freely (the channels and their recorded history live on independently) and share the page in
/// the auto grid that <c>PlotsView</c> lays out; size is the grid's concern, not this model's.
/// </summary>
public partial class PlotGraphViewModel : ObservableObject
{
    private readonly Action<PlotGraphViewModel> _remove;

    /// <summary>One toggle per channel, in the channels' fixed order — the graph's header chips.</summary>
    public IReadOnlyList<PlotGraphSeriesViewModel> Series { get; }

    /// <summary>The toggled-on channels, in fixed order — what the plot draws. Rebuilt (not
    /// mutated) on every toggle so bindings see one atomic change.</summary>
    [ObservableProperty]
    private IReadOnlyList<PlotChannelViewModel> _shownChannels = [];

    /// <summary>Which shown channel's range labels the y-axis, so the reader knows what the
    /// numbers mean when differently-scaled series share the strip. Follows the shown set:
    /// never null while anything is shown, never a hidden channel.</summary>
    [ObservableProperty]
    private PlotChannelViewModel? _axisChannel;

    public PlotGraphViewModel(
        IReadOnlyList<PlotChannelViewModel> channels,
        PlotChannelViewModel initiallyShown,
        Action<PlotGraphViewModel> remove
    )
    {
        _remove = remove;
        Series = channels.Select(c => new PlotGraphSeriesViewModel(c, RebuildShown)).ToList();
        Series.First(s => s.Channel == initiallyShown).IsShown = true;
    }

    /// <summary>Whether this graph currently draws <paramref name="channel"/> — lets Add graph
    /// default to something not yet on screen anywhere.</summary>
    public bool Shows(PlotChannelViewModel channel) => ShownChannels.Contains(channel);

    private void RebuildShown()
    {
        ShownChannels = Series.Where(s => s.IsShown).Select(s => s.Channel).ToList();
        if (AxisChannel is null || !ShownChannels.Contains(AxisChannel))
        {
            AxisChannel = ShownChannels.FirstOrDefault();
        }
    }

    [RelayCommand]
    private void Remove() => _remove(this);
}
