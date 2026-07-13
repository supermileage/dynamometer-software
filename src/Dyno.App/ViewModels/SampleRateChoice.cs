using Dyno.Core;

namespace Dyno.App.ViewModels;

/// <summary>
/// A force-sensor sample rate paired with its display label, for binding to the rate combo box.
/// <see cref="ToString"/> returns the label so the (template-less) <c>ComboBox</c> renders it.
/// </summary>
public sealed record SampleRateChoice(ForceSensorSampleRate Value, string Label)
{
    public override string ToString() => Label;
}
