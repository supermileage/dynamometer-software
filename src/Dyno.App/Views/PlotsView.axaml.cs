using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
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
