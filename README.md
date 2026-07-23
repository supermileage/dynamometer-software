# Dyno Software

PC-side companion to the STM32 dynamometer firmware, which lives in this repo under
[`firmware/`](firmware/). It connects to the dynamometer's STM32 over USB-CDC,
decodes its telemetry/error streams, and can send framed commands back. The UI is **Avalonia**
(cross-platform .NET); the device logic lives in a UI-agnostic `Dyno.Core` library.

The USB wire protocol is **not hand-maintained**: the firmware defines it once as a YAML
schema and the C# message types are generated from that same schema (see
[Regenerating message types](#regenerating-message-types)), so the host and firmware can't drift.

## Layout
| Path | What |
|---|---|
| `src/Dyno.Core/` | Device layer: serial, protocol parser + frame builder, generated message types. See [README](src/Dyno.Core/README.md). |
| `src/Dyno.App/` | Avalonia desktop UI. See [README](src/Dyno.App/README.md). |
| `tests/Dyno.Core.Tests/` | xUnit tests (struct sizes, CRC, error decode, parser). |
| `tools/message_gen/` | YAML → C# codegen + drift guard. See [README](tools/message_gen/README.md). |
| `firmware/` | STM32 firmware — the wire-protocol **source of truth**. See [README](firmware/README.md). |

## Requirements
| Tool | Purpose | Install (Fedora) | Install (Ubuntu/Debian) | Install (Windows) |
|------|---------|------------------|--------------------------|-------------------|
| .NET SDK 10 | Build / run / test | `sudo dnf install dotnet-sdk-10.0` | [packages.microsoft.com](https://learn.microsoft.com/dotnet/core/install/linux) | `winget install Microsoft.DotNet.SDK.10` |
| Python 3 + venv | Only to **regenerate** message types | `sudo dnf install python3` | `sudo apt install python3 python3-venv` | `winget install Python.Python.3.13` |

On **Linux**, opening a serial port needs the user in the `dialout` group, and ModemManager has to
be told to leave the board alone:
```bash
sudo usermod -aG dialout "$USER"                              # re-login afterwards
sudo install -m 0644 scripts/udev/99-dyno-cdc.rules /etc/udev/rules.d/
sudo udevadm control --reload && sudo udevadm trigger --subsystem-match=tty
```
Skip the rule and connects are intermittent rather than broken: ModemManager probes each
newly-appeared `ttyACM` with AT commands for ~20-30 s, holding the port open, so whether a connect
succeeds depends on how long after plugging in you click. The board appears flaky, not busy.
The rule also adds a stable `/dev/dyno` symlink, so the port keeps its name across replugs.

On **Windows** no setup is needed: the board's USB-CDC interface enumerates as a
`COMx` port with the inbox `usbser.sys` driver.

> The board's USB-CDC link comes from the STM32's own USB connector, **not** from an attached
> ST-Link. An ST-Link/V2 (`0483:3748`) carries no virtual COM port at all, so a board wired up for
> debugging only will flash fine and never appear in the app's port list. Confirm with
> `lsusb | grep 0483` — the data link is the device that reports `0483:5740`.

## Building the project
The same commands work on Linux, macOS and Windows:
```bash
dotnet build Dyno.slnx            # build everything (Debug)
dotnet test  Dyno.slnx            # run the unit tests
dotnet build Dyno.slnx -c Release # release build
```

## Running the app
```bash
./scripts/run.sh                 # Linux/macOS
scripts\run.ps1                  # Windows (PowerShell)
```
The GUI needs a display (X11/Wayland on Linux) and a connected board to show live data.
End users get native, per-RID self-contained builds:
```bash
dotnet publish src/Dyno.App/Dyno.App.csproj -c Release -r linux-x64 --self-contained
dotnet publish src/Dyno.App/Dyno.App.csproj -c Release -r win-x64   --self-contained
```

## Regenerating message types
The committed `src/Dyno.Core/Messages/Generated/Messages.cs` is generated from the firmware's
`messages_public.yaml`. After changing the schema:
```bash
./scripts/generate.sh            # Linux/macOS
scripts\generate.ps1             # Windows (PowerShell)
```
CI fails if the committed file is out of sync (`python tools/message_gen/check.py`). Details:
[tools/message_gen/README.md](tools/message_gen/README.md).

## Continuous integration
`.github/workflows/build.yml`:
- **codegen** — verifies `Messages.cs` matches the schema (drift guard).
- **build & test** — `dotnet build`/`test` on **Ubuntu and Windows**, then publishes the app
  per RID (`linux-x64`, `win-x64`) as workflow artifacts.

`.github/workflows/firmware.yml`:
- **generated-headers** — verifies the firmware's committed headers match the schema.
- **build** — Docker-toolchain firmware builds (Debug and Release), uploaded as workflow artifacts.

## Notes
- On connect the firmware announces itself and the host replies with a version-checked `USB_CMD_ACK`
  handshake; no telemetry streams until it completes, and a `USB_PROTOCOL_VERSION` mismatch refuses
  the link rather than mis-decoding the stream.
- Sensor data streams **only while the dyno is running a session**. The device announces each
  session start/stop (and re-states it after every ack), so the app can show whether a session is on
  and hide the readouts when it is not — an idle board and a dead one otherwise look identical.
- `Dyno.Core` has no UI dependency, so it can be driven headlessly or from another front-end.
- Target framework is `net10.0`; the solution uses the `.slnx` (XML) solution format.
