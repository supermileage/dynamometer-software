using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Dyno.App.ViewModels;

/// <summary>
/// One constant this PC uses to derive torque, power and the geared readouts. Unlike the runtime
/// parameters beside it on the SysConfig page, these are never sent to the device: nothing in the
/// firmware reads them, so they live only in the app's database. Changing one takes effect on the
/// next sample rather than needing a rebuild or a reflash.
/// </summary>
public partial class PcConstantViewModel : ObservableObject
{
    private readonly Action _edited;

    public string Name { get; }
    public string Label { get; }
    public string Unit { get; }
    public string Description { get; }
    public double Minimum { get; }
    public double Maximum { get; }
    public double Default { get; }

    public bool HasUnit => Unit.Length > 0;

    public string RangeText =>
        $"{Minimum.ToString("0.######", CultureInfo.InvariantCulture)} … "
        + $"{Maximum.ToString("0.######", CultureInfo.InvariantCulture)}";

    /// <summary>The value in force right now — what the derivation actually uses.</summary>
    public double Value { get; private set; }

    [ObservableProperty]
    private string _editedText = string.Empty;

    [ObservableProperty]
    private string? _error;

    public PcConstantViewModel(
        string name,
        string label,
        string unit,
        string description,
        double minimum,
        double maximum,
        double defaultValue,
        double? savedValue,
        Action edited
    )
    {
        Name = name;
        Label = label;
        Unit = unit;
        Description = description;
        Minimum = minimum;
        Maximum = maximum;
        Default = defaultValue;
        _edited = edited;
        Value = savedValue ?? defaultValue;
        _editedText = Format(Value);
    }

    /// <summary>True when the box holds a valid number that differs from the value in force.</summary>
    public bool IsDirty => Error is null && ParsedValue is { } parsed && parsed != Value;

    /// <summary>The staged value, or null when the text is not a usable number.</summary>
    public double? ParsedValue =>
        double.TryParse(
            EditedText,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed
        )
        && parsed >= Minimum
        && parsed <= Maximum
            ? parsed
            : null;

    partial void OnEditedTextChanged(string value)
    {
        // Validated as typed so Apply can be blocked on a bad value rather than silently coercing
        // one -- a mistyped lever arm would rescale every torque reading with nothing to show why.
        Error = double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed
        )
            ? parsed < Minimum || parsed > Maximum
                ? $"must be between {RangeText}"
                : null
            : "not a number";
        OnPropertyChanged(nameof(IsDirty));
        _edited();
    }

    /// <summary>Accepts the staged value as the one now in force.</summary>
    public void MarkSaved()
    {
        if (ParsedValue is { } parsed)
        {
            Value = parsed;
            EditedText = Format(Value);
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    public bool Matches(IReadOnlyList<string> lowerCaseTerms) =>
        lowerCaseTerms.All(term =>
            Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || Label.Contains(term, StringComparison.OrdinalIgnoreCase)
            || Description.Contains(term, StringComparison.OrdinalIgnoreCase)
        );

    private static string Format(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);
}
