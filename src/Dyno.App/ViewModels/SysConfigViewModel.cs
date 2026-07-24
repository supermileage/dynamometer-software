using System.Collections.ObjectModel;
using System.Globalization;
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
/// Drives the SysConfig page. Everything on it is saved the same way — <see cref="Apply"/> writes it
/// to a SQLite database on this computer — but the two halves differ in what happens next:
/// <list type="bullet">
/// <item><b>Runtime parameters</b> (gains, task delays, thresholds): the database is the only place
/// they are kept, since the board's store is RAM behind no flash and it forgets them on every
/// reboot. So saving and applying are separate jobs: Apply writes the values, and a reconciliation
/// pass (<see cref="SyncDeviceAsync"/>) brings whatever device is connected into line with them.
/// That pass sends only what the board is not already known to hold, which makes it both the cheap
/// path for a single edit and the correct path on connect, where nothing is known and every
/// parameter goes out.</item>
/// <item><b>Compile-time settings</b> (buffer sizes; debug.h's task/peripheral gates): these
/// physically cannot change at runtime — buffer sizes dimension static arrays on a heapless
/// firmware, and debug.h decides what code is compiled in at all. Saving one records what the user
/// wants and <b>nothing else</b>: the headers are read but never written, and no build reads the
/// database, so the firmware keeps whatever it was compiled with until someone edits config.h /
/// debug.h and flashes it.</item>
/// </list>
/// </summary>
public partial class SysConfigViewModel : ObservableObject
{
    /// <summary>config.h defines that are runtime-managed now: their #define is only the boot
    /// default, so the compile-time section hides them in favour of the runtime section above
    /// it.</summary>
    private static readonly HashSet<string> RuntimeManagedNames = SysConfigCatalog
        .Parameters.Select(p => p.Name)
        .ToHashSet();

    private readonly Func<DeviceClient?> _getClient;
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

    /// <summary>The constants this PC derives torque, power and gearing from. They sit on this page
    /// beside the device's parameters because that is where a user looks for "the dyno's numbers",
    /// but they are host-only: nothing in the firmware reads them, and nothing is pushed.</summary>
    public ObservableCollection<PcConstantViewModel> PcConstants { get; } = new();

    /// <summary>Raised after Apply commits a PC constant, so the derivation picks up the new value
    /// without waiting for a reconnect.</summary>
    public event Action? PcConstantsChanged;

    public const string MomentOfInertiaName = "MOMENT_OF_INERTIA_KG_M2";
    public const string ForceLeverArmName = "DISTANCE_FROM_FORCE_SENSOR_TO_CENTER_OF_SHAFT_M";
    public const string GearRatioName = "GEAR_RATIO";

    /// <summary>The value in force for a PC constant, or its default when unknown.</summary>
    public double PcConstant(string name) =>
        PcConstants.FirstOrDefault(c => c.Name == name)?.Value ?? 1.0;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Runtime section: where values were saved/applied, or why they couldn't be.</summary>
    [ObservableProperty]
    private string _runtimeStatusText = string.Empty;

    /// <summary>Compile-time section: how many settings were found, how a save went, or why
    /// loading failed.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>PC-constants section: what the last Apply did with them, or why it could not. Empty
    /// the rest of the time, and hidden while it is — the section needs no standing caption. Its own
    /// line rather than sharing the runtime one, because the two sections answer different questions
    /// — nothing here is ever sent to a device, so "applied to the device" would be the wrong
    /// reassurance and its absence would read as a failure.</summary>
    [ObservableProperty]
    private string _pcStatusText = string.Empty;

    [ObservableProperty]
    private bool _loadFailed;

    [ObservableProperty]
    private bool _hasNoMatches;

    /// <summary>Edits staged across both halves of the page — Apply's whole job, and the only thing
    /// that enables it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyPropertyChangedFor(nameof(PendingSummary))]
    private int _pendingCount;

    public string PendingSummary =>
        PendingCount == 1 ? "1 pending change" : $"{PendingCount} pending changes";

    /// <summary>The effective value of a named compile-time <c>#define</c>: the override saved on
    /// this PC if there is one, else what the header declares. Null when the headers could not be
    /// loaded or no such define exists.</summary>
    public string? CompileTimeValue(string name) =>
        _parameters.FirstOrDefault(p => p.Name == name)?.SavedValue;

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
            RuntimeStatusText = "Values persist on this PC and re-apply on connect";
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
                    Recount
                )
            );
        }

        BuildPcConstants();
    }

    private void BuildPcConstants()
    {
        IReadOnlyDictionary<string, double> saved;
        try
        {
            saved = _store?.LoadAllPcConstants() ?? new Dictionary<string, double>();
        }
        catch
        {
            saved = new Dictionary<string, double>();
        }

        // Only the failure is worth a line. Where the values are kept is this app's business, not
        // something the user has to know to use the page, and it pushed the constants themselves
        // further down the page to say it.
        PcStatusText = _store is null
            ? "Settings database unavailable — edits won't outlast this session"
            : string.Empty;

        void Add(
            string name,
            string label,
            string unit,
            string description,
            double min,
            double max,
            double fallback
        ) =>
            PcConstants.Add(
                new PcConstantViewModel(
                    name,
                    label,
                    unit,
                    description,
                    min,
                    max,
                    fallback,
                    saved.TryGetValue(name, out var value) ? value : null,
                    Recount
                )
            );

        Add(
            ForceLeverArmName,
            "Force sensor lever arm",
            "m",
            "Distance from the force sensor to the shaft centre. A longer arm means more torque for the same measured force.",
            1.0e-6,
            1000.0,
            0.1
        );
        Add(
            MomentOfInertiaName,
            "Moment of inertia",
            "kg·m²",
            "Rotating assembly's moment of inertia. Leave at 0 until it has been measured: the torque then counts only the force at the arm, ignoring what it takes to spin the rotor up.",
            0.0,
            1.0e6,
            0.0
        );
        Add(
            GearRatioName,
            "Gear ratio",
            "",
            "Sensed shaft to output ratio; 1.0 is direct drive. The geared readouts trade one for the other: torque is multiplied by this, speed divided by it.",
            1.0e-6,
            1000.0,
            1.0
        );
    }

    private int SavePcConstants()
    {
        var saved = 0;
        foreach (var constant in PcConstants.Where(c => c.IsDirty).ToList())
        {
            if (constant.ParsedValue is not double value)
            {
                continue;
            }
            _store?.SavePcConstant(constant.Name, value);
            constant.MarkSaved();
            saved++;
        }
        if (saved > 0)
        {
            PcStatusText =
                $"Saved {Wording.Count(saved, "constant")} on this PC — the readouts and plots "
                + "derive from the new value starting with the next sample";
            PcConstantsChanged?.Invoke();
        }
        return saved;
    }

    // ---- Import / export of the whole page as JSON ----------------------------------------------

    /// <summary>Everything this page holds, as it stands now — the values in force, not the ones
    /// staged in the boxes. Exporting what you have applied rather than what you have typed is what
    /// makes the file a description of a configuration rather than of a half-finished edit.
    /// </summary>
    public ConfigBundle CurrentConfiguration() =>
        new(
            _runtimeParameters.ToDictionary(p => p.Name, p => p.SavedValue, StringComparer.Ordinal),
            PcConstants.ToDictionary(c => c.Name, c => c.Value, StringComparer.Ordinal),
            _parameters.ToDictionary(p => p.Name, p => p.SavedValue, StringComparer.Ordinal)
        );

    /// <summary>
    /// Loads a bundle into the page. Every setting the page knows is given a value: the file's, if
    /// it has a usable one, otherwise the setting's own default. Nothing is saved or sent — the
    /// values are staged exactly as if typed, so the pending count says how much the file changed
    /// and Apply is still the only thing that commits it.
    /// </summary>
    /// <returns>Lines for the event log: what the file did not supply, and what in it could not be
    /// used. An import that covers everything cleanly returns nothing.</returns>
    /// <remarks>Staging rather than applying is deliberate. An import can come from a file someone
    /// else wrote, and a runtime value goes to a connected board the moment it is applied — so the
    /// user gets to look at what arrived, on a page that marks every changed row, before any of it
    /// reaches hardware.</remarks>
    public IReadOnlyList<string> ApplyImported(ConfigBundle bundle)
    {
        var report = new List<string>();
        var missingRuntime = new List<string>();
        var missingPc = new List<string>();
        var missingCompileTime = new List<string>();

        foreach (var parameter in _runtimeParameters)
        {
            if (!bundle.Runtime.TryGetValue(parameter.Name, out var value))
            {
                missingRuntime.Add(parameter.Name);
                StageRuntimeDefault(parameter);
            }
            else if (!parameter.Def.IsValid(value))
            {
                // Out of the firmware's range, so it would be refused with MALFORMED. Treated like
                // an absent value rather than staged as an invalid one, which would leave the page
                // un-appliable until the user found and fixed the row by hand.
                report.Add(
                    $"[WARN] config import: {parameter.Name} = {parameter.Def.Format(value)} is "
                        + $"outside {parameter.Def.Format(parameter.Def.Min)} to "
                        + $"{parameter.Def.Format(parameter.Def.Max)}"
                );
                StageRuntimeDefault(parameter);
            }
            else
            {
                parameter.Text = parameter.Def.Format(value);
            }
        }

        foreach (var constant in PcConstants)
        {
            if (bundle.PcConstants.TryGetValue(constant.Name, out var value))
            {
                constant.EditedText = value.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                missingPc.Add(constant.Name);
                constant.ResetCommand.Execute(null);
            }
        }

        foreach (var parameter in _parameters)
        {
            if (bundle.CompileTime.TryGetValue(parameter.Name, out var value))
            {
                if (parameter.IsBool)
                {
                    parameter.IsOn = value is "1" or "true" or "True";
                }
                else
                {
                    parameter.Text = value;
                }
            }
            else
            {
                missingCompileTime.Add(parameter.Name);
                parameter.ResetCommand.Execute(null);
            }
        }

        // Names in the file that this build has nothing to put them in — a typo, or a file from a
        // firmware with settings this one does not have. Worth saying: silently dropping them is
        // how someone concludes the import worked and the value did not take.
        report.AddRange(
            Unknown(
                bundle.Runtime.Keys,
                _runtimeParameters.Select(p => p.Name),
                "runtime parameter"
            )
        );
        report.AddRange(
            Unknown(bundle.PcConstants.Keys, PcConstants.Select(c => c.Name), "PC constant")
        );
        report.AddRange(
            Unknown(
                bundle.CompileTime.Keys,
                _parameters.Select(p => p.Name),
                "compile-time setting"
            )
        );

        report.AddRange(Missing(missingRuntime, "runtime parameter"));
        report.AddRange(Missing(missingPc, "PC constant"));
        report.AddRange(Missing(missingCompileTime, "compile-time setting"));

        Recount();
        ApplyFilter();
        return report;
    }

    /// <summary>Puts a runtime parameter back on the firmware default. A staged reset is the way to
    /// say "no override" — but only when there is one to drop, since requesting a reset on a
    /// parameter already running the default would mark the row changed with nothing to change.
    /// </summary>
    private static void StageRuntimeDefault(RuntimeParameterViewModel parameter)
    {
        if (parameter.IsOverride)
        {
            parameter.ResetCommand.Execute(null);
        }
    }

    /// <summary>One line naming what the file left out, rather than one line each. A partial file is
    /// the normal case — a few parameters pasted from somewhere — and a line per untouched setting
    /// would be eighty of them, burying both the real warnings here and whatever the log already
    /// held. Long lists are cut short, because a line nobody can read to the end is no better.
    /// </summary>
    private static IEnumerable<string> Missing(IReadOnlyList<string> names, string kind) =>
        names.Count == 0
            ? []
            :
            [
                $"[WARN] config import: {Wording.Count(names.Count, kind)} not in the file: "
                    + NameList(names),
            ];

    private static IEnumerable<string> Unknown(
        IEnumerable<string> fileNames,
        IEnumerable<string> known,
        string kind
    )
    {
        var unknown = fileNames.Except(known, StringComparer.Ordinal).ToList();
        return unknown.Count == 0
            ? []
            :
            [
                $"[WARN] config import: {Wording.Count(unknown.Count, kind)} not in this build: "
                    + NameList(unknown),
            ];
    }

    /// <summary>How many names a warning spells out before it stops counting them.</summary>
    private const int NamesListed = 12;

    private static string NameList(IReadOnlyList<string> names) =>
        names.Count <= NamesListed
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(NamesListed))
                + $", and {names.Count - NamesListed} more";

    private bool CanApply => PendingCount > 0;

    /// <summary>The page's one commit point: every staged edit, of either kind, is written to SQLite
    /// here and nowhere else. What follows differs by kind — the runtime values are then pushed to
    /// whatever device is connected, while the compile-time ones just sit in the database — but a
    /// user pressing this has done the same thing either way: told this computer what they want.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply()
    {
        var savedRuntime = SaveRuntime();
        SaveCompileTime();
        SavePcConstants();

        if (savedRuntime > 0)
        {
            RuntimeStatusText = $"Saved {Wording.Count(savedRuntime, "change")} on this PC";
            // Announced before the push rather than after it: the sync can take a moment or fail
            // outright, and a mirror of a saved value should track what was saved.
            RuntimeSettingsChanged?.Invoke();
            await SyncDeviceAsync(ConnectedClient(), announce: true);
        }
    }

    /// <summary>Persists the edited runtime values. Values the firmware would reject never get this
    /// far — the validation in <see cref="RuntimeParameterViewModel"/> keeps them out of the pending
    /// count, and so out of Apply — which is why a failure to reach the device below is a link
    /// problem rather than a value problem.</summary>
    private int SaveRuntime()
    {
        var saved = 0;
        foreach (var parameter in _runtimeParameters.Where(p => p.IsDirty).ToList())
        {
            // A staged reset is a change like any other, and this is where it lands: forgetting the
            // saved override rather than writing a value over it. Both leave the parameter wanting a
            // different value than the device is known to hold, so both are picked up by the same
            // sync — a reset simply wants the firmware default.
            if (parameter.ResetRequested)
            {
                _store?.Remove(parameter.Def.Id);
                parameter.MarkReset();
                saved++;
                continue;
            }

            if (parameter.EditedValue is not double value)
            {
                continue;
            }

            _store?.Save(parameter.Def.Id, parameter.Name, value);
            parameter.MarkSaved(isOverride: true);
            saved++;
        }
        return saved;
    }

    /// <summary>Persists the edited <c>#define</c>s, and stops there. Nothing reads them back except
    /// this page: the headers are the firmware's, and the app only ever reads them. A value equal to
    /// the header's is stored as no row at all — wanting what you already have is not worth
    /// remembering, and it is how the row a Reset undid actually disappears.</summary>
    private void SaveCompileTime()
    {
        var saved = 0;
        foreach (var parameter in _parameters.Where(p => p.IsDirty).ToList())
        {
            if (parameter.EditedValue == parameter.HeaderValue)
            {
                _store?.RemoveCompileTime(parameter.Name);
            }
            else
            {
                _store?.SaveCompileTime(parameter.Name, parameter.FileLabel, parameter.EditedValue);
            }
            parameter.MarkSaved();
            saved++;
        }

        if (saved > 0)
        {
            StatusText =
                $"Saved {Wording.Count(saved, "compile-time setting")} on this PC — nothing on the "
                + "board has changed until it is rebuilt and flashed";
        }
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
                (_, 0) => $"Applied {Wording.Count(written, "setting")} to the device",
                _ =>
                    $"Applied {written} to the device; it didn't ack {string.Join(", ", failures)} — retried on the next connect",
            }
        );

        // A restore writes the whole catalog, so it reports itself as one line rather than as a
        // send and an ack per parameter; an ordinary edit is already narrated write-by-write by
        // the client.
        if (!announce && (written > 0 || failures.Count > 0))
        {
            DeviceSyncLogged?.Invoke(
                failures.Count == 0
                    ? $"[CFG ] restored {Wording.Count(written, "sysconfig parameter")} to the device (its store is RAM only)"
                    : $"[ERR ] restored {written} sysconfig parameters, but the device didn't ack "
                        + $"{string.Join(", ", failures)}"
            );
        }
    }

    /// <summary>The value each parameter should have, keyed by wire id: the saved override where one
    /// exists and the firmware default otherwise — never an edit still being typed.</summary>
    private Dictionary<sysconfig_param_t, double> SavedValues() =>
        _runtimeParameters.ToDictionary(p => p.Def.Id, p => p.SavedValue);

    /// <summary>
    /// The value this PC holds for one runtime parameter — which is also the value any connected
    /// device is running, since it is pushed after every handshake and re-pushed whenever it
    /// changes. Falls back to the firmware default for a parameter this build does not know.
    /// </summary>
    public double RuntimeValue(sysconfig_param_t id) =>
        _runtimeParameters.FirstOrDefault(p => p.Def.Id == id)?.SavedValue
        ?? SysConfigCatalog.Get(id).Default;

    /// <summary>Raised after Apply has persisted new runtime values, for anything that mirrors one
    /// outside this page. Fires once per Apply, not once per parameter.</summary>
    public event Action? RuntimeSettingsChanged;

    /// <summary>Status lines can come off the handshake thread, so they are marshalled.</summary>
    private void Report(string status) =>
        Dispatcher.UIThread.Post(() => RuntimeStatusText = status);

    /// <summary>The saved compile-time settings that differ from their header — exactly what the
    /// Firmware page bakes into the next build, and the only thing that connects these two pages.
    /// Reads the applied values, never the half-typed ones: an edit nobody pressed Apply on has not
    /// been chosen yet, and silently compiling it in would make Apply meaningless.</summary>
    public IReadOnlyList<ConfigOverride> CompileTimeOverrides() =>
        _parameters
            .Where(p => p.IsOverride)
            .Select(p => new ConfigOverride(p.Name, p.FileLabel, p.HeaderValue, p.SavedValue))
            .ToList();

    private DeviceClient? ConnectedClient() =>
        _getClient() is { IsHandshaked: true } client ? client : null;

    private void Recount() =>
        PendingCount =
            _runtimeParameters.Count(p => p.IsDirty)
            + _parameters.Count(p => p.IsDirty)
            + PcConstants.Count(c => c.IsDirty);

    /// <summary>
    /// Re-reads the firmware's headers if it can be done without cost to the user, so the page
    /// cannot go on showing what a file said at startup. Called when the page is opened.
    /// </summary>
    /// <remarks>
    /// This replaced a Reload button, which was easy to read as the opposite of Apply and is not:
    /// Apply saves what you typed, this re-reads what the firmware source says. The reason it has
    /// to happen at all is that the headers are read once, at startup, and nothing in the app
    /// writes them — so a pull, a branch switch, or an edit in another editor leaves the page
    /// describing a firmware that is no longer there.
    ///
    /// Skipped outright while any compile-time edit is staged, because re-reading rebuilds the rows
    /// and would throw that edit away. Losing typed work to a navigation would be a far worse
    /// surprise than a value that is briefly stale, and the next visit after an Apply picks it up.
    /// </remarks>
    public void RefreshFromDisk()
    {
        if (_parameters.Any(p => p.IsDirty))
        {
            return;
        }
        Load();
    }

    private void Load()
    {
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
                var saved = _store?.LoadAllCompileTime() ?? new Dictionary<string, string>();
                // debug.h's defines are all 0/1 enable switches, so they get toggles; config.h's
                // are quantities, where a literal 0 or 1 would be an ordinary number.
                LoadFile(dir, "config.h", saved, binaryTogglesAreBool: false);
                LoadFile(dir, "debug.h", saved, binaryTogglesAreBool: true);
                LoadFailed = false;
                StatusText = $"{_parameters.Count} compile-time settings";
            }
            catch (Exception ex)
            {
                LoadFailed = true;
                StatusText = $"Failed to read config headers: {ex.Message}";
            }
        }

        Recount();
        ApplyFilter();
    }

    private void LoadFile(
        string dir,
        string fileName,
        IReadOnlyDictionary<string, string> saved,
        bool binaryTogglesAreBool
    )
    {
        var parsed = FirmwareConfigFile.Parse(
            fileName,
            File.ReadAllText(Path.Combine(dir, fileName)),
            binaryTogglesAreBool
        );
        foreach (var define in parsed.Defines)
        {
            // Runtime-managed values are edited in the section above; their #define is just the
            // boot default, and showing it twice would invite editing the wrong copy.
            if (fileName == "config.h" && RuntimeManagedNames.Contains(define.Name))
            {
                continue;
            }
            _parameters.Add(
                new ConfigParameterViewModel(
                    define,
                    fileName,
                    saved.TryGetValue(define.Name, out var value) ? value : null,
                    Recount
                )
            );
        }
    }

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
            if (visible.Count == 0)
            {
                continue;
            }

            // Within a category, split by subsection (the force sensor's All/I2C/ADC). GroupBy keeps
            // catalog order, so the "All" group — first in the schema — leads. A category whose
            // parameters carry no subsection collapses to one untitled group and renders unchanged.
            var subsections = visible
                .GroupBy(p => p.Subsection)
                .Select(sub => new RuntimeSubsectionViewModel
                {
                    Title = sub.Key,
                    Parameters = sub.ToList(),
                })
                .ToList();

            FilteredRuntimeCategories.Add(
                new RuntimeCategoryViewModel { Name = group.Key, Subsections = subsections }
            );
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
                        Parameters = visible,
                    }
                );
            }
        }

        HasNoMatches =
            FilteredRuntimeCategories.Count == 0 && FilteredCategories.Count == 0 && !LoadFailed;
    }
}
