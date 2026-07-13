#!/usr/bin/env bash
# Scripts/flash.sh
# Flash the built firmware to the STM32H743. You pick the method AND the tool
# explicitly — there is no auto-detect and no fallback, so the same command
# always uses the same tool (important when several probes/boards are attached).
#
# Methods and the tool each accepts:
#   swd    SWD via ST-Link probe          --tool cubeprog | st-flash | openocd
#   dfu    USB DFU via ROM bootloader      --tool cubeprog | dfu-util
#   uart   UART via ROM bootloader         --tool cubeprog | stm32flash
# (cubeprog = STM32CubeProgrammer's STM32_Programmer_CLI.)
#
# dfu and uart use the chip's built-in bootloader: hold BOOT0 high and reset the
# board first.
#
# Usage:
#   ./Scripts/flash.sh [Debug|Release] <swd|dfu|uart> --tool <tool> [options]
#   ./Scripts/flash.sh <swd|dfu|uart> [--tool <tool>] --list   # enumerate devices
#
# Options:
#   --tool <tool>   required (except with --list for uart); see table above
#   --serial <sn>   target a specific ST-Link probe (swd) or DFU device (dfu)
#   --index <n>     DFU device index for cubeprog (port=USB<n>); default 1
#   --port <port>   serial port for uart; default /dev/ttyUSB0
#   --baud <baud>   uart baud rate; default 115200
#   --list          list connected probes/DFU devices/serial ports, then exit
#
# Selecting among several connected devices:
#   swd  : --serial <sn>     (find with: --tool cubeprog --list, or st-info --probe)
#   dfu  : --serial <sn>     (find with: --tool dfu-util --list)  or  --index <n>
#   uart : --port <port>     (each adapter is a distinct /dev/tty* or COM port)
#
# Run on the host (needs USB/serial access; not via Docker). Build first:
#   cmake --build --preset Debug      # or ./Scripts/build-docker.sh Debug

set -euo pipefail

CONFIG="Debug"; METHOD=""; TOOL=""; SERIAL=""; PORT=""; BAUD="115200"; INDEX="1"; DO_LIST=0

usage() { sed -n '2,42p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        Debug|Release)  CONFIG="$1" ;;
        swd|dfu|uart)   METHOD="$1" ;;
        --tool)         TOOL="${2:?--tool needs a value}"; shift ;;
        --tool=*)       TOOL="${1#*=}" ;;
        --serial)       SERIAL="${2:?--serial needs a value}"; shift ;;
        --serial=*)     SERIAL="${1#*=}" ;;
        --index)        INDEX="${2:?--index needs a value}"; shift ;;
        --index=*)      INDEX="${1#*=}" ;;
        --port)         PORT="${2:?--port needs a value}"; shift ;;
        --port=*)       PORT="${1#*=}" ;;
        --baud)         BAUD="${2:?--baud needs a value}"; shift ;;
        --baud=*)       BAUD="${1#*=}" ;;
        --list)         DO_LIST=1 ;;
        -h|--help)      usage; exit 0 ;;
        *)              echo "ERROR: unknown argument '$1' (see --help)"; exit 1 ;;
    esac
    shift
done

[[ -n "$PORT" ]] || PORT="/dev/ttyUSB0"

[[ -n "$METHOD" ]] || { echo "ERROR: choose a method: swd | dfu | uart (see --help)"; exit 1; }

# Map a tool keyword to its executable name.
bin_for() { case "$1" in cubeprog) echo "STM32_Programmer_CLI" ;; *) echo "$1" ;; esac; }
have()    { command -v "$1" >/dev/null 2>&1; }
require() { have "$(bin_for "$1")" || { echo "ERROR: '$(bin_for "$1")' not found on PATH. Install it (see README)."; exit 1; }; }

# Which tools are valid for each method (no fallback — exactly the one you name).
case "$METHOD" in
    swd)  VALID="cubeprog st-flash openocd" ;;
    dfu)  VALID="cubeprog dfu-util" ;;
    uart) VALID="cubeprog stm32flash" ;;
esac

# --- list mode: enumerate what's connected, then exit ------------------------
if [[ $DO_LIST -eq 1 ]]; then
    case "$METHOD" in
        swd)
            case "${TOOL:-cubeprog}" in
                cubeprog) require cubeprog; exec STM32_Programmer_CLI -l ;;
                st-flash) require st-flash; have st-info && exec st-info --probe || exec st-flash --list ;;
                openocd)  echo "openocd has no list mode; use: $0 swd --tool st-flash --list"; exit 0 ;;
                *) echo "ERROR: --list for swd needs --tool cubeprog|st-flash"; exit 1 ;;
            esac ;;
        dfu)
            case "${TOOL:-dfu-util}" in
                cubeprog) require cubeprog; exec STM32_Programmer_CLI -l usb ;;
                dfu-util) require dfu-util; exec dfu-util -l ;;
                *) echo "ERROR: --list for dfu needs --tool cubeprog|dfu-util"; exit 1 ;;
            esac ;;
        uart)
            echo "Serial ports:"
            ls -l /dev/serial/by-id/ 2>/dev/null || ls /dev/ttyUSB* /dev/ttyACM* 2>/dev/null \
                || echo "  (none found)"
            exit 0 ;;
    esac
fi

# --- flash mode --------------------------------------------------------------
[[ -n "$TOOL" ]] || { echo "ERROR: --tool is required for $METHOD (one of: $VALID)"; exit 1; }
[[ " $VALID " == *" $TOOL "* ]] || { echo "ERROR: tool '$TOOL' is not valid for $METHOD (use one of: $VALID)"; exit 1; }
require "$TOOL"

PROJECT_PATH="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NAME="stm32_dyno_firmware_v2"

# The firmware may come from either tree: build-docker/ (Scripts/build-docker.sh)
# or build/ (native or IDE build). Flash whichever image is newer, so "build,
# then flash" does the obvious thing no matter which builder produced it.
BUILD_DIR=""
for candidate in "$PROJECT_PATH/build-docker/$CONFIG" "$PROJECT_PATH/build/$CONFIG"; do
    [[ -f "$candidate/$NAME.elf" ]] || continue
    if [[ -z "$BUILD_DIR" || "$candidate/$NAME.elf" -nt "$BUILD_DIR/$NAME.elf" ]]; then
        BUILD_DIR="$candidate"
    fi
done
[[ -n "$BUILD_DIR" ]] || {
    echo "ERROR: no $CONFIG firmware found in build-docker/$CONFIG/ or build/$CONFIG/."
    echo "       Build first: ./Scripts/build-docker.sh $CONFIG"
    exit 1
}

ELF="$BUILD_DIR/$NAME.elf"
BIN="$BUILD_DIR/$NAME.bin"
echo "Using ${ELF#"$PROJECT_PATH"/}"

[[ "$METHOD" == swd ]] || echo "$METHOD: ensure the board is in bootloader mode (BOOT0 high, then reset)."

cmd=()
case "$METHOD:$TOOL" in
    swd:cubeprog)
        conn=(port=SWD); [[ -n "$SERIAL" ]] && conn+=(sn="$SERIAL")
        cmd=(STM32_Programmer_CLI -c "${conn[@]}" -d "$ELF" -rst) ;;
    swd:st-flash)
        cmd=(st-flash); [[ -n "$SERIAL" ]] && cmd+=(--serial "$SERIAL")
        cmd+=(--reset write "$BIN" 0x08000000) ;;
    swd:openocd)
        cmd=(openocd -f interface/stlink.cfg)
        [[ -n "$SERIAL" ]] && cmd+=(-c "adapter serial $SERIAL")
        cmd+=(-f target/stm32h7x.cfg -c "program $ELF verify reset exit") ;;
    dfu:cubeprog)
        conn=(port=USB"$INDEX"); [[ -n "$SERIAL" ]] && conn+=(sn="$SERIAL")
        cmd=(STM32_Programmer_CLI -c "${conn[@]}" -d "$ELF" -rst) ;;
    dfu:dfu-util)
        cmd=(dfu-util -a 0); [[ -n "$SERIAL" ]] && cmd+=(-S "$SERIAL")
        cmd+=(-s 0x08000000:leave -D "$BIN") ;;
    uart:cubeprog)
        cmd=(STM32_Programmer_CLI -c port="$PORT" br="$BAUD" -d "$ELF" -rst) ;;
    uart:stm32flash)
        cmd=(stm32flash -b "$BAUD" -w "$BIN" -v -g 0x08000000 "$PORT") ;;
esac

echo "Flashing $CONFIG via $TOOL ($METHOD)..."
exec "${cmd[@]}"
