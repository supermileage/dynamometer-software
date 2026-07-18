using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Dyno.App.ViewModels;

namespace Dyno.App.Views;

public partial class PlotsView : UserControl
{
    public PlotsView()
    {
        InitializeComponent();
    }

    private void OnResizeGraph(object? sender, VectorEventArgs e)
    {
        // The grip sits under its graph, so dragging down (positive Y) grows it.
        if (sender is Thumb { DataContext: PlotGraphViewModel graph })
        {
            graph.Resize(e.Vector.Y);
        }
    }
}
