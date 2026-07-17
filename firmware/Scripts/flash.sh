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
#   Debug|Release   which build to flash (default: Release)
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
#   cmake --build --preset Release    # or ./Scripts/build-docker.sh

set -euo pipefail

CONFIG="Release"; METHOD=""; TOOL=""; SERIAL=""; PORT=""; BAUD="115200"; INDEX="1"; DO_LIST=0

# Print the header comment block above: from line 2 to the blank line that ends it.
usage() { sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

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

# The open-source tools are committed under Scripts/tools/<platform>/ (see its README; refresh them
# with Scripts/get-tools.sh). They are preferred over PATH, so a clone with nothing installed still
# flashes. PATH is the fallback only: a system install is used when nothing is bundled, and neither
# shadows the other by surprise. cubeprog is never bundled (proprietary, no redistribution), so it
# always comes from PATH.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VENDOR_DIR="$SCRIPT_DIR/tools/$(uname -s | tr '[:upper:]' '[:lower:]')-$(uname -m)"
# So a bundled binary finds the libs bundled next to it (st-flash needs libstlink.so.1) with no
# rpath patching. Harmless when nothing is bundled — the directory just isn't there.
export LD_LIBRARY_PATH="$VENDOR_DIR:${LD_LIBRARY_PATH:-}"

# Map a tool keyword to its executable name.
tool_bin() { case "$1" in cubeprog) echo "STM32_Programmer_CLI" ;; *) echo "$1" ;; esac; }
# The bundled copy's absolute path if present, else the bare name for a PATH lookup.
resolve() {
    local b; b="$(tool_bin "$1")"
    if [[ -x "$VENDOR_DIR/$b" ]]; then printf '%s' "$VENDOR_DIR/$b"; else printf '%s' "$b"; fi
}
have()    { command -v "$(resolve "$1")" >/dev/null 2>&1; }

# What to tell the user when a tool is missing. cubeprog is the one we cannot ship (proprietary);
# the OSS tools are bundled for linux-x86_64 / windows-x86_64, so a miss there means some other
# platform, where a package-manager install (no account) is the fix.
install_hint() {
    case "$1" in
        cubeprog)
            echo "  STM32CubeProgrammer is proprietary, so it cannot be bundled with this repo. To use it:"
            echo "    1. Download it from https://www.st.com/en/development-tools/stm32cubeprog.html"
            echo "       (a free ST 'myST' account is required)."
            echo "    2. Add its bin/ directory (containing STM32_Programmer_CLI) to your PATH."
            echo "  Or skip it entirely — every method has a bundled open-source tool that needs no install:"
            echo "    swd -> --tool st-flash,  dfu -> --tool dfu-util,  uart -> --tool stm32flash." ;;
        openocd)
            echo "  openocd is not bundled. Install it (no account needed) and put it on your PATH:"
            echo "    dnf install openocd  ·  apt install openocd  ·  brew install openocd  ·  or an xpack build."
            echo "  Or use the bundled swd tool instead: --tool st-flash." ;;
        st-flash|st-info)
            echo "  st-flash (stlink) ships bundled for linux-x86_64 and windows-x86_64, but not for your platform."
            echo "  Install it yourself (no account needed) and put it on your PATH:"
            echo "    dnf install stlink  ·  apt install stlink-tools  ·  brew install stlink." ;;
        dfu-util)
            echo "  dfu-util ships bundled for linux-x86_64 and windows-x86_64, but not for your platform."
            echo "  Install it yourself (no account needed) and put it on your PATH:"
            echo "    dnf install dfu-util  ·  apt install dfu-util  ·  brew install dfu-util." ;;
        stm32flash)
            echo "  stm32flash ships bundled for linux-x86_64 and windows-x86_64, but not for your platform."
            echo "  Install it yourself (no account needed) and put it on your PATH:"
            echo "    dnf install stm32flash  ·  apt install stm32flash  ·  brew install stm32flash." ;;
        *)
            echo "  Install '$1' and put it on your PATH (see Scripts/README.md)." ;;
    esac
}
require() {
    have "$1" && return 0
    echo "ERROR: '$(tool_bin "$1")' is not available (not bundled for this platform, not on PATH)."
    install_hint "$1"
    exit 1
}

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
                cubeprog) require cubeprog; exec "$(resolve cubeprog)" -l ;;
                st-flash) require st-flash; have st-info && exec "$(resolve st-info)" --probe || exec "$(resolve st-flash)" --list ;;
                openocd)  echo "openocd has no list mode; use: $0 swd --tool st-flash --list"; exit 0 ;;
                *) echo "ERROR: --list for swd needs --tool cubeprog|st-flash"; exit 1 ;;
            esac ;;
        dfu)
            case "${TOOL:-dfu-util}" in
                cubeprog) require cubeprog; exec "$(resolve cubeprog)" -l usb ;;
                dfu-util) require dfu-util; exec "$(resolve dfu-util)" -l ;;
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
        cmd=("$(resolve cubeprog)" -c "${conn[@]}" -d "$ELF" -rst) ;;
    swd:st-flash)
        cmd=("$(resolve st-flash)"); [[ -n "$SERIAL" ]] && cmd+=(--serial "$SERIAL")
        cmd+=(--reset write "$BIN" 0x08000000) ;;
    swd:openocd)
        cmd=("$(resolve openocd)" -f interface/stlink.cfg)
        [[ -n "$SERIAL" ]] && cmd+=(-c "adapter serial $SERIAL")
        cmd+=(-f target/stm32h7x.cfg -c "program $ELF verify reset exit") ;;
    dfu:cubeprog)
        conn=(port=USB"$INDEX"); [[ -n "$SERIAL" ]] && conn+=(sn="$SERIAL")
        cmd=("$(resolve cubeprog)" -c "${conn[@]}" -d "$ELF" -rst) ;;
    dfu:dfu-util)
        cmd=("$(resolve dfu-util)" -a 0); [[ -n "$SERIAL" ]] && cmd+=(-S "$SERIAL")
        cmd+=(-s 0x08000000:leave -D "$BIN") ;;
    uart:cubeprog)
        cmd=("$(resolve cubeprog)" -c port="$PORT" br="$BAUD" -d "$ELF" -rst) ;;
    uart:stm32flash)
        cmd=("$(resolve stm32flash)" -b "$BAUD" -w "$BIN" -v -g 0x08000000 "$PORT") ;;
esac

echo "Flashing $CONFIG via $TOOL ($METHOD)..."

# dfu-util with ':leave' (the dfu:dfu-util case above) tells the board to exit DFU and boot the app
# the instant the download completes -- so dfu-util's final get_status read hits a device that is
# already gone and it exits non-zero, even though the write itself succeeded ("File downloaded
# successfully"). That is the one tool/exit combination where a good flash looks like a failure, so
# special-case it: report success when the download is confirmed, but keep the real exit code for a
# genuine failure -- which never prints that line. Every other tool execs and stands by its own code.
if [[ "$METHOD:$TOOL" == dfu:dfu-util ]]; then
    out=$(mktemp)
    set +e
    "${cmd[@]}" 2>&1 | tee "$out"
    status=${PIPESTATUS[0]}
    set -e
    if [[ $status -ne 0 ]] && grep -q "File downloaded successfully" "$out"; then
        echo
        echo "Note: dfu-util exited $status on its post-download status read, which fails because"
        echo "':leave' already reset the board out of DFU. The 'File downloaded successfully' above is"
        echo "dfu-util confirming the write -- this flash succeeded."
        status=0
    fi
    rm -f "$out"
    exit "$status"
fi

exec "${cmd[@]}"
