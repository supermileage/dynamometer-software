namespace Dyno.App.ViewModels;

/// <summary>One section card on the SysConfig page: a header over the settings that survived the
/// current search filter.</summary>
/// <remarks>The card no longer names the header file a section came from, so there is no FileLabel
/// here — but the grouping behind it still keys on the file, since two headers may use the same
/// section name and merging them into one card would be wrong.</remarks>
public sealed class ConfigCategoryViewModel
{
    public required string Name { get; init; }
    public required IReadOnlyList<ConfigParameterViewModel> Parameters { get; init; }
}
