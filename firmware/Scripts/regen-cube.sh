#!/usr/bin/env bash
# Scripts/regen-cube.sh
# Regenerate the HAL/driver sources and cmake/stm32cubemx/CMakeLists.txt from the
# .ioc by driving STM32CubeMX headlessly — same result as clicking "Generate
# Code" in the GUI. Your USER CODE BEGIN/END blocks and the top-level
# CMakeLists.txt are preserved.
#
# CubeMX is NOT installed by apt/dnf and the download needs a free ST account,
# but running it needs no account. Point this script at the binary you installed.
#
# Usage: ./Scripts/regen-cube.sh [--check] [--cubemx <path>] [--ioc <path>]
#   --check          regenerate, then fail if it changed any tracked file
#                    (drift check for CI: the committed generated code no longer
#                    matches the .ioc). Without it, changes are left in the tree.
#   --cubemx <path>  path to the STM32CubeMX launcher or .jar. Overrides the
#                    $STM32CUBEMX env var and the search of common install dirs.
#   --ioc <path>     .ioc to generate from (default: the project's single .ioc).
#
# The binary is found in this order: --cubemx, $STM32CUBEMX, common install
# locations, then STM32CubeMX on PATH. On a headless host (no $DISPLAY) the run
# is wrapped in xvfb-run, since CubeMX's Java/SWT UI touches the display even in
# script mode; install it (dnf install xorg-x11-server-Xvfb / apt install xvfb).

set -euo pipefail

CHECK=0
CUBEMX="${STM32CUBEMX:-}"
IOC=""

usage() { sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --check)      CHECK=1 ;;
        --cubemx)     CUBEMX="${2:?--cubemx needs a value}"; shift ;;
        --cubemx=*)   CUBEMX="${1#*=}" ;;
        --ioc)        IOC="${2:?--ioc needs a value}"; shift ;;
        --ioc=*)      IOC="${1#*=}" ;;
        -h|--help)    usage; exit 0 ;;
        *)            echo "ERROR: unknown argument '$1' (see --help)"; exit 1 ;;
    esac
    shift
done

PROJECT_PATH="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# --- locate the .ioc ---------------------------------------------------------
if [[ -z "$IOC" ]]; then
    mapfile -t iocs < <(find "$PROJECT_PATH" -maxdepth 1 -name '*.ioc' | sort)
    case "${#iocs[@]}" in
        0) echo "ERROR: no .ioc found in $PROJECT_PATH (pass --ioc <path>)."; exit 1 ;;
        1) IOC="${iocs[0]}" ;;
        *) echo "ERROR: multiple .ioc files found; pick one with --ioc:"; printf '  %s\n' "${iocs[@]}"; exit 1 ;;
    esac
fi
[[ -f "$IOC" ]] || { echo "ERROR: .ioc not found: $IOC"; exit 1; }
IOC="$(cd "$(dirname "$IOC")" && pwd)/$(basename "$IOC")"   # absolutise

# --- locate STM32CubeMX ------------------------------------------------------
if [[ -z "$CUBEMX" ]]; then
    for c in \
        "$HOME/STM32CubeMX/STM32CubeMX" \
        "/opt/STM32CubeMX/STM32CubeMX" \
        "/usr/local/STMicroelectronics/STM32Cube/STM32CubeMX/STM32CubeMX" \
        "$HOME/STM32CubeMX/STM32CubeMX.exe"
    do
        [[ -x "$c" || -f "$c" ]] && { CUBEMX="$c"; break; }
    done
    [[ -z "$CUBEMX" ]] && command -v STM32CubeMX >/dev/null 2>&1 && CUBEMX="$(command -v STM32CubeMX)"
fi
[[ -n "$CUBEMX" && ( -x "$CUBEMX" || -f "$CUBEMX" ) ]] || {
    echo "ERROR: STM32CubeMX not found. Set \$STM32CUBEMX, pass --cubemx <path>,"
    echo "       or install it (needs a free ST account):"
    echo "       https://www.st.com/en/development-tools/stm32cubemx.html"
    exit 1
}

# A .jar is launched via java; a native launcher is run directly.
RUN=()
case "$CUBEMX" in
    *.jar) command -v java >/dev/null 2>&1 || { echo "ERROR: java not found (needed for $CUBEMX)."; exit 1; }
           RUN=(java -jar "$CUBEMX") ;;
    *)     RUN=("$CUBEMX") ;;
esac

# On a headless host CubeMX still needs a display; borrow a virtual one.
if [[ -z "${DISPLAY:-}" ]] && command -v xvfb-run >/dev/null 2>&1; then
    RUN=(xvfb-run -a "${RUN[@]}")
fi

# --- verify the firmware pack the .ioc needs is installed --------------------
# If the HAL/firmware package the .ioc references isn't in the local repository,
# 'config load' hangs for many minutes, silently, while CubeMX tries to fetch it
# (and offline it just never finishes). Fail fast with an actionable message.
REPO="${STM32CUBE_REPO:-$HOME/STM32Cube/Repository}"
PACK="$(sed -n 's/^ProjectManager\.FirmwarePackage=//p' "$IOC" | tr -d '\r' | head -1)"
if [[ -n "$PACK" && ! -d "$REPO/${PACK// /_}" ]]; then
    echo "ERROR: the firmware package this .ioc needs is not installed:"
    echo "         $PACK"
    echo "       expected at: $REPO/${PACK// /_}"
    echo
    echo "Without it, CubeMX hangs in 'config load' trying to download it. Install it first:"
    echo "  headless (downloads from ST — needs internet + a free myST login the first time):"
    echo "     printf 'swmgr install \"%s\" ask\\nexit\\n' \"$PACK\" > /tmp/inst.txt && \\"
    echo "     '$CUBEMX' -q /tmp/inst.txt"
    echo "  from a pre-downloaded pack .zip (no login needed at install time):"
    echo "     printf 'swmgr install /path/to/pack.zip deny\\nexit\\n' > /tmp/inst.txt && '$CUBEMX' -q /tmp/inst.txt"
    echo "  or in the GUI: Help -> Manage embedded software packages -> STM32H7 -> tick it -> Install"
    echo "  (set \$STM32CUBE_REPO if your repository lives elsewhere)"
    if [[ -d "$REPO" ]]; then
        echo "  firmware packs currently installed in $REPO:"
        find "$REPO" -maxdepth 1 -mindepth 1 -type d -name '*_FW_*' -printf '     %f\n' 2>/dev/null | head || echo "     (none)"
    fi
    exit 1
fi

# --- generate ----------------------------------------------------------------
SCRIPT="$(mktemp)"
trap 'rm -f "$SCRIPT"' EXIT
printf 'config load %s\nproject generate\nexit\n' "$IOC" > "$SCRIPT"

echo "Regenerating from ${IOC#"$PROJECT_PATH"/} using ${RUN[*]}..."
"${RUN[@]}" -q "$SCRIPT"

# --- optional drift check ----------------------------------------------------
if [[ $CHECK -eq 1 ]]; then
    # Ignore .mxproject: CubeMX rewrites this bookkeeping file on every generate,
    # so it churns even when no source changed, and it isn't compiled.
    DRIFT_SPEC=(-- . ':(exclude).mxproject')
    if ! git -C "$PROJECT_PATH" diff --quiet "${DRIFT_SPEC[@]}"; then
        echo
        echo "ERROR: regeneration changed committed files — the generated code is"
        echo "       out of date with the .ioc. Regenerate and commit:"
        git -C "$PROJECT_PATH" --no-pager diff --stat "${DRIFT_SPEC[@]}"
        exit 1
    fi
    echo "Drift check passed: generated code matches the .ioc."
else
    echo "Done. Review changes with: git -C ${PROJECT_PATH##*/} diff"
fi
