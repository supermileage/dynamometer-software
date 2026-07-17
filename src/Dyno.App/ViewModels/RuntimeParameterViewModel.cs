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

    /// <summary>Which sensing-path sub-group of the category this belongs to (e.g. the force
    /// sensor's "I2C"/"ADC"), or empty when it sits directly under the category.</summary>
    public string Subsection => Def.Subsection;
    public string Description => Def.Description;
    public bool HasUnit => Def.Unit.Length > 0;
    public string Unit => Def.Unit;

    /// <summary>True when this parameter is picked from a fixed set of labelled codes, shown as a
    /// dropdown rather than a number box.</summary>
    public bool IsEnum => Def.IsEnum;

    /// <summary>The selectable codes for an <see cref="IsEnum"/> parameter (empty otherwise).</summary>
    public IReadOnlyList<SysConfigEnumOption> Options =>
        Def.Options ?? Array.Empty<SysConfigEnumOption>();

    /// <summary>Shown as the edit hint: the firmware default — and, for a number, the accepted
    /// range. For an enum it names the default option so the dropdown's baseline is clear.</summary>
    public string RangeText =>
        IsEnum
            ? $"default {LabelFor(Def.Default)}"
            : $"default {Def.Format(Def.Default)} · {Def.Format(Def.Min)} to {Def.Format(Def.Max)}";

    private string LabelFor(double value) =>
        Options.FirstOrDefault(o => o.Value == (uint)value)?.Label ?? Def.Format(value);

    private SysConfigEnumOption? OptionFor(double value) =>
        Options.FirstOrDefault(o => o.Value == (uint)value);

    /// <summary>Stages a return to the firmware default. Like typing a value, it only fills the
    /// editor and marks the row changed — Apply is what saves it.</summary>
    public IRelayCommand ResetCommand { get; }

    /// <summary>True once Reset has been pressed and Apply has not yet run: the override is to be
    /// forgotten, not merely overwritten.</summary>
    /// <remarks>A flag rather than a comparison against the default, because the two are not the
    /// same thing. An override that happens to <em>equal</em> the default is still an override — a
    /// saved row that resetting removes — so a value-only test would find nothing changed, leave the
    /// row un-dirty, and leave Apply greyed out with a reset the user had clearly asked for.</remarks>
    [ObservableProperty]
    private bool _resetRequested;

    /// <summary>True while <see cref="Text"/> is being set by the page rather than typed into.</summary>
    private bool _settingText;

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>True while the text doesn't parse or falls outside the firmware's range —
    /// exactly the values the device would reject with MALFORMED.</summary>
    [ObservableProperty]
    private bool _isInvalid;

    /// <summary>True when a saved override exists on this PC (the reset button's visibility). Stays
    /// true through a pending reset — the override is not gone until Apply says so.</summary>
    [ObservableProperty]
    private bool _isOverride;

    /// <summary>The dropdown selection for an <see cref="IsEnum"/> parameter. Two-way bound; a user
    /// pick drives the same <see cref="Text"/> pipeline a typed value would, so dirty tracking,
    /// validation and reset behave identically for enums and numbers.</summary>
    [ObservableProperty]
    private SysConfigEnumOption? _selectedOption;

    /// <summary>True while <see cref="SelectedOption"/> is being synced from the value rather than
    /// chosen by the user — the guard that keeps the option/text bridge from looping.</summary>
    private bool _settingOption;

    public RuntimeParameterViewModel(SysConfigParameterDef def, double? savedValue, Action edited)
    {
        Def = def;
        _edited = edited;
        _savedValue = savedValue ?? def.Default;
        _isOverride = savedValue is not null;
        _text = def.Format(_savedValue);
        _selectedOption = def.IsEnum ? OptionFor(_savedValue) : null;
        ResetCommand = new RelayCommand(RequestReset);
    }

    /// <summary>Puts the firmware default in the editor and marks the row as a pending reset. It is
    /// staged, not done: nothing reaches the database or the device until Apply, so a reset can be
    /// walked back the same way any other edit can — by typing over it.</summary>
    private void RequestReset()
    {
        _settingText = true;
        Text = Def.Format(Def.Default);
        _settingText = false;

        ResetRequested = true;
        RefreshDirty();
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
        // Typing a value *is* choosing an override, so it cancels a reset the user has staged but
        // not yet applied — otherwise Apply would be asked to forget the override and save a new one
        // in the same breath.
        if (!_settingText)
        {
            ResetRequested = false;
        }
        RefreshDirty();
        SyncSelectedOption();
    }

    /// <summary>User picked a dropdown option: route it through <see cref="Text"/> so it behaves
    /// exactly like a typed value. Ignored while the option is being synced back from the value.</summary>
    partial void OnSelectedOptionChanged(SysConfigEnumOption? value)
    {
        if (_settingOption || value is null)
        {
            return;
        }
        Text = Def.Format(value.Value);
    }

    /// <summary>Keeps the dropdown selection matching the current value after any Text change
    /// (typed, reset, or restored), without re-triggering the option handler.</summary>
    private void SyncSelectedOption()
    {
        if (!IsEnum)
        {
            return;
        }
        var match = EditedValue is double v ? OptionFor(v) : null;
        if (!ReferenceEquals(match, SelectedOption))
        {
            _settingOption = true;
            SelectedOption = match;
            _settingOption = false;
        }
    }

    private void RefreshDirty()
    {
        IsInvalid = EditedValue is not double v || !Def.IsValid(v);
        IsDirty = !IsInvalid && (ResetRequested || EditedValue != _savedValue);
        _edited();
    }

    /// <summary>The current edit has been persisted (and pushed, when connected).</summary>
    public void MarkSaved(bool isOverride)
    {
        _savedValue = EditedValue ?? _savedValue;
        IsOverride = isOverride;
        ResetRequested = false;
        IsDirty = false;
        _edited();
    }

    /// <summary>The pending reset has been applied: the override is forgotten and the firmware
    /// default is what this parameter now wants (and so what the next sync sends).</summary>
    public void MarkReset()
    {
        _savedValue = Def.Default;
        _settingText = true;
        Text = Def.Format(Def.Default);
        _settingText = false;

        IsOverride = false;
        ResetRequested = false;
        IsDirty = false;
        _edited();
    }

    public bool Matches(IReadOnlyList<string> lowerCaseTerms)
    {
        var haystack = string.Join(
                ' ',
                Name,
                Name.Replace('_', ' '),
                Category,
                Subsection,
                Description,
                Unit,
                string.Join(' ', Options.Select(o => o.Label)),
                "runtime device live"
            )
            .ToLowerInvariant();
        return lowerCaseTerms.All(haystack.Contains);
    }
}
