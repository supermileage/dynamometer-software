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
/// <item><b>Runtime parameters</b> (gains, task delays, thresholds): edited here and persisted in a
/// SQLite database on this computer, which is the only place they are kept — the board's store is
/// RAM behind no flash, so it forgets them on every reboot. Saving and applying are therefore two
/// separate jobs: <see cref="ApplyRuntime"/> writes the values to the database, and a reconciliation
/// pass (<see cref="SyncDeviceAsync"/>) brings whatever device is connected into line with it. That
/// pass sends only what the board is not already known to hold, which makes it both the cheap path
/// for a single edit and the correct path on connect, where nothing is known and every parameter
/// goes out.</item>
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

    /// <summary>What the connected board is believed to hold — the difference between this and the
    /// saved values is exactly the set of writes still owed to it.</summary>
    private readonly SysConfigDeviceMirror _mirror = new();

    /// <summary>One reconciliation pass at a time. An edit applied while the connect-time restore is
    /// still running would otherwise race it, and the two could write the same parameter in either
    /// order — leaving the board holding the older value.</summary>
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    private SysConfigStore? _store;

    /// <summary>Raised with a line for the main window's event log. The connect-time restore writes
    /// every parameter at once, which is summarised here rather than narrated write by write (see
    /// <see cref="DeviceClient.SetSysConfigParamAsync"/>'s <c>announce</c>).</summary>
    public event Action<string>? DeviceSyncLogged;

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

    /// <summary>Saves every edited runtime value to SQLite. That is all it does to the settings
    /// themselves — the database is where they live, and the device is a copy of it, brought up to
    /// date by the sync that follows. Values the firmware would reject never leave the validation in
    /// <see cref="RuntimeParameterViewModel"/> (the button counts only valid edits), so a failure to
    /// reach the device is a link problem, not a value problem.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyRuntime))]
    private async Task ApplyRuntime()
    {
        var saved = 0;
        foreach (var parameter in _runtimeParameters.Where(p => p.IsDirty && !p.IsInvalid).ToList())
        {
            if (parameter.EditedValue is not double value)
            {
                continue;
            }

            _store?.Save(parameter.Def.Id, parameter.Name, value);
            parameter.MarkSaved(isOverride: true);
            saved++;
        }

        RuntimeStatusText = $"Saved {Count(saved, "setting")} on this PC";
        await SyncDeviceAsync(ConnectedClient(), announce: true);
    }

    /// <summary>Reverts one parameter to the firmware default: forgets the local override, so the
    /// value the sync below wants on the device is the default again.</summary>
    private async Task ResetRuntimeParameterAsync(RuntimeParameterViewModel parameter)
    {
        _store?.Remove(parameter.Def.Id);
        parameter.ResetToDefault();

        RuntimeStatusText = $"{parameter.Name} reset to its firmware default on this PC";
        await SyncDeviceAsync(ConnectedClient(), announce: true);
    }

    /// <summary>
    /// Re-applies the settings to a freshly handshaked device. Called (on a background thread) on
    /// every handshake, including the re-handshake that follows a recovered link — because a link
    /// that dropped may well have dropped *because* the board reset, and a board that reset is back
    /// on its config.h defaults with no way to say so. Nothing is assumed about what it holds, so
    /// this writes the whole catalog: not only the overrides, but the defaults too, since a board
    /// that stayed powered through a host restart is still holding the previous session's values.
    /// </summary>
    public Task ResyncDeviceAsync(DeviceClient client) =>
        SyncDeviceAsync(client, announce: false, forget: true);

    /// <summary>
    /// Brings the device in line with what this PC has saved, writing only the parameters it is not
    /// already known to hold — after a save that is the one value that changed; with
    /// <paramref name="forget"/>, it is all of them. A write that isn't acked stays unconfirmed and
    /// so is simply included in the next pass.
    /// </summary>
    private async Task SyncDeviceAsync(DeviceClient? client, bool announce, bool forget = false)
    {
        if (client is null)
        {
            Report("Saved on this PC — applied to the device when one connects");
            return;
        }

        var written = 0;
        var failures = new List<string>();

        await _syncGate.WaitAsync();
        try
        {
            // Inside the gate, so the mirror is only ever touched by one pass: forgetting from
            // outside it would let a restore already in flight confirm its writes into a mirror that
            // a second handshake had just cleared, and the board would be credited with values that
            // the reboot behind that handshake had already thrown away.
            if (forget)
            {
                _mirror.Forget();
            }

            foreach (var (def, value) in _mirror.Outstanding(SavedValues()))
            {
                try
                {
                    await client.SetSysConfigParamAsync(
                        def.Id,
                        def.ToRawBits(value),
                        announce: announce
                    );
                    _mirror.Confirm(def.Id, value);
                    written++;
                }
                catch (Exception)
                {
                    // Left unconfirmed on purpose: the next sync — or the next connect — sends it
                    // again, which is the only recovery a board with no storage of its own has.
                    failures.Add(def.Name);
                }
            }
        }
        finally
        {
            _syncGate.Release();
        }

        Report(
            (written, failures.Count) switch
            {
                (0, 0) => "The device already holds every saved value",
                (_, 0) => $"Applied {Count(written, "setting")} to the device",
                _ =>
                    $"Applied {written} to the device; it didn't ack {string.Join(", ", failures)} — retried on the next connect",
            }
        );

        // A restore writes all 27 parameters, so it reports itself as one line rather than as 27
        // sends and 27 acks; an ordinary edit is already narrated write-by-write by the client.
        if (!announce && (written > 0 || failures.Count > 0))
        {
            DeviceSyncLogged?.Invoke(
                failures.Count == 0
                    ? $"[CFG ] restored {Count(written, "sysconfig parameter")} to the device (its store is RAM only)"
                    : $"[ERR ] restored {written} sysconfig parameters, but the device didn't ack "
                        + $"{string.Join(", ", failures)}"
            );
        }
    }

    /// <summary>The value each parameter should have, keyed by wire id: the saved override where one
    /// exists and the firmware default otherwise — never an edit still being typed.</summary>
    private Dictionary<sysconfig_param_t, double> SavedValues() =>
        _runtimeParameters.ToDictionary(p => p.Def.Id, p => p.SavedValue);

    /// <summary>Status lines can come off the handshake thread, so they are marshalled.</summary>
    private void Report(string status) =>
        Dispatcher.UIThread.Post(() => RuntimeStatusText = status);

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

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
