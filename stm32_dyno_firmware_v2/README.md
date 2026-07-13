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
./Scripts/build-docker.sh            # Debug
./Scripts/build-docker.sh Release
```
The repo is bind-mounted, so output still lands in `build/<CONFIG>/` on the host.
On Windows run it from Git Bash/WSL. (On SELinux hosts the script adds the
required `:z` mount option automatically.)

## Regenerating Code from the `.ioc`
The toolchain in the `.ioc` is set to **CMake**. After editing the design in
STM32CubeMX, click **Generate Code** to refresh the HAL/driver sources and
`cmake/stm32cubemx/CMakeLists.txt`. Your edits in the top-level `CMakeLists.txt`
(and inside `USER CODE BEGIN/END` blocks) are preserved.

To regenerate headlessly from the command line, drive STM32CubeMX with a script:
```bash
printf 'config load %s/stm32_dyno_firmware_v2.ioc\nproject generate\nexit\n' "$PWD" > /tmp/gen.txt
/path/to/STM32CubeMX -q /tmp/gen.txt
```

## Flashing the Firmware
Build once, then flash the generated binary — **no rebuild needed**. Three methods
(SWD via ST-Link, USB DFU, or UART) work on Linux and Windows; you pick the method
and tool explicitly.

```bash
./Scripts/build-docker.sh Debug                 # build → build-docker/Debug/*.elf,*.bin
./Scripts/flash.sh Debug swd --tool st-flash    # flash that image (no rebuild)
```
```powershell
.\Scripts\flash.ps1 -Config Debug -Method swd -Tool st-flash
```

The open-source tools (`st-flash`, `openocd`, `dfu-util`, `stm32flash`) install
from apt/dnf with no account; **STM32CubeProgrammer is _not_ in apt/dnf and needs
a free ST account**. On Linux, USB access also needs a one-time udev-rule / group
setup.

See **[Scripts/README.md](Scripts/README.md)** for the full guide: installing each
tool, choosing among multiple connected probes, device discovery, the CMake
`flash` targets, and **Linux USB permissions**.

## Continuous Integration
`.github/workflows/build.yml` builds both `Debug` and `Release` with CMake on every
push/PR and uploads the resulting firmware as workflow artifacts.

## Notes
- Ensure all submodules are initialized and updated before building.
- The build is IDE-independent. The project can still be opened in STM32CubeIDE
  1.15+ via **File → Import → Import CMake Project**, but that is optional.
