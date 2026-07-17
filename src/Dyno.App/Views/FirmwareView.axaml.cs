using Avalonia.Controls;

namespace Dyno.App.Views;

/// <summary>The Firmware page: build the firmware and flash it to the board. Pure markup — behavior
/// lives in <c>FirmwareViewModel</c>, and the build/flash output it used to show now belongs to the
/// window's log panel (its Console tab).</summary>
public partial class FirmwareView : UserControl
{
    public FirmwareView() => InitializeComponent();
}
