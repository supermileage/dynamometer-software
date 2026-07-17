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
- **Home** — connect, live telemetry, task monitor.
- **Config** (`SysConfigViewModel`) — the device's runtime parameters (saved on this PC, pushed over
  USB) and the firmware's compile-time `#define`s (saved on this PC, built in by the Firmware page).
  One Apply button commits both; see [[Dyno.Core]] for why saving and applying are separate.
- **Firmware** (`FirmwareViewModel`) — build the firmware in the Docker toolchain, then flash it over
  SWD, USB DFU or UART. It runs `firmware/Scripts/` and shows their output verbatim.

Every page stays alive while another is showing, so Home keeps accumulating telemetry in the
background.

### The log panel belongs to the window, not to a page
`EventLogView` is hosted by `MainWindow`, beneath whichever page is showing, because the events worth
seeing happen while you are on the page that caused them: a sysconfig write is *rejected* while you
are on Config, a link drops while you are flashing. It used to be the bottom row of Home, which is
the one page you are not on when that matters.

It holds **tabs**, one per stream it can show — the same window furniture (tab strip, Copy, Clear,
pin, hide, resize) serving all of them. Each tab is a `LogTabViewModel` over an existing collection,
so the panel is indifferent to what a tab actually is and a third is a constructor call, not new
plumbing. Two exist today:
- **Errors / Events** — the device link log, newest first, coloured by severity.
- **Console** — build/flash/scan output, appended and followed, plain text. It used to live on the
  Firmware page; moving it here means output is watchable from any page, and the panel reveals this
  tab automatically the moment a command runs (`FirmwareViewModel.OutputStarted`) so output the user
  just triggered is never off-screen. What the two tabs share is only their shape; what differs —
  read order, colouring, how a copy is rendered — is two flags and a delegate on the tab.

Three placements, and the panel is resizable in each (drag the grip on its top edge):
- **Pinned** (default) — it takes a strip at the foot of the window and the page ends above it, so
  nothing is ever covered.
- **Floating** — it hovers over the foot of the page instead of shortening it. The dense pages are
  worth their full height, and there the log is something you glance at rather than read.
- **Hidden** — a one-line bar, *not* nothing: it still shows the newest line and counts what arrived
  while it was shut, turning red once any of that was an error or a warning. A hidden log that
  silently swallowed a fault would be worse than no log.

### The Firmware page
Two steps, in the order you do them. **Build** writes the saved compile-time settings into the
generated override headers and then builds — one button, because a build that silently ignored the
Config page would be the whole feature quietly not working. It says what it will bake in before it
does, and warns when the Config page holds edits nobody pressed Apply on (those are *not* built in).

**Flash** offers the three methods as what they physically are — a probe, a USB cable, a serial
adapter — because that, not a protocol name, is what decides which one a user can actually use. The
tool list follows the method, the device boxes follow the tool (a DFU index only means something to
`cubeprog`), and **Scan** turns the chosen tool's `--list` output into a **clickable list of the
boards you can flash** (`DeviceScanParser`) — click one and it fills the serial / index / port, so a
board is a choice rather than a serial number to copy. Each tool prints its own format, so parsing is
per (method, tool) and deliberately lax; the raw output still goes to the Console tab as the ground
truth, and the box is still typeable for a device the tool can't quite name. DFU and UART go through
the chip's ROM bootloader, which the board does not enter on its own, so the page says so rather than
letting the flash fail at it.

Nothing is reimplemented here: both buttons run `firmware/Scripts/`, echo the command first, and
stream the output unedited into the log panel's **Console** tab (the page no longer shows it itself —
the panel opens that tab on its own when a command runs). On Linux the page also names the
udev/`dialout` permissions up front — the failure otherwise reads as a mysterious `libusb open
failed` and sends people to `sudo`.

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
