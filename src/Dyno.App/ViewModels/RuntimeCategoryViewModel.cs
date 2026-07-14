namespace Dyno.App.ViewModels;

/// <summary>One category card in the runtime section of the SysConfig page.</summary>
public sealed class RuntimeCategoryViewModel
{
    public required string Name { get; init; }
    public required IReadOnlyList<RuntimeParameterViewModel> Parameters { get; init; }
}
