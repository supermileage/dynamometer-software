---
module: Dyno.App
summary: Avalonia (CommunityToolkit.Mvvm) desktop UI — connect, live telemetry, task monitor, and an error/event log.
code:
  - src/Dyno.App/Program.cs
  - src/Dyno.App/App.axaml.cs
  - src/Dyno.App/ViewModels/MainWindowViewModel.cs
  - src/Dyno.App/Views/MainWindow.axaml
related: [Dyno.Core]
---

# Dyno.App — desktop UI

Cross-platform (Windows + Linux) Avalonia front-end over [[Dyno.Core]]. Renders with Skia,
so it does not depend on WPF/Win32.

## Structure
- **`Program.cs`** — Avalonia entry point (`BuildAvaloniaApp`).
- **`App.axaml(.cs)`** — composition root: builds the Serilog-backed `ILoggerFactory`, then
  constructs `MainWindowViewModel` and the window. Releases the device link on shutdown.
- **`ViewModels/MainWindowViewModel.cs`** — port selection + connect/disconnect lifecycle;
  owns a `Dyno.Core.DeviceClient` and applies its messages on the UI thread
  (`Dispatcher.UIThread`). Uses CommunityToolkit.Mvvm source generators
  (`[ObservableProperty]`, `[RelayCommand]`).
- **`ViewModels/TaskMonitorRow.cs`** — one observable row in the task table, updated in place.
- **`Views/MainWindow.axaml`** — compiled-binding (`x:DataType`) view: sidebar, connection toolbar,
  telemetry panel, task-monitor list, and an errors/events log.
- **`Services/FirmwareConfigLocator.cs`** — finds the firmware tree by walking up from where the app
  runs (`DYNO_FIRMWARE_CONFIG_DIR` overrides it). The Config and Firmware pages both need it, and
  both disable themselves cleanly when the app runs outside the repo.

## Session gating
The dyno only streams sensor data while it is running a session, and it says so explicitly (see
[[Dyno.Core]] — `SessionState`). The UI follows that signal:

- a **session pill** in the toolbar (green *Session running* / grey *No session*) — otherwise an
  idle board and a dead one are indistinguishable, since both stream nothing;
- the **telemetry readouts are replaced**, not merely frozen, by a "No session running" notice
  while `IsSessionActive` is false. A stale-but-plausible number left on screen reads as a live
  measurement, which is the failure worth avoiding.

A session stop clears the readouts, and `Apply` ignores any sensor sample that arrives while no
session is active — samples framed just before a stop can still be in flight behind the stop event.
Health data (task monitor) and faults are *not* gated: they are most worth seeing while the dyno
sits idle.

## Pages
- **Home** — connect, live telemetry, task monitor, event log.
- **Config** (`SysConfigViewModel`) — the device's runtime parameters (saved on this PC, pushed over
  USB) and the firmware's compile-time `#define`s (saved on this PC, built in by the Firmware page).
  One Apply button commits both; see [[Dyno.Core]] for why saving and applying are separate.
- **Firmware** (`FirmwareViewModel`) — build the firmware in the Docker toolchain, then flash it over
  SWD, USB DFU or UART. It runs `firmware/Scripts/` and shows their output verbatim.

Every page stays alive while another is showing, so Home keeps accumulating events in the background.

### The Firmware page
Two steps, in the order you do them. **Build** writes the saved compile-time settings into the
generated override headers and then builds — one button, because a build that silently ignored the
Config page would be the whole feature quietly not working. It says what it will bake in before it
does, and warns when the Config page holds edits nobody pressed Apply on (those are *not* built in).

**Flash** offers the three methods as what they physically are — a probe, a USB cable, a serial
adapter — because that, not a protocol name, is what decides which one a user can actually use. The
tool list follows the method, the device boxes follow the tool (a DFU index only means something to
`cubeprog`), and **Scan** lists what is plugged in, since the serial numbers it prints are what the
Serial box wants. DFU and UART go through the chip's ROM bootloader, which the board does not enter
on its own, so the page says so rather than letting the flash fail at it.

Nothing is reimplemented here: both buttons run `firmware/Scripts/`, echo the command first, and
stream the output unedited. On Linux the page also names the udev/`dialout` permissions up front —
the failure otherwise reads as a mysterious `libusb open failed` and sends people to `sudo`.

## Logging
Serilog → console + a rolling file under `logs/`, bridged to `Microsoft.Extensions.Logging`
via `SerilogLoggerFactory` and injected into `DeviceClient`.

## Running
`./Scripts/run.sh` (or `dotnet run --project src/Dyno.App`). Needs a display; on Linux the
user must be in the `dialout` group to open `/dev/ttyACM*`. Ship native per-RID
`dotnet publish` builds to users.

## Deferred
Compile-time settings have no *dependency* rules in the UI: the firmware enforces them itself with
`#error`, so a bad combination fails the build with a good message rather than being greyed out in
advance. Moving that check earlier means a real config schema (Kconfig, or extending the existing
[[message_gen]] YAML), which is worth doing only if these options grow or start gating each other.

## Related
[[Dyno.Core]]
