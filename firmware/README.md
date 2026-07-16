# STM32 Dyno Firmware v2

## Overview
This repository contains the firmware for the STM32-based dynamometer project. It
targets the **STM32H743IITx** microcontroller and uses FreeRTOS for real-time task
management. The project is built with **CMake + Ninja** and the Arm GNU toolchain
(`arm-none-eabi-gcc`); code is generated from the `.ioc` with **STM32CubeMX**.

## Cloning the Repository
Include all submodules when cloning:

```bash
git clone --recurse-submodules <repository-url>
```

If you forgot the flag, initialize the submodules afterwards:

```bash
git submodule update --init --recursive
```

## Requirements
You only need these to **build**:

| Tool | Purpose | Install (Fedora) | Install (Ubuntu/Debian) |
|------|---------|------------------|--------------------------|
| Arm GNU toolchain | Compiler/linker | `sudo dnf install arm-none-eabi-gcc-cs arm-none-eabi-newlib` | `sudo apt install gcc-arm-none-eabi` |
| CMake (≥ 3.22) | Build system | `sudo dnf install cmake` | `sudo apt install cmake` |
| Ninja | Build backend | `sudo dnf install ninja-build` | `sudo apt install ninja-build` |

Additional, only if you need them:
- **STM32CubeMX** — to regenerate code after editing `stm32_dyno_firmware_v2.ioc`
  ([download](https://www.st.com/en/development-tools/stm32cubemx.html)). Not in
  apt/dnf; the download requires a free ST (myST) account.
- A **flashing tool** — to program the board. The open-source options
  (`stlink`, `openocd`, `dfu-util`, `stm32flash`) install from apt/dnf with no
  account; **STM32CubeProgrammer is _not_ available via apt/dnf** and requires a
  free ST account to download. See [Flashing the Firmware](#flashing-the-firmware).

## Building the Project

### Native build (CMake)
The same commands work on Linux, macOS and Windows:
```bash
cmake --preset Debug            # configure (use Release for the release build)
cmake --build --preset Debug    # build
rm -rf build                    # clean
```
Presets (`Debug`, `Release`) are defined in `CMakePresets.json`; the Arm toolchain
file is `cmake/gcc-arm-none-eabi.cmake`.

Build output is written to `build/<CONFIG>/`:
- `stm32_dyno_firmware_v2.elf`
- `stm32_dyno_firmware_v2.hex`
- `stm32_dyno_firmware_v2.bin`
- `stm32_dyno_firmware_v2.map`

### Reproducible build (Docker)
Requires only Docker — no host toolchain. The `Dockerfile` pins the Arm GNU
toolchain, CMake and Ninja, and CI builds inside this same image:
```bash
./Scripts/build-docker.sh            # Release          (Linux/macOS/Git-Bash)
./Scripts/build-docker.sh Debug
```
```powershell
.\Scripts\build-docker.ps1                  # Release   (Windows/PowerShell)
.\Scripts\build-docker.ps1 -Config Debug
```
The repo is bind-mounted, so output lands in `build-docker/<CONFIG>/` on the host
— a separate tree from a native build's `build/<CONFIG>/`, so the two can coexist.
(On SELinux hosts the shell script adds the required `:z` mount option
automatically; on Windows, Docker Desktop must be in Linux container mode, which
is the default.)

## Regenerating Code from the `.ioc`
The toolchain in the `.ioc` is set to **CMake**. After editing the design in
STM32CubeMX, click **Generate Code** to refresh the HAL/driver sources and
`cmake/stm32cubemx/CMakeLists.txt`. Your edits in the top-level `CMakeLists.txt`
(and inside `USER CODE BEGIN/END` blocks) are preserved.

To regenerate headlessly from the command line, use the wrapper script — it
finds STM32CubeMX, feeds it a `config load … / project generate` script, and (on
Linux) wraps the run in `xvfb-run` for headless hosts:
```bash
./Scripts/regen-cube.sh                                   # auto-find CubeMX + the .ioc
./Scripts/regen-cube.sh --cubemx ~/STM32CubeMX/STM32CubeMX # or point at the binary
./Scripts/regen-cube.sh --check                           # regenerate, then fail on drift
```
```powershell
.\Scripts\regen-cube.ps1
.\Scripts\regen-cube.ps1 -Cubemx "C:\Program Files\STMicroelectronics\STM32Cube\STM32CubeMX\STM32CubeMX.exe"
```
CubeMX is located via `--cubemx`/`-Cubemx`, then `$STM32CUBEMX`, then common
install dirs, then `STM32CubeMX` on `PATH`. On a headless Linux host, install the
virtual-display helper (`dnf install xorg-x11-server-Xvfb` / `apt install xvfb`).

### The firmware pack (no ST account needed)
Generation needs the HAL/driver package the `.ioc` names
(`ProjectManager.FirmwarePackage`, currently **`STM32Cube FW_H7 V1.12.1`**). Rather
than downloading it from ST behind a myST login, it is **vendored as a submodule**
— [`STM32CubeH7`](https://github.com/STMicroelectronics/STM32CubeH7) pinned to tag
`v1.12.1`, which is the same package ST ships, public and cloneable:
```bash
git submodule update --init firmware/third_party/STM32CubeH7
```
`regen-cube.sh` links it into CubeMX's repository under the exact name CubeMX
expects, so no pack install is required. Two things worth knowing:
- The submodule is only needed to **regenerate** — never to *build*, since the
  generated HAL is committed. It's ~2 GB, so skip initialising it unless you're
  regenerating.
- `STM32CubeH7` is a **meta-repo** whose sources are themselves nested submodules;
  a plain clone leaves `Drivers/` empty and CubeMX stalls exactly as if the pack
  were missing. The script populates the two that matter
  (`STM32H7xx_HAL_Driver`, `CMSIS/Device`) and ignores the ~40 eval-board BSPs.

> **Match the CubeMX version.** CubeMX prompts to migrate when its version differs
> from the `.ioc`'s `MxCube.Version`. Headless there's nobody to answer, so the run
> hangs at `config load` with no output and no error. Keep your CubeMX in step with
> the `.ioc` (and pin the same version in CI).

Under the hood it just drives CubeMX's own scripting mode, which you can also run
by hand:
```bash
printf 'config load %s/stm32_dyno_firmware_v2.ioc\nproject generate\nexit\n' "$PWD" > /tmp/gen.txt
/path/to/STM32CubeMX -q /tmp/gen.txt
```

## Flashing the Firmware
Build once, then flash the generated binary — **no rebuild needed**. Three methods
(SWD via ST-Link, USB DFU, or UART) work on Linux and Windows; you pick the method
and tool explicitly.

Both scripts default to the **Release** build, so the pair lines up with no arguments
(pass `Debug` / `-Config Debug` to build and flash the Debug image instead).
```bash
./Scripts/build-docker.sh                 # build → build-docker/Release/*.elf,*.bin
./Scripts/flash.sh swd --tool st-flash    # flash that image (no rebuild)
```
```powershell
.\Scripts\build-docker.ps1
.\Scripts\flash.ps1 -Method swd -Tool st-flash
```

The open-source tools (`st-flash`, `openocd`, `dfu-util`, `stm32flash`) install
from apt/dnf with no account; **STM32CubeProgrammer is _not_ in apt/dnf and needs
a free ST account**. On Linux, USB access also needs a one-time udev-rule / group
setup.

See **[Scripts/README.md](Scripts/README.md)** for the full guide: installing each
tool, choosing among multiple connected probes, device discovery, the CMake
`flash` targets, and **Linux USB permissions**.

## Continuous Integration
`.github/workflows/firmware.yml` runs on every push/PR:
- **build** — builds `Debug` and `Release` with CMake in the pinned Docker image
  and uploads the firmware as workflow artifacts.
- **generated-headers** — verifies the committed MessagePassing headers still
  match their YAML schema.
- **ioc-drift** — regenerates from the `.ioc` with STM32CubeMX and fails if the
  committed generated code drifted (i.e. the `.ioc` was edited without running
  `Scripts/regen-cube.sh`). `.mxproject` churn is ignored. **Off by default** —
  see below to enable it.

### Enabling the `ioc-drift` check
It needs STM32CubeMX **plus** the ST-licensed `STM32Cube FW_H7` pack, which can't
live in a public image. It stays **skipped** until you point it at a private image:

1. Build the image once from `cubemx.Dockerfile` (bundles your CubeMX install and
   `~/STM32Cube/Repository`) and push it to a **private** registry. The Dockerfile
   header has the exact `docker build`/`push` recipe. ST credentials are used only
   here, at build time — never in CI.
2. In the repo's **Settings → Secrets and variables → Actions**, set:
   - variable **`CUBEMX_IMAGE`** = the image ref (e.g. `ghcr.io/<org>/cubemx-h7:6.18.0`)
   - secret **`CUBEMX_IMAGE_TOKEN`** = a token that can pull that private image

Once both are set the job runs on every push/PR; leave them unset and it's a no-op.

## Notes
- Ensure all submodules are initialized and updated before building.
- The build is IDE-independent. The project can still be opened in STM32CubeIDE
  1.15+ via **File → Import → Import CMake Project**, but that is optional.
