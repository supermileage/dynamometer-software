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
        $"default {Def.Format(Def.Default)} · {Def.Format(Def.Min)} to {Def.Format(Def.Max)}";

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

    public RuntimeParameterViewModel(SysConfigParameterDef def, double? savedValue, Action edited)
    {
        Def = def;
        _edited = edited;
        _savedValue = savedValue ?? def.Default;
        _isOverride = savedValue is not null;
        _text = def.Format(_savedValue);
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
                Description,
                Unit,
                "runtime device live"
            )
            .ToLowerInvariant();
        return lowerCaseTerms.All(haystack.Contains);
    }
}
