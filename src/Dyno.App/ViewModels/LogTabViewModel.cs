using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Dyno.App.ViewModels;

/// <summary>
/// One tab in the window's bottom log panel. Every tab is the same shape — a title and a growing
/// list of lines the user can copy or clear — so the panel holds any number of them, and gains more
/// later, without its chrome (the tab strip, Copy, Clear, pin, hide, resize) needing to know what
/// any particular tab is.
///
/// What differs between tabs is only how the lines <em>read</em>: whether the newest is at the top
/// or the bottom, and whether they are coloured by severity. Those two flags cover both tabs that
/// exist today — <b>Errors / Events</b> (newest first, colour-coded) and <b>Console</b> (appended
/// and followed, plain) — and are the seam a third would slot into.
/// </summary>
public partial class LogTabViewModel : ObservableObject
{
    private readonly Func<IEnumerable<string>, string> _buildReport;
    private readonly Action _clear;

    public string Title { get; }
    public ObservableCollection<string> Lines { get; }

    /// <summary>Colour each line by its <c>[LEVEL]</c> tag. On for events; off for console, whose
    /// lines are a tool's own words and carry no such tag — rendering those all-grey would just make
    /// a long build log hard to read.</summary>
    public bool Colorize { get; }

    /// <summary>Newest line at the top (events, inserted at index 0) versus appended at the bottom
    /// and followed (console). Decides both the read order and whether the view tails the list.</summary>
    public bool NewestFirst { get; }

    /// <summary>Shown in place of the list while it is empty.</summary>
    public string EmptyText { get; }

    public LogTabViewModel(
        string title,
        ObservableCollection<string> lines,
        bool colorize,
        bool newestFirst,
        string emptyText,
        Func<IEnumerable<string>, string> buildReport,
        Action clear
    )
    {
        Title = title;
        Lines = lines;
        Colorize = colorize;
        NewestFirst = newestFirst;
        EmptyText = emptyText;
        _buildReport = buildReport;
        _clear = clear;
    }

    /// <summary>Render the given lines for the clipboard. The lines arrive in list order; a tab that
    /// shows newest-first flips them back to chronological and may prepend context (events do both),
    /// while console copies them verbatim.</summary>
    public string BuildReport(IEnumerable<string> lines) => _buildReport(lines);

    [RelayCommand]
    private void Clear() => _clear();
}
