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

    /// <summary>Whether the console is pinned to its newest line. Cleared when the user scrolls
    /// up into the backlog, restored when they scroll back to the bottom (OnScrollChanged) or
    /// (re)open the tab — so streaming output never yanks the view away from what they're
    /// reading.</summary>
    private bool _tailing = true;

    /// <summary>The next queued scroll must re-pin the tail first (set on tab switches, which
    /// always open at the newest line). Carried as a flag because the scroll runs a layout pass
    /// later, after the tab's content has actually landed.</summary>
    private bool _resumeTail;

    /// <summary>One pending scroll at a time: a build appends many lines between layout passes,
    /// and queueing a jump for each just churns the viewport.</summary>
    private bool _scrollQueued;

    public EventLogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // ScrollChanged bubbles up from the ScrollViewer inside the ListBox's template.
        EventList.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
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
        // A new tab means a new line collection to follow — and a fresh tail: opening a tailing
        // tab always lands on its newest line, whatever the previous scroll position was.
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedLogTab))
        {
            Unsubscribe();
            Subscribe();
            ScrollToEnd(resumeTail: true);
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
        ScrollToEnd(resumeTail: false);

    /// <summary>Follow a console tab's newest line, which is appended at the bottom. An events tab
    /// puts its newest at the top and is left where the user has it. Following is paused while
    /// the user is up in the backlog (see <see cref="OnScrollChanged"/>).</summary>
    private void ScrollToEnd(bool resumeTail)
    {
        // Recorded even when a scroll is already queued: that queued scroll will honor it.
        if (resumeTail)
        {
            _resumeTail = true;
        }

        if (_vm?.SelectedLogTab is not { NewestFirst: false } || _scrollQueued)
        {
            return;
        }

        _scrollQueued = true;

        // After the items are laid out.
        Dispatcher.UIThread.Post(
            () =>
            {
                _scrollQueued = false;
                if (_resumeTail)
                {
                    _resumeTail = false;
                    _tailing = true;
                }

                if (
                    _tailing
                    && _vm?.SelectedLogTab is { NewestFirst: false, Lines.Count: > 0 } tab
                )
                {
                    // By index, never by item: console lines repeat (blank lines, duplicated
                    // compiler output), and the item overload resolves by value to the FIRST
                    // equal line — jumping up into the backlog instead of to the end.
                    EventList.ScrollIntoView(tab.Lines.Count - 1);
                }
            },
            DispatcherPriority.Background
        );
    }

    /// <summary>The scroll position is the user's tailing intent: away from the bottom pauses the
    /// follow, back at the bottom resumes it. Only offset changes are read as intent — the extent
    /// growing under freshly appended lines says nothing about where the user wants to be (and is
    /// exactly the moment the view must not move if they are reading the backlog).</summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.Y == 0 || e.Source is not ScrollViewer scroll)
        {
            return;
        }

        // Within a line's height of the bottom counts as "at the bottom", so sub-pixel layout
        // rounding can't silently detach the tail.
        _tailing = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 16;
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

        // The list looks identical after a copy, so say it happened. The real label is captured
        // once, in a field: a second copy inside the 1.2 s flash would otherwise capture "Copied"
        // itself and restore that, leaving the button stuck saying it.
        _copyLabel ??= CopyEventsButton.Content;
        CopyEventsButton.Content = "Copied";
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        CopyEventsButton.Content = _copyLabel;
    }

    /// <summary>The Copy button's real label, held while it briefly reads "Copied".</summary>
    private object? _copyLabel;

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
