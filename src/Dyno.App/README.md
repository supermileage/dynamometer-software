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
- **`Views/MainWindow.axaml`** — compiled-binding (`x:DataType`) view: connection toolbar,
  telemetry panel, task-monitor list, and an errors/events log.

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

## Logging
Serilog → console + a rolling file under `logs/`, bridged to `Microsoft.Extensions.Logging`
via `SerilogLoggerFactory` and injected into `DeviceClient`.

## Running
`./Scripts/run.sh` (or `dotnet run --project src/Dyno.App`). Needs a display; on Linux the
user must be in the `dialout` group to open `/dev/ttyACM*`. Ship native per-RID
`dotnet publish` builds to users.

## Deferred
Outbound-command UI (e.g. set the ADS1115 data rate) — the transport already exists in
[[Dyno.Core]] (`DeviceClient.SendCommandAsync`); only the controls are pending.

## Related
[[Dyno.Core]]
