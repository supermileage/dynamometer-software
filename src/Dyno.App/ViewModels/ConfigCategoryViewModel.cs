namespace Dyno.App.ViewModels;

/// <summary>One section card on the SysConfig page: a header (category + source file) over the
/// settings that survived the current search filter.</summary>
public sealed class ConfigCategoryViewModel
{
    public required string Name { get; init; }
    public required string FileLabel { get; init; }
    public required IReadOnlyList<ConfigParameterViewModel> Parameters { get; init; }
}
