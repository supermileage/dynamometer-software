# Dyno Software

PC-side companion to [`stm32_dyno_firmware_v2`](https://github.com/supermileage/stm32_dyno_firmware_v2)
(vendored here as a git submodule). It connects to the dynamometer's STM32 over USB-CDC,
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
| `stm32_dyno_firmware_v2/` | Firmware submodule — the wire-protocol **source of truth**. |

## Cloning the repository
The firmware schema lives in a submodule, so clone with submodules:

```bash
git clone --recurse-submodules <repository-url>
```

If you forgot the flag:

```bash
git submodule update --init --recursive
```

## Requirements
| Tool | Purpose | Install (Fedora) | Install (Ubuntu/Debian) | Install (Windows) |
|------|---------|------------------|--------------------------|-------------------|
| .NET SDK 10 | Build / run / test | `sudo dnf install dotnet-sdk-10.0` | [packages.microsoft.com](https://learn.microsoft.com/dotnet/core/install/linux) | `winget install Microsoft.DotNet.SDK.10` |
| Python 3 + venv | Only to **regenerate** message types | `sudo dnf install python3` | `sudo apt install python3 python3-venv` | `winget install Python.Python.3.13` |

On **Linux**, opening a serial port needs the user in the `dialout` group:
`sudo usermod -aG dialout "$USER"` (re-login afterwards).
On **Windows** no setup is needed: the board's USB-CDC interface enumerates as a
`COMx` port with the inbox `usbser.sys` driver.

## Building the project
The same commands work on Linux, macOS and Windows:
```bash
dotnet build Dyno.slnx            # build everything (Debug)
dotnet test  Dyno.slnx            # run the unit tests
dotnet build Dyno.slnx -c Release # release build
```

## Running the app
```bash
./Scripts/run.sh                 # Linux/macOS
Scripts\run.ps1                  # Windows (PowerShell)
```
The GUI needs a display (X11/Wayland on Linux) and a connected board to show live data.
End users get native, per-RID self-contained builds:
```bash
dotnet publish src/Dyno.App/Dyno.App.csproj -c Release -r linux-x64 --self-contained
dotnet publish src/Dyno.App/Dyno.App.csproj -c Release -r win-x64   --self-contained
```

## Regenerating message types
The committed `src/Dyno.Core/Messages/Generated/Messages.cs` is generated from the firmware
submodule's `messages_public.yaml`. After bumping the submodule (or changing the schema):
```bash
./Scripts/generate.sh            # Linux/macOS
Scripts\generate.ps1             # Windows (PowerShell)
```
CI fails if the committed file is out of sync (`python tools/message_gen/check.py`). Details:
[tools/message_gen/README.md](tools/message_gen/README.md).

## Continuous integration
`.github/workflows/build.yml`:
- **codegen** — verifies `Messages.cs` matches the schema (drift guard).
- **build & test** — `dotnet build`/`test` on **Ubuntu and Windows**, then publishes the app
  per RID (`linux-x64`, `win-x64`) as workflow artifacts.

## Notes
- Initialize the submodule before building or regenerating types.
- On connect the firmware announces itself and the host replies with a version-checked `USB_CMD_ACK`
  handshake; no telemetry streams until it completes, and a `USB_PROTOCOL_VERSION` mismatch refuses
  the link rather than mis-decoding the stream.
- Sensor data streams **only while the dyno is running a session**. The device announces each
  session start/stop (and re-states it after every ack), so the app can show whether a session is on
  and hide the readouts when it is not — an idle board and a dead one otherwise look identical.
- `Dyno.Core` has no UI dependency, so it can be driven headlessly or from another front-end.
- Target framework is `net10.0`; the solution uses the `.slnx` (XML) solution format.
