using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dyno.App.ViewModels;
using Path = Avalonia.Controls.Shapes.Path;

namespace Dyno.App.Views;

public partial class MainWindow : Window
{
    // Restore glyph (two offset squares) vs. the maximize glyph (single square).
    private const string MaximizeGlyph = "M0,0 L10,0 L10,10 L0,10 Z";
    private const string RestoreGlyph = "M2,2 L8,2 L8,8 L2,8 Z M2,2 L2,0 L10,0 L10,8 L8,8";

    public MainWindow()
    {
        InitializeComponent();

        // On X11 (incl. WSLg/Weston) the ExtendClientArea* hints don't suppress the window
        // manager's own title bar — only SystemDecorations does. Left at the default the WM
        // draws its bar *on top of* our custom chrome, i.e. a second set of window buttons.
        // Windows honours NoChrome, so we only drop decorations on Linux.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            SystemDecorations = SystemDecorations.None;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // No decorations also means no WM resize borders — our own grips stand in.
            ResizeGrips.IsVisible = true;
        }
    }

    private void OnResizeGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (
            sender is Control { Tag: string tag }
            && Enum.TryParse<WindowEdge>(tag, out var edge)
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
        )
        {
            BeginResizeDrag(edge, e);
        }
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

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize();

    private void OnMinimize(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState =
            WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            if (this.FindControl<Path>("MaximizeIcon") is { } icon)
            {
                icon.Data = Avalonia.Media.Geometry.Parse(
                    WindowState == WindowState.Maximized ? RestoreGlyph : MaximizeGlyph
                );
            }
            // A maximized window has no edges to drag; re-enabled on restore.
            ResizeGrips.IsVisible =
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                && WindowState == WindowState.Normal;
        }
    }
}
