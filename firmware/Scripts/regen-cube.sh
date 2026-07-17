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

# --- provide the firmware pack from the bundled submodule --------------------
# The HAL/firmware pack is vendored as the STM32CubeH7 submodule, pinned to the
# version the .ioc names, so regenerating needs no myST account. CubeMX only
# looks for packs in its own repository, so link the submodule in there under the
# exact directory name it expects. A real pack already installed there wins.
# If no pack is found at all, CubeMX hangs in 'config load' for minutes rather
# than reporting anything, so bail out early instead.
REPO="${STM32CUBE_REPO:-$HOME/STM32Cube/Repository}"
PACK="$(sed -n 's/^ProjectManager\.FirmwarePackage=//p' "$IOC" | tr -d '\r' | head -1)"
PACK_DIR="$REPO/${PACK// /_}"
SUBMOD="$PROJECT_PATH/third_party/STM32CubeH7"

if [[ -n "$PACK" && ! -d "$PACK_DIR" ]]; then
    if [[ -f "$SUBMOD/package.xml" ]]; then
        # STM32CubeH7 is a meta-repo: both Drivers/ and Middlewares/ are nested
        # submodules that a plain clone leaves empty. That is worse than a hang —
        # CubeMX happily generates a project *without* the missing middleware and
        # deletes the committed copies (e.g. all of FreeRTOS). Populate every
        # non-BSP module; the ~40 eval-board BSPs are for boards we do not use.
        if git -C "$SUBMOD" submodule status 2>/dev/null | grep '^-' | grep -qv 'BSP'; then
            echo "Populating STM32CubeH7 sources (Drivers + Middlewares)..."
            mapfile -t sub_paths < <(git -C "$SUBMOD" config -f .gitmodules \
                --get-regexp 'submodule\..*\.path' | awk '{print $2}' | grep -v 'BSP')
            git -C "$SUBMOD" submodule update --init --depth 1 -- "${sub_paths[@]}"
        fi
        mkdir -p "$REPO"
        ln -sfn "$SUBMOD" "$PACK_DIR"
        echo "Using bundled pack: ${PACK_DIR/#$HOME/\~} -> firmware/third_party/STM32CubeH7"
    else
        echo "ERROR: the firmware pack this .ioc needs is not available:"
        echo "         $PACK"
        echo
        echo "It is vendored as a submodule — initialise it:"
        echo "     git submodule update --init firmware/third_party/STM32CubeH7"
        echo "  (or install the pack into $REPO yourself, or set \$STM32CUBE_REPO.)"
        exit 1
    fi
fi

# CubeMX shows a migration prompt when its version differs from the one that
# wrote the .ioc. Headless there is nobody to answer it, so the run just hangs at
# 'config load' with no output — surface the expected version up front.
IOC_MX_VER="$(sed -n 's/^MxCube\.Version=//p' "$IOC" | tr -d '\r' | head -1)"
[[ -n "$IOC_MX_VER" ]] && echo "Note: this .ioc was written by STM32CubeMX $IOC_MX_VER; a different version will stall on a migration prompt."

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
