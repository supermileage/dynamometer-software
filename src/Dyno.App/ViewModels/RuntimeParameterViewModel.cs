using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dyno.Core.SysConfig;

namespace Dyno.App.ViewModels;

/// <summary>
/// One runtime-tunable firmware parameter on the SysConfig page. Wraps its catalog definition
/// with edit state: the text being typed, whether it parses and sits in the firmware-accepted
/// range, whether it differs from the last saved value, and whether a saved override exists at
/// all (vs. running the firmware default).
/// </summary>
public partial class RuntimeParameterViewModel : ObservableObject
{
    private readonly Action _edited;
    private double _savedValue;

    public SysConfigParameterDef Def { get; }
    public string Name => Def.Name;
    public string Category => Def.Category;
    public string Description => Def.Description;
    public bool HasUnit => Def.Unit.Length > 0;
    public string Unit => Def.Unit;

    /// <summary>Shown as the edit hint: the firmware default and accepted range.</summary>
    public string RangeText =>
        $"default {Format(Def.Default)}"
        + (
            Def.IsFloat
                ? $" · {Format(Def.Min)} to {Format(Def.Max)}"
                : $" · {Def.Min:0} to {Def.Max:0}"
        );

    /// <summary>Reverts to the firmware default: forgets the SQLite override and, when connected,
    /// pushes the default back to the device. Injected by the page view model.</summary>
    public IAsyncRelayCommand ResetCommand { get; }

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>True while the text doesn't parse or falls outside the firmware's range —
    /// exactly the values the device would reject with MALFORMED.</summary>
    [ObservableProperty]
    private bool _isInvalid;

    /// <summary>True when a saved override exists on this PC (the reset button's visibility).</summary>
    [ObservableProperty]
    private bool _isOverride;

    public RuntimeParameterViewModel(
        SysConfigParameterDef def,
        double? savedValue,
        Action edited,
        Func<RuntimeParameterViewModel, Task> reset
    )
    {
        Def = def;
        _edited = edited;
        _savedValue = savedValue ?? def.Default;
        _isOverride = savedValue is not null;
        _text = Format(_savedValue);
        ResetCommand = new AsyncRelayCommand(() => reset(this));
    }

    /// <summary>The last persisted value (or the firmware default when no override exists) —
    /// what a handshake re-push sends, regardless of any in-progress edit in the text box.</summary>
    public double SavedValue => _savedValue;

    /// <summary>The typed value, or null while it doesn't parse.</summary>
    public double? EditedValue =>
        double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    partial void OnTextChanged(string value)
    {
        IsInvalid = EditedValue is not double v || !Def.IsValid(v);
        IsDirty = !IsInvalid && EditedValue != _savedValue;
        _edited();
    }

    /// <summary>The current edit has been persisted (and pushed, when connected).</summary>
    public void MarkSaved(bool isOverride)
    {
        _savedValue = EditedValue ?? _savedValue;
        IsOverride = isOverride;
        IsDirty = false;
        _edited();
    }

    /// <summary>Puts the firmware default back in the editor after an override was forgotten.</summary>
    public void ResetToDefault()
    {
        _savedValue = Def.Default;
        IsOverride = false;
        Text = Format(Def.Default);
    }

    public bool Matches(IReadOnlyList<string> lowerCaseTerms)
    {
        var haystack = string.Join(
                ' ',
                Name,
                Name.Replace('_', ' '),
                Category,
                Description,
                Unit,
                "runtime device live"
            )
            .ToLowerInvariant();
        return lowerCaseTerms.All(haystack.Contains);
    }

    private string Format(double value) =>
        Def.IsFloat
            ? value.ToString("0.######", CultureInfo.InvariantCulture)
            : value.ToString("0", CultureInfo.InvariantCulture);
}
