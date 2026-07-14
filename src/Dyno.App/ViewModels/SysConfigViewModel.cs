using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dyno.App.Services;
using Dyno.Core;
using Dyno.Core.Firmware;
using Dyno.Core.Messages;
using Dyno.Core.SysConfig;

namespace Dyno.App.ViewModels;

/// <summary>
/// Drives the SysConfig page, which manages two kinds of firmware settings:
/// <list type="bullet">
/// <item><b>Runtime parameters</b> (gains, task delays, thresholds): edited here, persisted in a
/// SQLite database on this computer, and written to the running device over USB — immediately when
/// connected, and re-pushed automatically after every handshake, since the board itself has no
/// settings storage and reboots back to its config.h defaults.</item>
/// <item><b>Compile-time settings</b> (buffer sizes; debug.h's task/peripheral gates): these
/// physically cannot change at runtime — buffer sizes dimension static arrays on a heapless
/// firmware, and debug.h decides what code is compiled in at all — so they are edited in the
/// headers themselves and take effect on the next firmware build and flash.</item>
/// </list>
/// </summary>
public partial class SysConfigViewModel : ObservableObject
{
    /// <summary>Search text that should also reveal the sample-rate card, which is a routed
    /// device command rather than a store parameter or a parsed define.</summary>
    private const string SampleRateCardHaystack =
        "device runtime force sensor sample rate sps usb ads1115 live";

    private sealed record LoadedFile(string Label, string Path, FirmwareConfigFile File);

    /// <summary>config.h defines that are runtime-managed now: their #define is only the boot
    /// default, so the header editor hides them in favour of the runtime section above it.</summary>
    private static readonly HashSet<string> RuntimeManagedNames = SysConfigCatalog
        .Parameters.Select(p => p.Name)
        .ToHashSet();

    private readonly Func<DeviceClient?> _getClient;
    private readonly List<LoadedFile> _files = new();
    private readonly List<ConfigParameterViewModel> _parameters = new();
    private readonly List<RuntimeParameterViewModel> _runtimeParameters = new();
    private SysConfigStore? _store;

    /// <summary>Runtime category cards currently visible, in catalog order.</summary>
    public ObservableCollection<RuntimeCategoryViewModel> FilteredRuntimeCategories { get; } =
        new();

    /// <summary>Compile-time section cards currently visible, in file order.</summary>
    public ObservableCollection<ConfigCategoryViewModel> FilteredCategories { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Runtime section: where values were saved/applied, or why they couldn't be.</summary>
    [ObservableProperty]
    private string _runtimeStatusText = string.Empty;

    /// <summary>Compile-time section: where the headers came from, how a save went, or why
    /// loading failed.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _loadFailed;

    /// <summary>Whether the sample-rate card matches the current search.</summary>
    [ObservableProperty]
    private bool _showRuntimeCard = true;

    [ObservableProperty]
    private bool _hasNoMatches;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(DirtySummary))]
    private int _dirtyCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyRuntimeCommand))]
    [NotifyPropertyChangedFor(nameof(RuntimeDirtySummary))]
    private int _runtimeDirtyCount;

    public string DirtySummary =>
        DirtyCount == 1 ? "1 unsaved change" : $"{DirtyCount} unsaved changes";

    public string RuntimeDirtySummary =>
        RuntimeDirtyCount == 1 ? "1 pending change" : $"{RuntimeDirtyCount} pending changes";

    public SysConfigViewModel(Func<DeviceClient?> getClient, string? databasePath = null)
    {
        _getClient = getClient;
        OpenStoreAndBuildRuntimeParameters(databasePath);
        Load();
    }

    /// <summary>Parameterless constructor for the XAML design-time previewer.</summary>
    public SysConfigViewModel()
        : this(() => null) { }

    private void OpenStoreAndBuildRuntimeParameters(string? databasePath)
    {
        IReadOnlyDictionary<sysconfig_param_t, double> saved;
        try
        {
            _store = new SysConfigStore(databasePath);
            saved = _store.LoadAll();
            RuntimeStatusText = $"Values persist in {_store.DatabasePath} and re-apply on connect";
        }
        catch (Exception ex)
        {
            // No store: the page still works for this session, it just can't remember.
            _store = null;
            saved = new Dictionary<sysconfig_param_t, double>();
            RuntimeStatusText =
                $"Settings database unavailable ({ex.Message}) — edits won't persist";
        }

        foreach (var def in SysConfigCatalog.Parameters)
        {
            _runtimeParameters.Add(
                new RuntimeParameterViewModel(
                    def,
                    saved.TryGetValue(def.Id, out var value) ? value : null,
                    RecountRuntimeDirty,
                    ResetRuntimeParameterAsync
                )
            );
        }
    }

    private bool CanApplyRuntime => RuntimeDirtyCount > 0;

    /// <summary>Saves every edited runtime value to SQLite and, when a device is connected,
    /// writes it into the running firmware's store. Values the firmware would reject never
    /// leave the validation in <see cref="RuntimeParameterViewModel"/> (the button counts only
    /// valid edits), so a failure here is a link problem, not a value problem.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyRuntime))]
    private async Task ApplyRuntime()
    {
        var dirty = _runtimeParameters.Where(p => p.IsDirty && !p.IsInvalid).ToList();
        var applied = 0;
        var pushFailures = new List<string>();
        var client = ConnectedClient();

        foreach (var parameter in dirty)
        {
            if (parameter.EditedValue is not double value)
            {
                continue;
            }

            _store?.Save(parameter.Def.Id, parameter.Name, value);

            if (client is not null)
            {
                try
                {
                    await client.SetSysConfigParamAsync(
                        parameter.Def.Id,
                        parameter.Def.ToRawBits(value)
                    );
                }
                catch (Exception)
                {
                    pushFailures.Add(parameter.Name);
                    // Saved locally all the same; the next handshake re-push delivers it.
                }
            }

            parameter.MarkSaved(isOverride: true);
            applied++;
        }

        RuntimeStatusText = (client, pushFailures.Count) switch
        {
            (null, _) =>
                $"Saved {applied} setting{(applied == 1 ? "" : "s")} on this PC — will apply when a device connects",
            (_, 0) =>
                $"Applied {applied} setting{(applied == 1 ? "" : "s")} to the device and saved on this PC",
            _ =>
                $"Saved {applied}, but the device didn't ack: {string.Join(", ", pushFailures)} — they'll re-apply on the next handshake",
        };
    }

    /// <summary>Reverts one parameter to the firmware default: forgets the local override and,
    /// when connected, pushes the default so the running device matches what the page now shows.</summary>
    private async Task ResetRuntimeParameterAsync(RuntimeParameterViewModel parameter)
    {
        _store?.Remove(parameter.Def.Id);
        parameter.ResetToDefault();

        if (ConnectedClient() is { } client)
        {
            try
            {
                await client.SetSysConfigParamAsync(
                    parameter.Def.Id,
                    parameter.Def.ToRawBits(parameter.Def.Default)
                );
                RuntimeStatusText = $"{parameter.Name} reset to default on the device and this PC";
            }
            catch (Exception)
            {
                RuntimeStatusText =
                    $"{parameter.Name} reset on this PC; the device didn't ack and will pick it up on the next handshake or reboot";
            }
        }
        else
        {
            RuntimeStatusText =
                $"{parameter.Name} reset on this PC — the device returns to it on next connect or reboot";
        }
    }

    /// <summary>
    /// Pushes every locally saved override to a freshly handshaked device. Called (on a background
    /// thread) each time the link handshakes — including re-handshakes after a lost link — because
    /// the board holds settings only in RAM: without this re-push a rebooted board would silently
    /// run defaults while the page displays the saved values.
    /// </summary>
    public async Task PushSavedToDeviceAsync(DeviceClient client)
    {
        var overrides = _runtimeParameters.Where(p => p.IsOverride).ToList();
        if (overrides.Count == 0)
        {
            return;
        }

        var failures = 0;
        foreach (var parameter in overrides)
        {
            try
            {
                // The last *saved* value, not any in-progress edit in the text box.
                await client.SetSysConfigParamAsync(
                    parameter.Def.Id,
                    parameter.Def.ToRawBits(parameter.SavedValue)
                );
            }
            catch (Exception)
            {
                failures++;
            }
        }

        var summary =
            failures == 0
                ? $"Applied {overrides.Count} saved setting{(overrides.Count == 1 ? "" : "s")} to the device"
                : $"Applied {overrides.Count - failures}/{overrides.Count} saved settings to the device; the rest were not acked";
        Dispatcher.UIThread.Post(() => RuntimeStatusText = summary);
    }

    private DeviceClient? ConnectedClient() =>
        _getClient() is { IsHandshaked: true } client ? client : null;

    private void RecountRuntimeDirty() =>
        RuntimeDirtyCount = _runtimeParameters.Count(p => p.IsDirty && !p.IsInvalid);

    [RelayCommand]
    private void Reload() => Load();

    private void Load()
    {
        _files.Clear();
        _parameters.Clear();

        var dir = FirmwareConfigLocator.FindConfigDirectory();
        if (dir is null)
        {
            LoadFailed = true;
            StatusText =
                "firmware/Core/Inc/Config not found — run the app from inside the repo, or set "
                + $"{FirmwareConfigLocator.OverrideVariable}";
        }
        else
        {
            try
            {
                // debug.h's defines are all 0/1 enable switches, so they get toggles; config.h's
                // are quantities, where a literal 0 or 1 would be an ordinary number.
                LoadFile(dir, "config.h", binaryTogglesAreBool: false);
                LoadFile(dir, "debug.h", binaryTogglesAreBool: true);
                LoadFailed = false;
                StatusText = $"{_parameters.Count} compile-time settings — {dir}";
            }
            catch (Exception ex)
            {
                LoadFailed = true;
                StatusText = $"Failed to read config headers: {ex.Message}";
            }
        }

        RecountDirty();
        ApplyFilter();
    }

    private void LoadFile(string dir, string fileName, bool binaryTogglesAreBool)
    {
        var path = Path.Combine(dir, fileName);
        var parsed = FirmwareConfigFile.Parse(
            fileName,
            File.ReadAllText(path),
            binaryTogglesAreBool
        );
        _files.Add(new LoadedFile(fileName, path, parsed));
        foreach (var define in parsed.Defines)
        {
            // Runtime-managed values are edited in the section above; their #define is just the
            // boot default, and showing it twice would invite editing the wrong copy.
            if (fileName == "config.h" && RuntimeManagedNames.Contains(define.Name))
            {
                continue;
            }
            _parameters.Add(new ConfigParameterViewModel(define, fileName, RecountDirty));
        }
    }

    private bool CanSave => DirtyCount > 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var saved = 0;
        var rejected = new List<string>();
        foreach (var file in _files)
        {
            var applied = new List<ConfigParameterViewModel>();
            foreach (
                var parameter in _parameters.Where(p => p.IsDirty && p.FileLabel == file.Label)
            )
            {
                if (file.File.TrySetValue(parameter.Name, parameter.EditedValue))
                {
                    applied.Add(parameter);
                }
                else
                {
                    rejected.Add(parameter.Name);
                }
            }
            if (applied.Count == 0)
            {
                continue;
            }

            try
            {
                File.WriteAllText(file.Path, file.File.ToText());
            }
            catch (Exception ex)
            {
                // The edits stay dirty (nothing below runs), so a later save retries them.
                StatusText = $"Failed to write {file.Label}: {ex.Message}";
                return;
            }
            foreach (var parameter in applied)
            {
                parameter.MarkSaved();
            }
            saved += applied.Count;
        }

        StatusText =
            rejected.Count > 0
                ? $"Saved {saved}, but rejected empty/invalid value for: {string.Join(", ", rejected)}"
                : $"Saved {saved} setting{(saved == 1 ? "" : "s")} — rebuild and flash the firmware to apply";
    }

    private void RecountDirty() => DirtyCount = _parameters.Count(p => p.IsDirty);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var terms = SearchText
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        FilteredRuntimeCategories.Clear();
        foreach (var group in _runtimeParameters.GroupBy(p => p.Category))
        {
            var visible = group.Where(p => p.Matches(terms)).ToList();
            if (visible.Count > 0)
            {
                FilteredRuntimeCategories.Add(
                    new RuntimeCategoryViewModel { Name = group.Key, Parameters = visible }
                );
            }
        }

        FilteredCategories.Clear();
        foreach (var group in _parameters.GroupBy(p => (p.FileLabel, p.Category)))
        {
            var visible = group.Where(p => p.Matches(terms)).ToList();
            if (visible.Count > 0)
            {
                FilteredCategories.Add(
                    new ConfigCategoryViewModel
                    {
                        Name = group.Key.Category.Length > 0 ? group.Key.Category : "Other",
                        FileLabel = group.Key.FileLabel,
                        Parameters = visible,
                    }
                );
            }
        }

        ShowRuntimeCard = terms.Length == 0 || terms.All(SampleRateCardHaystack.Contains);
        HasNoMatches =
            FilteredRuntimeCategories.Count == 0
            && FilteredCategories.Count == 0
            && !ShowRuntimeCard
            && !LoadFailed;
    }
}
