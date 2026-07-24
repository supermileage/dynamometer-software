using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dyno.Core.Firmware;

namespace Dyno.App.ViewModels;

/// <summary>
/// One editable firmware <c>#define</c> on the SysConfig page. Wraps a parsed
/// <see cref="ConfigDefine"/> with edit state: the value being typed (or toggled), whether it
/// differs from the last saved value, whether it differs from the header at all, and the text
/// searched against.
///
/// The header's value is the firmware's — the app never writes it — so a saved value here is a
/// statement of intent, not a change to the build. <see cref="HeaderValue"/> is kept alongside it
/// so the page can always show what the running firmware actually has.
/// </summary>
public partial class ConfigParameterViewModel : ObservableObject
{
    private readonly Action _edited;
    private readonly bool _boolUsesWords; // true/false on disk, vs 1/0
    private readonly string _searchHaystack;
    private string _savedValue;

    public string Name { get; }
    public string Category { get; }
    public string FileLabel { get; }
    public string Description { get; }
    public bool IsBool { get; }
    public bool IsText => !IsBool;
    public bool HasDescription => Description.Length > 0;

    /// <summary>What the header declares, and so what a clean tree would build.</summary>
    public string HeaderValue { get; }

    /// <summary>The last applied value — what the next firmware build will use. Not the text in the
    /// box, which may be an edit the user hasn't pressed Apply on.</summary>
    public string SavedValue => _savedValue;

    /// <summary>Shown while the saved value differs from the header, which is the only case where
    /// the two can be confused. Says what the firmware has rather than which file says so: the
    /// filename is not something a reader of this page can act on, and naming it was what made the
    /// line long enough to be clipped.</summary>
    public string HeaderText => $"firmware has {HeaderValue}";

    /// <summary>Puts the header's value back in the editor. Staged like any other edit — Apply is
    /// what drops the saved row.</summary>
    public IRelayCommand ResetCommand { get; }

    /// <summary>Free-form value for non-bool settings, as shown in the TextBox.</summary>
    [ObservableProperty]
    private string _text;

    /// <summary>Toggle state for bool settings.</summary>
    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>True while the box is empty: a <c>#define</c> with no value is an include guard,
    /// not a setting, so there is nothing to save.</summary>
    [ObservableProperty]
    private bool _isInvalid;

    /// <summary>True when the saved value differs from the header's — the page is showing something
    /// the firmware does not have. Drives the Reset button.</summary>
    [ObservableProperty]
    private bool _isOverride;

    public ConfigParameterViewModel(
        ConfigDefine define,
        string fileLabel,
        string? savedValue,
        Action edited
    )
    {
        _edited = edited;
        Name = define.Name;
        Category = define.Category;
        FileLabel = fileLabel;
        Description = define.Description;
        IsBool = define.Kind == ConfigValueKind.Bool;
        HeaderValue = define.Value;
        _boolUsesWords = define.Value is "true" or "false";
        _savedValue = savedValue ?? define.Value;
        _isOverride = savedValue is not null && savedValue != define.Value;
        _text = _savedValue;
        _isOn = _savedValue is "1" or "true";
        ResetCommand = new RelayCommand(Reset);
        _searchHaystack = string.Join(
                ' ',
                Name,
                Name.Replace('_', ' '),
                Category,
                Description,
                fileLabel
            )
            .ToLowerInvariant();
    }

    /// <summary>The value as it would appear in the header.</summary>
    public string EditedValue =>
        IsBool
            ? (IsOn, _boolUsesWords) switch
            {
                (true, true) => "true",
                (false, true) => "false",
                (true, false) => "1",
                (false, false) => "0",
            }
            : Text.Trim();

    private void Reset()
    {
        if (IsBool)
        {
            IsOn = HeaderValue is "1" or "true";
        }
        else
        {
            Text = HeaderValue;
        }
    }

    partial void OnTextChanged(string value) => RefreshDirty();

    partial void OnIsOnChanged(bool value) => RefreshDirty();

    private void RefreshDirty()
    {
        IsInvalid = EditedValue.Length == 0;
        IsDirty = !IsInvalid && EditedValue != _savedValue;
        _edited();
    }

    /// <summary>The current edit has been persisted. A value equal to the header's is not an
    /// override at all — there is nothing to remember about wanting what you already have — so
    /// saving it is how a Reset takes effect.</summary>
    public void MarkSaved()
    {
        _savedValue = EditedValue;
        IsOverride = _savedValue != HeaderValue;
        RefreshDirty();
    }

    /// <summary>True when every search term appears in this setting's name, category,
    /// description or file name. An empty search matches everything.</summary>
    public bool Matches(IReadOnlyList<string> lowerCaseTerms) =>
        lowerCaseTerms.All(_searchHaystack.Contains);
}
