using Avalonia.Controls;

namespace Dyno.App.Views;

/// <summary>The live console: connection toolbar, telemetry and task monitor. Pure markup — the
/// event log that used to sit at the foot of this page now belongs to the window, so it is shown on
/// every page (see <see cref="EventLogView"/>).</summary>
public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();
}
