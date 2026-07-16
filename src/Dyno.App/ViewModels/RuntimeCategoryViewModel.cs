namespace Dyno.App.ViewModels;

/// <summary>One category card in the runtime section of the SysConfig page. Its parameters are
/// split into <see cref="Subsections"/> so a category can group them by sensing path (the force
/// sensor's "All" / "I2C" / "ADC"); a category with no such split has a single untitled group.
/// </summary>
public sealed class RuntimeCategoryViewModel
{
    public required string Name { get; init; }
    public required IReadOnlyList<RuntimeSubsectionViewModel> Subsections { get; init; }
}
