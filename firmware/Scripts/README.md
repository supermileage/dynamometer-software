# Scripts

Helper scripts for building and flashing the firmware. All are meant to run on
the **host** (not inside the Docker build container).

> The desktop app's **Firmware** page runs exactly these scripts — same arguments,
> output shown verbatim — and additionally writes the compile-time settings saved
> on its Config page into `Core/Inc/Config/*_overrides.h` before building. Use
> whichever you prefer; they cannot disagree. A build from this terminal picks up
> those override files too, and deleting them returns you to the committed headers.

| Script | Purpose |
|--------|---------|
| `build-docker.sh` | Build the firmware in the pinned Docker toolchain image on Linux/macOS/Git-Bash (byte-for-byte the CI environment). |
| `build-docker.ps1` | The same Docker build on Windows (PowerShell). |
| `flash.sh` | Flash a built image on Linux/macOS/Git-Bash. |
| `flash.ps1` | Flash a built image on Windows (PowerShell). |
| `99-stm32-flash.rules` | udev rules for non-root USB flashing on Linux. |

---

## Building (Docker)

```bash
./Scripts/build-docker.sh            # Release (the default)
./Scripts/build-docker.sh Debug
./Scripts/build-docker.sh Debug -r   # also rebuild the toolchain image
```
```powershell
.\Scripts\build-docker.ps1                  # Release (the default)
.\Scripts\build-docker.ps1 -Config Debug
.\Scripts\build-docker.ps1 -Rebuild         # also rebuild the toolchain image
```
The repo is bind-mounted, so output lands in `build-docker/<CONFIG>/` on the host.
Both scripts drive the same `Dockerfile` and issue the same `docker run`, so the
artifacts are identical either way.

The toolchain image is Linux-based: on Windows, Docker Desktop must be in Linux
container mode (the default). The PowerShell script checks this up front and
tells you how to switch if it isn't.

---

## Flashing

Firmware is programmed to address `0x08000000`. Three methods are supported on
**both Linux and Windows**, each over a different connection. You choose the
method **and** the tool explicitly — there is no auto-detect and no fallback, so
the same command always uses the same tool (which matters when more than one
probe/board is attached).

| Method | Needs a probe? | BOOT0 | Tools (pick one) |
|--------|----------------|-------|------------------|
| **SWD** | Yes — ST-Link/J-Link | — | `cubeprog`, `st-flash`, `openocd` |
| **USB DFU** | No — USB cable only | hold **high**, reset | `cubeprog`, `dfu-util` |
| **UART** | No — USB-serial adapter | hold **high**, reset | `cubeprog`, `stm32flash` |

`cubeprog` = STM32CubeProgrammer (`STM32_Programmer_CLI`). USB DFU and UART use
the chip's built-in ROM bootloader, entered by holding **BOOT0 high and
resetting** the board.

The scripts flash an **already-built** image without rebuilding — the right entry
point after a Docker build. Build once, flash as often as you like. Both the build
and the flash scripts default to **Release**, so the pair lines up with no
arguments; pass `Debug` explicitly to work with the Debug build instead.
```bash
./Scripts/build-docker.sh                       # Release
./Scripts/flash.sh swd  --tool st-flash         # flashes the Release build
./Scripts/flash.sh dfu  --tool dfu-util
./Scripts/flash.sh uart --tool stm32flash --port /dev/ttyUSB0
./Scripts/flash.sh Debug swd --tool st-flash    # flash the Debug build instead
```
```powershell
.\Scripts\build-docker.ps1
.\Scripts\flash.ps1 -Method swd  -Tool st-flash
.\Scripts\flash.ps1 -Method dfu  -Tool dfu-util
.\Scripts\flash.ps1 -Method uart -Tool stm32flash -Port COM5
.\Scripts\flash.ps1 -Config Debug -Method swd -Tool st-flash
```

Run `./Scripts/flash.sh --help` for the full option list.

### Installing a tool
The open-source tools come from your package manager with no account.
**STM32CubeProgrammer (`cubeprog`) is _not_ in apt/dnf** and requires a free ST
(myST) account to download.

| Tool | Linux | Windows | ST account? |
|------|-------|---------|-------------|
| `st-flash` (stlink) | `sudo dnf install stlink` · `sudo apt install stlink-tools` | [stlink releases](https://github.com/stlink-org/stlink/releases) | No |
| `openocd` | `sudo dnf install openocd` · `sudo apt install openocd` | [xpack OpenOCD](https://github.com/xpack-dev-tools/openocd-xpack/releases) | No |
| `dfu-util` | `sudo dnf install dfu-util` · `sudo apt install dfu-util` | [dfu-util.sourceforge.net](https://dfu-util.sourceforge.net/) | No |
| `stm32flash` | `sudo dnf install stm32flash` · `sudo apt install stm32flash` | [stm32flash (SourceForge)](https://sourceforge.net/projects/stm32flash/) | No |
| `cubeprog` (STM32CubeProgrammer) | installer from st.com | installer from st.com | **Yes — free [ST account](https://www.st.com/en/development-tools/stm32cubeprog.html) required** |

Since every method has at least one open-source, no-account tool, you never *need*
an ST account to flash this firmware.

### Discovering connected devices
```bash
./Scripts/flash.sh swd  --list   # ST-Link probes + serials  (cubeprog -l / st-info --probe)
./Scripts/flash.sh dfu  --list   # DFU devices + serials      (dfu-util -l)
./Scripts/flash.sh uart --list   # serial ports
```
OS-level backups: `lsusb` (shows the ST-Link, and `0483:df11 … DFU Mode` when in
the bootloader); `ls -l /dev/serial/by-id/` for stable serial-port names.

### Picking a specific device (two+ probes/boards connected)
| Method | Disambiguate by | Scripts | Manual |
|--------|-----------------|---------|--------|
| SWD | ST-Link **serial** | `--serial <sn>` | `st-flash --serial <sn>` · `… -c port=SWD sn=<sn>` · `openocd -c "adapter serial <sn>"` |
| USB DFU | DFU **serial** or index | `--serial <sn>` / `--index <n>` | `dfu-util -S <sn>` · `… -c port=USB<n>` |
| UART | **port name** | `--port <port>` | each adapter is its own `/dev/tty*` or `COMx` |

### From the build system (native toolchain only)
Rebuilds first, then flashes (the preset selects which build). Tool/serial/port
are configure-time options. Requires the Arm toolchain installed (CMake can't
configure this project without it):
```bash
cmake --preset Debug -DFLASH_TOOL=st-flash
cmake --build --preset Debug --target flash          # or flash-dfu / flash-uart
cmake --preset Debug -DFLASH_TOOL=cubeprog -DFLASH_SERIAL=0670FF...   # specific probe
cmake --preset Debug -DFLASH_TOOL=stm32flash -DFLASH_UART_PORT=/dev/ttyUSB0
```
These targets just delegate to `flash.sh`/`flash.ps1`, so the tool matrix above
applies unchanged.

### Manual (no scripts)
Paths below assume a Docker build (`build-docker/Debug/`); for a native or IDE
build, substitute `build/Debug/`. `flash.sh`/`flash.ps1` pick the right one for you.
```bash
st-flash --reset write build-docker/Debug/stm32_dyno_firmware_v2.bin 0x08000000        # SWD
STM32_Programmer_CLI -c port=SWD  -d build-docker/Debug/stm32_dyno_firmware_v2.elf -rst # SWD
STM32_Programmer_CLI -c port=USB1 -d build-docker/Debug/stm32_dyno_firmware_v2.elf -rst # USB DFU
dfu-util -a 0 -s 0x08000000:leave -D build-docker/Debug/stm32_dyno_firmware_v2.bin      # USB DFU
stm32flash -b 115200 -w build-docker/Debug/stm32_dyno_firmware_v2.bin -v -g 0x08000000 /dev/ttyUSB0  # UART
```

---

## Linux USB permissions

On Linux, non-root access to USB devices is denied by default, so flashing fails
with **`permission denied`** / **`libusb open failed`** (SWD, USB DFU) or
**`could not open /dev/ttyUSB0`** (UART) unless you grant access. There are two
distinct cases:

### ST-Link and USB DFU → udev rule
These are raw USB devices, governed by udev. Install the bundled rules once:
```bash
sudo cp Scripts/99-stm32-flash.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger
# then unplug and replug the probe / board
```
The rules grant the logged-in user access via systemd's `uaccess` tag and cover
ST-Link V1/V2/V2-1/V3 plus the STM32 DFU bootloader (`0483:df11`).

> The `stlink` package usually installs its own ST-Link rules already; the bundled
> file is still useful because it also covers the DFU bootloader, and it keeps a
> known-good copy in the repo. **STM32CubeProgrammer** ships its own rules in
> `…/STM32CubeProgrammer/Driver/rules/` — run the installer's `install.sh` there,
> or just use this file.

Verify after replugging: `lsusb` should list the device, and
`./Scripts/flash.sh swd --list` should see the probe without `sudo`.

### UART → serial-port group
A USB-serial adapter shows up as `/dev/ttyUSB*` or `/dev/ttyACM*`, owned by the
`dialout` group. Add yourself once, then **log out and back in** (group
membership is applied at login):
```bash
sudo usermod -aG dialout "$USER"
groups | grep dialout        # confirm after re-login
```
(Some distros use `uucp` instead of `dialout`; check the port's group with
`ls -l /dev/ttyUSB0`.)

### Last resort
Running the flash command under `sudo` works but is discouraged — `sudo`'s
environment/PATH differs, so a tool you installed for your user may not be found.
Prefer the udev rule / group membership above.
