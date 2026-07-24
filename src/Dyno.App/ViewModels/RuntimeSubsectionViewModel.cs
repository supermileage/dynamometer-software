namespace Dyno.App.ViewModels;

/// <summary>A labelled sub-group inside a runtime category card — e.g. the force sensor's "I2C"
/// (ADS1115) vs "ADC" (STM32 internal) parameters. A category whose parameters carry no
/// subsection has a single group with an empty <see cref="Title"/>, which renders with no
/// sub-header so the card looks exactly as it did before subsections existed.</summary>
public sealed class RuntimeSubsectionViewModel
{
    public required string Title { get; init; }
    public bool HasTitle => Title.Length > 0;
    public required IReadOnlyList<RuntimeParameterViewModel> Parameters { get; init; }
}
