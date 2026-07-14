using Avalonia.Controls;

namespace Dyno.App.Views;

/// <summary>The SysConfig page: firmware config.h/debug.h editor plus the runtime (USB) device
/// settings. Pure markup — all behavior lives in <c>SysConfigViewModel</c>.</summary>
public partial class SysConfigView : UserControl
{
    public SysConfigView() => InitializeComponent();
}
