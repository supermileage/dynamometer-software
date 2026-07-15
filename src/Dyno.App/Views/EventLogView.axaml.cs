using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dyno.App.ViewModels;

namespace Dyno.App.Views;

/// <summary>
/// The window's bottom log panel: a strip of tabs over one list. It lives in the window rather than
/// in a page, so the same instance is shown wherever the user is — a rejected sysconfig write, a
/// dropped link, or a build's output is worth seeing on whatever page they are on.
///
/// The code-behind holds the two things a view model has no business knowing: the clipboard, and
/// how the list is scrolled — the grip's drag distance, and following the tail of a console tab.
/// </summary>
public partial class EventLogView : UserControl
{
    private MainWindowViewModel? _vm;
    private INotifyCollectionChanged? _lines;

    public EventLogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }
        Unsubscribe();

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            Subscribe();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A new tab means a new line collection to follow (and, if it is a tailing tab, to jump to
        // the end of).
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedLogTab))
        {
            Unsubscribe();
            Subscribe();
            ScrollToEndIfTailing();
        }
    }

    private void Subscribe()
    {
        if (_vm?.SelectedLogTab.Lines is INotifyCollectionChanged incc)
        {
            _lines = incc;
            _lines.CollectionChanged += OnLinesChanged;
        }
    }

    private void Unsubscribe()
    {
        if (_lines is not null)
        {
            _lines.CollectionChanged -= OnLinesChanged;
            _lines = null;
        }
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ScrollToEndIfTailing();

    /// <summary>Follow a console tab's newest line, which is appended at the bottom. An events tab
    /// puts its newest at the top and is left where the user has it.</summary>
    private void ScrollToEndIfTailing()
    {
        if (_vm?.SelectedLogTab is not { NewestFirst: false, Lines.Count: > 0 } tab)
        {
            return;
        }

        // After the item is laid out.
        Dispatcher.UIThread.Post(
            () => EventList.ScrollIntoView(tab.Lines[^1]),
            DispatcherPriority.Background
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
    /// Puts the selected lines of the active tab — or the whole tab, when nothing is selected — on
    /// the clipboard. Selection is read as indexes into the same collection the list is bound to, so
    /// the copy keeps the list's order (and can't confuse two identically-worded lines). How the
    /// lines are rendered is the tab's business: an events tab prepends context and flips to
    /// chronological order, a console tab copies verbatim.
    /// </summary>
    private async void CopyEvents()
    {
        if (
            DataContext is not MainWindowViewModel vm
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard
        )
        {
            return;
        }

        var tab = vm.SelectedLogTab;
        if (tab.Lines.Count == 0)
        {
            return;
        }

        var selected = EventList.Selection.SelectedIndexes;
        var lines =
            selected.Count > 0
                ? selected.Where(i => i < tab.Lines.Count).Select(i => tab.Lines[i])
                : tab.Lines;

        try
        {
            await clipboard.SetTextAsync(tab.BuildReport(lines));
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
}
