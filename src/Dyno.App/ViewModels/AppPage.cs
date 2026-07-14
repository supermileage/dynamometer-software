namespace Dyno.App.ViewModels;

/// <summary>The pages reachable from the sidebar. Exactly one is active at a time — the window
/// holds a single <c>CurrentPage</c> rather than a flag per page.</summary>
public enum AppPage
{
    Home,
    SysConfig,
}
