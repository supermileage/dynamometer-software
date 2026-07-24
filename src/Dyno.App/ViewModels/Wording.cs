namespace Dyno.App.ViewModels;

/// <summary>Phrasing helpers shared by the view models' user-facing strings.</summary>
internal static class Wording
{
    /// <summary>A count with its noun pluralised: "1 device", "3 devices".</summary>
    public static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
