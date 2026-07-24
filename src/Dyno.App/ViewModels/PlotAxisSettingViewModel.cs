using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Dyno.App.ViewModels;

/// <summary>
/// The staged y-axis settings for one plot channel on the Settings page: autoscale on/off and,
/// when off, a fixed min/max. Edits live here until Apply copies them onto the channel — which is
/// the moment every graph of that channel re-renders — so half-typed numbers never touch a plot.
/// </summary>
public partial class PlotAxisSettingViewModel : ObservableObject
{
    private readonly Action _edited;

    public PlotChannelViewModel Channel { get; }

    public string Title => Channel.Title;

    [ObservableProperty]
    private bool _autoScale;

    [ObservableProperty]
    private string _minText;

    [ObservableProperty]
    private string _maxText;

    /// <summary>Why the staged values can't apply, or empty. The one rule beyond "a number" is
    /// min &lt; max — an empty or inverted range describes no axis at all.</summary>
    [ObservableProperty]
    private string _error = string.Empty;

    public bool HasError => Error.Length > 0;

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>The range every graph of this channel is showing right now — what Apply last set
    /// (or the defaults). Shown so editing starts from what you are looking at.</summary>
    public string AppliedSummary =>
        Channel.AutoScale
            ? "Applied: autoscale (axis follows the data)"
            : $"Applied: {Format(Channel.AxisMin)} to {Format(Channel.AxisMax)}";

    public PlotAxisSettingViewModel(PlotChannelViewModel channel, Action edited)
    {
        Channel = channel;
        _edited = edited;
        _autoScale = channel.AutoScale;
        _minText = Format(channel.AxisMin);
        _maxText = Format(channel.AxisMax);
    }

    partial void OnAutoScaleChanged(bool value) => Refresh();

    partial void OnMinTextChanged(string value) => Refresh();

    partial void OnMaxTextChanged(string value) => Refresh();

    private void Refresh()
    {
        Error = Validate();
        OnPropertyChanged(nameof(HasError));
        IsDirty =
            Error.Length == 0
            && (
                AutoScale != Channel.AutoScale
                || (!AutoScale && (Min() != Channel.AxisMin || Max() != Channel.AxisMax))
            );
        _edited();
    }

    private string Validate()
    {
        if (AutoScale)
        {
            return string.Empty; // min/max are ignored, so stale text in them is not an error
        }
        if (!TryParse(MinText, out double min))
        {
            return "Min must be a number.";
        }
        if (!TryParse(MaxText, out double max))
        {
            return "Max must be a number.";
        }
        if (min >= max)
        {
            return "Min must be less than max.";
        }
        return string.Empty;
    }

    /// <summary>Copies the staged values onto the channel. Caller guarantees validity (Apply is
    /// disabled while any editor has an error).</summary>
    public void Apply()
    {
        Channel.AutoScale = AutoScale;
        if (!AutoScale)
        {
            Channel.AxisMin = Min();
            Channel.AxisMax = Max();
        }
        OnPropertyChanged(nameof(AppliedSummary));
        Refresh();
    }

    private double Min() => TryParse(MinText, out double v) ? v : Channel.AxisMin;

    private double Max() => TryParse(MaxText, out double v) ? v : Channel.AxisMax;

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
