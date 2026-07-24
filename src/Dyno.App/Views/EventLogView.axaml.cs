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
/// how the list is scrolled — the grip's drag distance, and following whichever end of a tab its
/// newest line arrives at.
/// </summary>
public partial class EventLogView : UserControl
{
    private MainWindowViewModel? _vm;
    private INotifyCollectionChanged? _lines;

    /// <summary>Whether the list is pinned to the selected tab's newest line — the top of an events
    /// tab, the bottom of a console one. Cleared when the user scrolls away into the backlog,
    /// restored when they scroll back to that end (OnScrollChanged) or (re)open the tab — so new
    /// lines never yank the view away from what they're reading.</summary>
    private bool _pinned = true;

    /// <summary>The next queued scroll must re-pin first (set on tab switches, which always open at
    /// the newest line). Carried as a flag because the scroll runs a layout pass later, after the
    /// tab's content has actually landed.</summary>
    private bool _resumePin;

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
            ScrollToNewest(resumePin: true);
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
        ScrollToNewest(resumePin: false);

    /// <summary>Follow the selected tab's newest line, at whichever end of the list that tab puts
    /// it: a console tab appends at the bottom, an events tab inserts at the top. Following is
    /// paused while the user is away from that end, down in the backlog (see
    /// <see cref="OnScrollChanged"/>).</summary>
    /// <remarks>Both tabs share one ListBox, so an events tab that is never scrolled does not open
    /// at its top — it opens wherever the console left the shared ScrollViewer, which is the bottom,
    /// showing the oldest events under a list that grows upward. Hence scrolling both, rather than
    /// only the tab that appends.</remarks>
    private void ScrollToNewest(bool resumePin)
    {
        // Recorded even when a scroll is already queued: that queued scroll will honor it.
        if (resumePin)
        {
            _resumePin = true;
        }

        if (_scrollQueued)
        {
            return;
        }

        _scrollQueued = true;

        // After the items are laid out.
        Dispatcher.UIThread.Post(
            () =>
            {
                _scrollQueued = false;
                if (_resumePin)
                {
                    _resumePin = false;
                    _pinned = true;
                }

                if (_pinned && _vm?.SelectedLogTab is { Lines.Count: > 0 } tab)
                {
                    // By index, never by item: console lines repeat (blank lines, duplicated
                    // compiler output), and the item overload resolves by value to the FIRST
                    // equal line — jumping up into the backlog instead of to the end.
                    EventList.ScrollIntoView(tab.NewestFirst ? 0 : tab.Lines.Count - 1);
                }
            },
            DispatcherPriority.Background
        );
    }

    /// <summary>The scroll position is the user's intent to keep following: away from the newest
    /// end pauses the follow, back at it resumes. Which end that is comes from the tab — the top
    /// for events, the bottom for console. Only offset changes are read as intent — the extent
    /// growing under freshly logged lines says nothing about where the user wants to be (and is
    /// exactly the moment the view must not move if they are reading the backlog).</summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.Y == 0 || e.Source is not ScrollViewer scroll)
        {
            return;
        }

        // Within a line's height of the end counts as being at it, so sub-pixel layout rounding
        // can't silently unpin the follow.
        _pinned =
            _vm?.SelectedLogTab.NewestFirst == true
                ? scroll.Offset.Y <= 16
                : scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 16;
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
