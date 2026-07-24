using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Dyno.App.ViewModels;

/// <summary>
/// The Settings page: application preferences, organized in subsections. One subsection today —
/// Plots — holding the per-channel y-axis configuration; further subsections slot in beside it.
/// (Device settings are not here: they live on the SysConfig page, because they leave the app.)
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    /// <summary>One y-axis editor per plot channel, in the channels' fixed order.</summary>
    public IReadOnlyList<PlotAxisSettingViewModel> PlotAxes { get; }

    /// <summary>The channel whose range is being viewed/edited — ranges differ per channel (they
    /// carry different units), so the page shows one at a time, chosen here.</summary>
    [ObservableProperty]
    private PlotAxisSettingViewModel _selectedPlotAxis;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public SettingsViewModel(PlotsViewModel plots)
    {
        PlotAxes = plots.Channels.Select(c => new PlotAxisSettingViewModel(c, Recount)).ToList();
        _selectedPlotAxis = PlotAxes[0];
    }

    /// <summary>Apply is the page's one commit point: it pushes every staged, changed editor onto
    /// its channel in one click, and the plots re-render with the new ranges immediately. Disabled
    /// while any editor is invalid — a min above its max must be fixed, not silently skipped.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        int applied = 0;
        foreach (var axis in PlotAxes.Where(a => a.IsDirty))
        {
            axis.Apply();
            applied++;
        }
        StatusText =
            applied == 0
                ? "Nothing to apply."
                : $"Applied {applied} axis range{(applied == 1 ? "" : "s")} — plots updated.";
    }

    private bool CanApply => PlotAxes.All(a => !a.HasError) && PlotAxes.Any(a => a.IsDirty);

    private void Recount()
    {
        ApplyCommand.NotifyCanExecuteChanged();
        StatusText = string.Empty;
    }
}
