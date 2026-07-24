using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Dyno.App.ViewModels;

namespace Dyno.App.Views;

/// <summary>
/// The Plots page. The XAML holds the header and the graph-card template; this code-behind owns
/// the graph layout, because its shape is data-dependent in a way a static panel can't express:
/// N graphs share the whole page as a near-square grid (1 fills it, 2 stack, 4 make 2×2, 5–6 make
/// 3×2 …) with a GridSplitter in every gap, so dragging a gap trades space between the adjacent
/// row or column. The grid is rebuilt when graphs are added or removed — which necessarily resets
/// dragged proportions, since the shape they described no longer exists.
/// </summary>
public partial class PlotsView : UserControl
{
    private const double SplitterThickness = 8;
    private const double MinCellHeight = 90; // below this a strip chart is all margins, no data
    private const double MinCellWidth = 220;

    private PlotsViewModel? _plots;

    public PlotsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachToViewModel();
    }

    private void AttachToViewModel()
    {
        if (_plots is not null)
        {
            ((INotifyCollectionChanged)_plots.Graphs).CollectionChanged -= OnGraphsChanged;
        }
        _plots = (DataContext as MainWindowViewModel)?.Plots;
        if (_plots is not null)
        {
            ((INotifyCollectionChanged)_plots.Graphs).CollectionChanged += OnGraphsChanged;
        }
        RebuildGrid();
    }

    private void OnGraphsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RebuildGrid();

    /// <summary>Saves the recorded channels to a CSV the user picks. The dialog belongs here rather
    /// than in the view model because it needs this control's window; the file's contents come from
    /// <see cref="PlotsViewModel.WriteExport"/>, which is where the format is decided and tested.</summary>
    /// <remarks>The picker call is inside the try, and that is the whole point of the try. This
    /// handler is <c>async void</c>, so an exception escaping it is never returned to a caller — it
    /// reaches the synchronization context unobserved and, with no global handler installed, ends
    /// the process. Opening the dialog was outside the try, and on Linux that call is a DBus request
    /// to xdg-desktop-portal rather than anything in-process: when the portal did not answer, the
    /// button took the app down instead of reporting it, which is what "Export CSV crashes" was.
    /// </remarks>
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        var main = DataContext as MainWindowViewModel;
        var plots = main?.Plots;
        if (plots is null)
        {
            return;
        }
        if (!plots.HasRecordedData)
        {
            main!.AddEvent("[INFO] nothing to export yet — run a session first");
            return;
        }

        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                main!.AddEvent(
                    "[ERR ] this window cannot open a file dialog — no storage provider"
                );
                return;
            }

            var file = await storage.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Export plot data",
                    SuggestedFileName = $"dyno-export-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
                    DefaultExtension = "csv",
                    FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
                }
            );
            if (file is null)
            {
                return; // cancelled
            }

            // Read the buffers on the UI thread (their single-threaded contract) and write from
            // here too: the file is small enough that the alternative -- copying every sample out
            // first just to move the write off-thread -- would cost more than it saves.
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            int rows = plots.WriteExport(writer);
            main!.AddEvent($"[INFO] exported {rows} row{(rows == 1 ? "" : "s")} to {file.Name}");
        }
        catch (Exception ex)
        {
            main!.AddEvent(
                ex is TimeoutException or OperationCanceledException
                    ? "[ERR ] export failed — the system file dialog did not respond. On Linux this "
                        + "is xdg-desktop-portal; check that a portal backend for your desktop is "
                        + "installed and running"
                    : $"[ERR ] export failed — {ex.GetType().Name}: {ex.Message}"
            );
        }
    }

    private void RebuildGrid()
    {
        var host = GraphsHost;
        host.Children.Clear();
        host.RowDefinitions.Clear();
        host.ColumnDefinitions.Clear();

        var graphs = _plots?.Graphs;
        if (graphs is null || graphs.Count == 0)
        {
            host.Children.Add(
                new TextBlock
                {
                    Text = "No graphs — press Add graph to create one.",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = this.FindResource("TextMutedBrush") as IBrush,
                }
            );
            return;
        }

        // Near-square, wider than tall: rows grow first (a time-series strip wants width more
        // than height, so 2 graphs stack rather than sit side by side). 4 → 2×2, as requested.
        int n = graphs.Count;
        int rows = (int)Math.Ceiling(Math.Sqrt(n));
        int cols = (int)Math.Ceiling(n / (double)rows);

        // Graph rows/columns at even indices (star-sized: the shares the splitters trade);
        // splitter lanes at odd indices (fixed).
        for (int r = 0; r < rows; r++)
        {
            host.RowDefinitions.Add(
                new RowDefinition(1, GridUnitType.Star) { MinHeight = MinCellHeight }
            );
            if (r < rows - 1)
            {
                host.RowDefinitions.Add(new RowDefinition(SplitterThickness, GridUnitType.Pixel));
            }
        }
        for (int c = 0; c < cols; c++)
        {
            host.ColumnDefinitions.Add(
                new ColumnDefinition(1, GridUnitType.Star) { MinWidth = MinCellWidth }
            );
            if (c < cols - 1)
            {
                host.ColumnDefinitions.Add(
                    new ColumnDefinition(SplitterThickness, GridUnitType.Pixel)
                );
            }
        }

        var template = (IDataTemplate)this.FindResource("GraphCardTemplate")!;
        for (int i = 0; i < n; i++)
        {
            var card = new ContentControl { Content = graphs[i], ContentTemplate = template };
            Grid.SetRow(card, 2 * (i / cols));
            Grid.SetColumn(card, 2 * (i % cols));
            host.Children.Add(card);
        }

        int totalRows = 2 * rows - 1;
        int totalCols = 2 * cols - 1;
        for (int r = 0; r < rows - 1; r++)
        {
            var splitter = new GridSplitter
            {
                ResizeDirection = GridResizeDirection.Rows,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            Grid.SetRow(splitter, 2 * r + 1);
            Grid.SetColumnSpan(splitter, totalCols);
            host.Children.Add(splitter);
        }
        for (int c = 0; c < cols - 1; c++)
        {
            var splitter = new GridSplitter
            {
                ResizeDirection = GridResizeDirection.Columns,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            Grid.SetColumn(splitter, 2 * c + 1);
            Grid.SetRowSpan(splitter, totalRows);
            host.Children.Add(splitter);
        }
    }
}
