using CommunityToolkit.Mvvm.ComponentModel;
using Dyno.Core.Firmware;

namespace Dyno.App.ViewModels;

/// <summary>
/// One editable firmware <c>#define</c> on the SysConfig page. Wraps a parsed
/// <see cref="ConfigDefine"/> with edit state: the value being typed (or toggled), whether it
/// differs from what's on disk, and the text searched against.
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

    /// <summary>Free-form value for non-bool settings, as shown in the TextBox.</summary>
    [ObservableProperty]
    private string _text;

    /// <summary>Toggle state for bool settings.</summary>
    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private bool _isDirty;

    public ConfigParameterViewModel(ConfigDefine define, string fileLabel, Action edited)
    {
        _edited = edited;
        Name = define.Name;
        Category = define.Category;
        FileLabel = fileLabel;
        Description = define.Description;
        IsBool = define.Kind == ConfigValueKind.Bool;
        _boolUsesWords = define.Value is "true" or "false";
        _savedValue = define.Value;
        _text = define.Value;
        _isOn = define.Value is "1" or "true";
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

    /// <summary>The value as it would be written back to the header.</summary>
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

    partial void OnTextChanged(string value) => RefreshDirty();

    partial void OnIsOnChanged(bool value) => RefreshDirty();

    private void RefreshDirty()
    {
        IsDirty = EditedValue != _savedValue;
        _edited();
    }

    /// <summary>The current edit is now what's on disk.</summary>
    public void MarkSaved()
    {
        _savedValue = EditedValue;
        RefreshDirty();
    }

    /// <summary>True when every search term appears in this setting's name, category,
    /// description or file name. An empty search matches everything.</summary>
    public bool Matches(IReadOnlyList<string> lowerCaseTerms) =>
        lowerCaseTerms.All(_searchHaystack.Contains);
}
