using CommunityToolkit.Mvvm.ComponentModel;
using Dyno.Core;

namespace Dyno.App.ViewModels;

/// <summary>One row in the task-monitor table; updated in place as new samples arrive.</summary>
public partial class TaskMonitorRow : ObservableObject
{
    [ObservableProperty]
    private string _task = string.Empty;

    /// <summary>The raw <c>task_state</c> from the device. Shown as <see cref="StateLabel"/>; kept
    /// as the number behind it so an unrecognised state is still reportable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    private int _state;

    /// <summary>The state as a name ("Running", "Blocked", …) — the number alone means nothing to
    /// someone reading the table.</summary>
    public string StateLabel => ThreadStateExtensions.ToLabel(State);

    [ObservableProperty]
    private uint _freeBytes;

    [ObservableProperty]
    private uint _timestamp;
}
