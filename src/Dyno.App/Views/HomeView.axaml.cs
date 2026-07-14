using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dyno.App.ViewModels;

namespace Dyno.App.Views;

/// <summary>The live console: connection toolbar, telemetry, task monitor, and event log. The
/// code-behind only handles event-log copying; everything else binds to the main view model.</summary>
public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private void OnEventListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            CopyEvents();
            e.Handled = true;
        }
    }

    private void OnCopyEvents(object? sender, RoutedEventArgs e) => CopyEvents();

    /// <summary>
    /// Puts the selected event lines — or the whole list, when nothing is selected — on the
    /// clipboard. Selection is read as indexes into the same collection the list is bound to, so
    /// the copy keeps the list's order (and can't confuse two identically-worded events).
    /// </summary>
    private async void CopyEvents()
    {
        if (
            DataContext is not MainWindowViewModel vm
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard
            || vm.Events.Count == 0
        )
        {
            return;
        }

        var selected = EventList.Selection.SelectedIndexes;
        var lines =
            selected.Count > 0
                ? selected.Where(i => i < vm.Events.Count).Select(i => vm.Events[i])
                : vm.Events;

        try
        {
            await clipboard.SetTextAsync(vm.BuildEventReport(lines));
        }
        catch (Exception)
        {
            // No clipboard (a headless or locked-down session): nothing to recover, and failing to
            // copy must not take the window down.
            return;
        }

        // The list looks identical after a copy, so say it happened.
        object? label = CopyEventsButton.Content;
        CopyEventsButton.Content = "Copied";
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        CopyEventsButton.Content = label;
    }
}
