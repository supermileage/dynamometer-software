using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dyno.App.ViewModels;

namespace Dyno.App.Views;

/// <summary>
/// The errors/events log. It lives in the window rather than in a page, so the same instance is
/// shown wherever the user is — a rejected sysconfig write or a dropped link is worth seeing most
/// while you are on the page that caused it.
///
/// The code-behind holds the two things a view model has no business knowing: the clipboard, and
/// how far the resize grip was dragged.
/// </summary>
public partial class EventLogView : UserControl
{
    public EventLogView() => InitializeComponent();

    /// <summary>Drag the grip to resize. Bounded at both ends: a log dragged to nothing is a log the
    /// user cannot get back by dragging, and one dragged past the window leaves no page.</summary>
    private void OnResize(object? sender, VectorEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        // Up (negative Y) grows the log, since it is anchored to the bottom.
        var available = (TopLevel.GetTopLevel(this)?.Bounds.Height ?? 800) - 200;
        vm.EventLogHeight = Math.Clamp(
            vm.EventLogHeight - e.Vector.Y,
            90,
            Math.Max(160, available)
        );
    }

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
