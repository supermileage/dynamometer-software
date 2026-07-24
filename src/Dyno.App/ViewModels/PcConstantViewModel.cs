using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    /// <summary>The edit hint, worded exactly like a runtime parameter's: what this constant falls
    /// back to and what it will accept. The default is part of it because it is otherwise nowhere
    /// on the page — these have no header and no firmware to read one from, so if this line does
    /// not say what the value started as, nothing does.</summary>
    public string RangeText =>
        $"default {Format(Default)} · {Format(Minimum)} to {Format(Maximum)}";

    /// <summary>The value in force right now — what the derivation actually uses.</summary>
    public double Value { get; private set; }

    /// <summary>Puts the default back in the editor. Staged like any typed value: Apply is what
    /// saves it, so a reset can be walked back the same way an edit can.</summary>
    public IRelayCommand ResetCommand { get; }

    /// <summary>Whether Reset has anything to undo — the value in force is not the default, or the
    /// text staged over it is not. The second half is what makes it useful in the case it is most
    /// wanted: a value the box will not accept, where there is no parsed number to compare and the
    /// only way back would otherwise be to remember what the default was.</summary>
    public bool CanReset => Value != Default || EditedText != Format(Default);

    /// <summary>True while the box holds something Apply will not take. Mirrored onto the box
    /// itself, not just the message beside it: a greyed-out Apply with no visible cause reads as a
    /// broken button rather than as a rejected value.</summary>
    public bool IsInvalid => Error is not null;

    [ObservableProperty]
    private string _editedText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInvalid))]
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
        ResetCommand = new RelayCommand(() => EditedText = Format(Default));
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
        OnPropertyChanged(nameof(CanReset));
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
            OnPropertyChanged(nameof(CanReset));
        }
        // Unconditionally, and this is the whole point of it: the row is no longer pending, and the
        // page's pending count is what enables Apply and reports what is outstanding. Setting
        // EditedText above usually re-formats to the same string, which raises nothing, so leaving
        // the recount to that left the count stuck at its pre-Apply value — a page that went on
        // claiming an unsaved change forever, and an Apply button that had already saved.
        _edited();
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
