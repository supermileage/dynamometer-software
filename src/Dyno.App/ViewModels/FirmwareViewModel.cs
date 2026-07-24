using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dyno.App.Services;
using Dyno.Core.Firmware;

namespace Dyno.App.ViewModels;

/// <summary>
/// Drives the Firmware page: build the firmware, then put it on the board.
///
/// Neither job is reimplemented here. <c>firmware/Scripts/</c> already encodes how this board is
/// built and programmed — the tool matrix, the ROM-bootloader rules, which build tree is newer — and
/// it is what CI and the terminal use. The page runs those scripts and shows their output verbatim,
/// so the app can never disagree with the command line about what a flash does. What the page adds
/// is the part a script can't: knowing which tools go with which method, what the board needs doing
/// to it first, and what is plugged in.
///
/// The one thing it does before building is write the compile-time settings saved on the SysConfig
/// page into the generated override headers (<see cref="ConfigOverrides"/>) — which is what makes
/// those settings mean anything at all.
/// </summary>
public partial class FirmwareViewModel : ObservableObject
{
    private const string ElfName = "stm32_dyno_firmware_v2.elf";

    private readonly Func<IReadOnlyList<ConfigOverride>> _savedOverrides;
    private readonly Func<int> _unappliedEdits;
    private readonly IDeviceLinkGate? _link;
    private readonly string? _firmwareDirectory;
    private readonly string? _configDirectory;

    private CancellationTokenSource? _running;

    /// <summary>False when the app can't see the firmware tree (running from a published build
    /// outside the repo). The page then explains itself instead of offering dead buttons.</summary>
    public bool IsAvailable => _firmwareDirectory is not null;

    public bool IsUnavailable => !IsAvailable;

    public string UnavailableText =>
        "The firmware tree isn't next to this app, so it can't be built or flashed from here. Run "
        + $"the app from inside the repo, or set {FirmwareConfigLocator.OverrideVariable} to point "
        + "at firmware/Core/Inc/Config.";

    public IReadOnlyList<FirmwareBuild> Builds { get; } =
    [FirmwareBuild.Release, FirmwareBuild.Debug];

    public IReadOnlyList<FlashMethodChoice> Methods { get; } = FlashMethodChoice.All;

    /// <summary>Everything the two scripts print, in arrival order. Capped, because a Docker image
    /// rebuild prints thousands of lines and nobody scrolls back through them. This is shown in the
    /// window's log panel (the Console tab), not on this page.</summary>
    public ObservableCollection<string> Output { get; } = new();

    /// <summary>Raised when a build, flash or scan begins. The window uses it to reveal the Console
    /// tab, so output the user just triggered is never off-screen just because they were on another
    /// tab or had the panel collapsed.</summary>
    public event Action? OutputStarted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BuildStatusText))]
    private FirmwareBuild _selectedBuild = FirmwareBuild.Release;

    /// <summary>Only needed after the Dockerfile itself changes, so it is off by default: it turns a
    /// 30-second build into a several-minute one.</summary>
    [ObservableProperty]
    private bool _rebuildImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tools))]
    [NotifyPropertyChangedFor(nameof(NeedsBootloader))]
    [NotifyPropertyChangedFor(nameof(ShowSerial))]
    [NotifyPropertyChangedFor(nameof(ShowIndex))]
    [NotifyPropertyChangedFor(nameof(ShowPort))]
    private FlashMethodChoice _selectedMethod;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolHint))]
    [NotifyPropertyChangedFor(nameof(ShowIndex))]
    private string _selectedTool = string.Empty;

    /// <summary>Devices a Scan found, for the user to click instead of typing a serial or a port.
    /// The raw scan output still goes to the Console tab; this is the convenient view of it.</summary>
    public ObservableCollection<FlashTarget> Devices { get; } = new();

    /// <summary>The device the user clicked. Setting it fills whichever field it targets (serial,
    /// index or port), so a pick and a hand-typed value end up in the same place.</summary>
    [ObservableProperty]
    private FlashTarget? _selectedDevice;

    /// <summary>A line under the Scan button: how many devices were found, or why none were.</summary>
    [ObservableProperty]
    private string _deviceStatus = "Scan to list the boards you can flash to.";

    /// <summary>Which ST-Link probe / DFU device, when more than one is attached.</summary>
    [ObservableProperty]
    private string _serial = string.Empty;

    /// <summary>STM32CubeProgrammer's DFU device index (<c>port=USB&lt;n&gt;</c>). No other tool has
    /// the notion, which is why the box only appears for that one.</summary>
    [ObservableProperty]
    private string _index = "1";

    [ObservableProperty]
    private string _port = OperatingSystem.IsWindows() ? "COM3" : "/dev/ttyUSB0";

    [ObservableProperty]
    private string _baud = "115200";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(FlashCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    /// <summary>What is running, so the spinner says "Building…" rather than just spinning.</summary>
    [ObservableProperty]
    private string _busyText = string.Empty;

    /// <summary>The compile-time settings this build will bake in, one line each.</summary>
    public ObservableCollection<string> Overrides { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverrides))]
    private string _overridesSummary = string.Empty;

    public bool HasOverrides => Overrides.Count > 0;

    /// <summary>Set when the SysConfig page holds edits nobody pressed Apply on. They will *not* be
    /// built in, and saying so here is cheaper than letting someone flash a board and wonder why
    /// their change did nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedEdits))]
    private string _unappliedEditsText = string.Empty;

    public bool HasUnappliedEdits => UnappliedEditsText.Length > 0;

    public string ToolHint =>
        SelectedTool switch
        {
            "st-flash" => "Open-source (stlink). dnf install stlink · apt install stlink-tools",
            "openocd" => "Open-source. dnf install openocd · apt install openocd",
            "dfu-util" => "Open-source. dnf install dfu-util · apt install dfu-util",
            "stm32flash" => "Open-source. dnf install stm32flash · apt install stm32flash",
            "cubeprog" =>
                "STM32CubeProgrammer (STM32_Programmer_CLI) — the only tool here that needs a free "
                    + "ST account to download.",
            _ => string.Empty,
        };

    public IReadOnlyList<string> Tools => FirmwareCommands.ToolsFor(SelectedMethod.Method);

    public bool NeedsBootloader => SelectedMethod.NeedsBootloader;

    public bool ShowSerial => SelectedMethod.Method is FlashMethod.Swd or FlashMethod.Dfu;

    public bool ShowIndex => SelectedMethod.Method is FlashMethod.Dfu && SelectedTool == "cubeprog";

    public bool ShowPort => SelectedMethod.Method is FlashMethod.Uart;

    /// <summary>Whether a flashable image exists, and how old it is — the question you actually have
    /// before flashing, and one the scripts can only answer by failing.</summary>
    public string BuildStatusText => DescribeBuild();

    /// <summary>Linux denies non-root USB access by default, and the resulting "libusb open failed"
    /// sends people to sudo. Named up front instead.</summary>
    public bool ShowLinuxPermissionsNote => OperatingSystem.IsLinux();

    public FirmwareViewModel(
        Func<IReadOnlyList<ConfigOverride>> savedOverrides,
        Func<int> unappliedEdits,
        IDeviceLinkGate? link = null
    )
    {
        _savedOverrides = savedOverrides;
        _unappliedEdits = unappliedEdits;
        _link = link;
        _firmwareDirectory = FirmwareConfigLocator.FindFirmwareDirectory();
        _configDirectory = FirmwareConfigLocator.FindConfigDirectory();

        _selectedMethod = Methods[0];
        _selectedTool = Tools[0];
        Refresh();
    }

    /// <summary>Design-time constructor for the XAML previewer.</summary>
    public FirmwareViewModel()
        : this(() => [], () => 0) { }

    /// <summary>Re-reads what a build would do, for when the page is opened after editing settings
    /// elsewhere. Cheap, so it runs on every navigation rather than trying to be clever about it.</summary>
    public void Refresh()
    {
        Overrides.Clear();
        foreach (var value in _savedOverrides())
        {
            Overrides.Add(value.Describe());
        }

        OverridesSummary = Overrides.Count switch
        {
            0 => "No compile-time overrides saved — this builds the firmware's own settings.",
            1 => "1 compile-time setting from the Config page will be built in:",
            var n => $"{n} compile-time settings from the Config page will be built in:",
        };

        UnappliedEditsText = _unappliedEdits() switch
        {
            0 => string.Empty,
            1 =>
                "1 change on the Config page hasn't been applied — press Apply there, or it won't be built in.",
            var n =>
                $"{n} changes on the Config page haven't been applied — press Apply there, or they won't be built in.",
        };

        OnPropertyChanged(nameof(HasOverrides));
        OnPropertyChanged(nameof(BuildStatusText));
    }

    partial void OnSelectedMethodChanged(FlashMethodChoice value)
    {
        // The tool lists don't overlap between methods, so a stale selection would be invalid.
        SelectedTool = Tools[0];
        // A scan is method-and-tool specific; what it found no longer applies.
        ClearDevices();
    }

    partial void OnSelectedToolChanged(string value) => ClearDevices();

    /// <summary>Applying a clicked device is just filling the field it targets — so the manual box
    /// shows what was picked, and a scan-then-click lands in the same place as typing would.</summary>
    partial void OnSelectedDeviceChanged(FlashTarget? value)
    {
        switch (value?.Field)
        {
            case FlashTargetField.Serial:
                Serial = value.Value;
                break;
            case FlashTargetField.Index:
                Index = value.Value;
                break;
            case FlashTargetField.Port:
                Port = value.Value;
                break;
        }
    }

    private void ClearDevices()
    {
        Devices.Clear();
        SelectedDevice = null;
        DeviceStatus = "Scan to list the boards you can flash to.";
    }

    private bool CanRun => IsAvailable && !IsBusy;

    /// <summary>Writes the saved compile-time settings into the generated headers, then builds in the
    /// Docker toolchain image. The two are one button on purpose: a build that silently ignored the
    /// settings page would be the whole feature quietly not working.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Build()
    {
        if (_firmwareDirectory is null || _configDirectory is null)
        {
            return;
        }

        var overrides = _savedOverrides();
        try
        {
            var written = ConfigOverrides.Write(_configDirectory, overrides);
            Append(
                overrides.Count == 0
                    ? "No compile-time overrides — building the headers as committed."
                    : $"Applying {overrides.Count} compile-time override(s): "
                        + string.Join(", ", overrides.Select(o => $"{o.Name}={o.Value}"))
            );
            if (written.Count > 0)
            {
                Append($"Wrote {string.Join(" and ", written)}.");
            }
        }
        catch (Exception ex)
        {
            // Nothing is built on a bad override: compiling the committed defaults instead would
            // hand back a board that looks right and isn't.
            Append($"ERROR: could not write the compile-time overrides: {ex.Message}");
            return;
        }

        Refresh();
        await ExecuteAsync(
            "Building",
            FirmwareCommands.Build(_firmwareDirectory, SelectedBuild, RebuildImage)
        );
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Flash()
    {
        if (_firmwareDirectory is null)
        {
            return;
        }

        if (NeedsBootloader)
        {
            Append(
                $"{SelectedMethod.Title}: the board must be in its bootloader — BOOT0 high, then reset."
            );
        }

        // Get the link off the board first. Programming it over the same USB the link is holding
        // open breaks that link either way — the port stops answering, and the reset at the end of
        // a flash takes the device node with it — so the only choice is whether it happens in an
        // order we control. Suspend reports whether there was a link, which is what decides whether
        // to bring one back: a flash the user started with nothing connected should leave it that
        // way rather than helpfully connecting to a board they were only programming.
        bool resume = _link is not null && await _link.SuspendAsync();
        try
        {
            await ExecuteAsync(
                "Flashing",
                FirmwareCommands.Flash(
                    _firmwareDirectory,
                    new FlashRequest(
                        SelectedMethod.Method,
                        SelectedTool,
                        SelectedBuild,
                        Serial,
                        Index,
                        Port,
                        Baud
                    )
                )
            );
        }
        finally
        {
            if (resume)
            {
                // In a finally, so a flash that failed or was cancelled still gets the link back.
                // The board is on the bus either way, and having to reconnect by hand because the
                // flash errored is the pointless half of the original problem.
                await ReconnectAsync();
            }
        }
    }

    /// <summary>
    /// Waits out the board's restart and puts the link back. Stays busy while it does: the page's
    /// buttons are live again the moment <see cref="ExecuteAsync"/> returns, and a second Flash
    /// started while this is still polling would begin programming the board just as the watcher
    /// reconnects to it — with nothing connected at that moment, the flash would not know to
    /// release the link it is about to break.
    /// </summary>
    private async Task ReconnectAsync()
    {
        IsBusy = true;
        BusyText = "Reconnecting…";
        _running = new CancellationTokenSource();
        Append("Waiting for the board to restart, then reconnecting…");
        try
        {
            await _link!.ResumeAsync(_running.Token);
        }
        finally
        {
            _running.Dispose();
            _running = null;
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    /// <summary>Ask the chosen tool what it can see and turn the answer into a list the user clicks,
    /// so "which board am I about to overwrite" is a choice rather than a serial number to copy. The
    /// raw output still goes to the Console tab — the list is a convenience over it, and the fallback
    /// when a device the tool can't quite name still shows up there.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Scan()
    {
        if (_firmwareDirectory is null)
        {
            return;
        }

        var method = SelectedMethod.Method;
        var tool = SelectedTool;
        var command = FirmwareCommands.ListDevices(_firmwareDirectory, method, tool);

        IsBusy = true;
        BusyText = "Scanning…";
        _running = new CancellationTokenSource();
        Append($"$ {command.DisplayLine}");

        // Captured on the read thread (the sole writer, so no lock) and parsed after; the Console
        // copy is marshalled to the UI thread. A scan does not reveal the Console the way a build
        // does — its result is the on-page list, not the raw text.
        var captured = new List<string>();
        try
        {
            var exit = await ProcessRunner.RunAsync(
                command,
                line =>
                {
                    captured.Add(line);
                    Dispatcher.UIThread.Post(() => Append(line));
                },
                _running.Token
            );

            var found = DeviceScanParser.Parse(method, tool, captured);
            Devices.Clear();
            foreach (var device in found)
            {
                Devices.Add(device);
            }
            // Keep a prior hand-typed value if the scan re-found it; otherwise pick the first.
            SelectedDevice =
                found.FirstOrDefault(d => d.Value == ValueFor(d.Field)) ?? found.FirstOrDefault();

            DeviceStatus = (found.Count, tool) switch
            {
                (> 0, _) =>
                    $"{Wording.Count(found.Count, "device")} found — click one to select it.",
                // openocd is the one tool with no list mode; the scan just prints as much.
                (_, "openocd") =>
                    "openocd can't list devices — scan with st-flash instead, or type the serial below.",
                _ when exit != 0 => "Scan failed — see the Console tab.",
                _ when NeedsBootloader =>
                    "No devices found. Check it's connected and in bootloader mode (BOOT0 high, then reset).",
                _ => "No devices found. Check it's connected.",
            };
        }
        catch (OperationCanceledException)
        {
            DeviceStatus = "Scan cancelled.";
        }
        finally
        {
            _running.Dispose();
            _running = null;
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    private string ValueFor(FlashTargetField field) =>
        field switch
        {
            FlashTargetField.Serial => Serial,
            FlashTargetField.Index => Index,
            FlashTargetField.Port => Port,
            _ => string.Empty,
        };

    private bool CanCancel => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _running?.Cancel();

    private async Task ExecuteAsync(string what, ProcessCommand command)
    {
        IsBusy = true;
        BusyText = $"{what}…";
        _running = new CancellationTokenSource();
        OutputStarted?.Invoke();

        // Echo the command first: everything the page does is something the user could have typed,
        // and showing it keeps that promise checkable.
        Append($"$ {command.DisplayLine}");

        try
        {
            var exit = await ProcessRunner.RunAsync(
                command,
                // The read loop is not on the UI thread.
                line => Dispatcher.UIThread.Post(() => Append(line)),
                _running.Token
            );
            Append(exit == 0 ? $"✓ {what} finished." : $"✗ {what} failed (exit code {exit}).");
        }
        catch (OperationCanceledException)
        {
            Append($"✗ {what} cancelled.");
        }
        finally
        {
            _running.Dispose();
            _running = null;
            IsBusy = false;
            BusyText = string.Empty;
            OnPropertyChanged(nameof(BuildStatusText));
        }
    }

    private void Append(string line)
    {
        Output.Add(line);
        // Trimmed in blocks rather than one line per append: RemoveAt(0) shifts every index and
        // re-fires the console's collection events, so a per-line trim would churn the list (and
        // its scroll position) on every single line of a long build.
        if (Output.Count > 1100)
        {
            while (Output.Count > 1000)
            {
                Output.RemoveAt(0);
            }
        }
    }

    private string DescribeBuild()
    {
        if (_firmwareDirectory is null)
        {
            return string.Empty;
        }

        // Either tree may hold the image — Docker builds land in build-docker/, an IDE or native
        // build in build/ — and flash.sh takes whichever is newer. Report the same one it would.
        var newest = new[] { "build-docker", "build" }
            .Select(tree => new FileInfo(
                Path.Combine(_firmwareDirectory, tree, SelectedBuild.ToString(), ElfName)
            ))
            .Where(f => f.Exists)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (newest is null)
        {
            return $"No {SelectedBuild} build yet — press Build, and this is what Flash will send.";
        }

        var tree = Path.GetRelativePath(_firmwareDirectory, newest.DirectoryName!);
        return $"{SelectedBuild} image ready in {tree}/ — built {Ago(newest.LastWriteTimeUtc)}. "
            + "This is what Flash will send.";
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes} min ago",
            { TotalHours: < 24 } => $"{(int)span.TotalHours} h ago",
            _ => $"{(int)span.TotalDays} d ago",
        };
    }
}
