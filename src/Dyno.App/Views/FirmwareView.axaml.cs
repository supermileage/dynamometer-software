using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Dyno.App.ViewModels;

namespace Dyno.App.Views;

/// <summary>The Firmware page: build the firmware and flash it to the board. Behavior lives in
/// <c>FirmwareViewModel</c>; the only thing here is following the output, which a view model cannot
/// do because scrolling is not state.</summary>
public partial class FirmwareView : UserControl
{
    private INotifyCollectionChanged? _output;

    public FirmwareView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_output is not null)
        {
            _output.CollectionChanged -= OnOutputChanged;
        }

        _output = (DataContext as MainWindowViewModel)?.Firmware.Output;
        if (_output is not null)
        {
            _output.CollectionChanged += OnOutputChanged;
        }
    }

    /// <summary>Keep the newest line in view. A build prints for minutes and its last line is the
    /// one that matters — an output pane you have to chase is worse than none.</summary>
    private void OnOutputChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        // After the item is laid out, or there is nothing yet to scroll to.
        Dispatcher.UIThread.Post(() => OutputScroller.ScrollToEnd(), DispatcherPriority.Background);
}
