#!/usr/bin/env bash
# Scripts/get-tools.sh
# Regenerate the bundled flashing tools under Scripts/tools/<platform>/. These binaries are already
# committed, so END USERS DO NOT RUN THIS — a clone already has them, and flash.sh uses them ahead
# of PATH (see its resolve()). This is the maintainer's tool for adding a platform or bumping a
# version: it downloads the pinned releases, verifies each SHA-256, and lays out the directory for
# you to commit.
#
# What this fetches (Linux x86_64):
#   st-flash / st-info   SWD     from the stlink release .deb (BSD-3-Clause)
#   dfu-util             USB DFU from the Debian package       (GPL-2.0+)
#   stm32flash           UART    built from source             (GPL-2.0)
#
# NOT fetched: STM32CubeProgrammer (cubeprog) — proprietary, ST forbids redistribution, and every
# method already has an open-source tool above. Install it yourself if you want it; flash.sh finds
# it on PATH.
#
# Each download is pinned to a version and checked against a SHA-256, so a supplier swapping a file
# under the URL fails loudly instead of running. Windows has its own downloader: get-tools.ps1.
#
# Usage: ./Scripts/get-tools.sh [--force]
#   --force   re-download even if the tools are already present

set -euo pipefail

FORCE=0
[[ "${1:-}" == "--force" || "${1:-}" == "-f" ]] && FORCE=1

OS="$(uname -s | tr '[:upper:]' '[:lower:]')"
ARCH="$(uname -m)"
if [[ "$OS" != "linux" || "$ARCH" != "x86_64" ]]; then
    echo "get-tools.sh covers Linux x86_64. For Windows use get-tools.ps1;"
    echo "on '$OS-$ARCH', install the tools from your package manager (see Scripts/README.md)."
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="$SCRIPT_DIR/tools/$OS-$ARCH"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

for t in curl sha256sum dpkg-deb tar make gcc; do
    command -v "$t" >/dev/null 2>&1 || { echo "ERROR: '$t' is required but not installed."; exit 1; }
done

mkdir -p "$DEST"

# fetch <url> <sha256> <outfile>
fetch() {
    local url="$1" sha="$2" out="$3"
    echo "  downloading $(basename "$out")"
    curl -fsSL --retry 3 -m 300 "$url" -o "$out"
    echo "$sha  $out" | sha256sum -c - >/dev/null \
        || { echo "ERROR: checksum mismatch for $url"; echo "       expected $sha"; echo "       got      $(sha256sum "$out" | cut -d' ' -f1)"; exit 1; }
}

# ---- st-flash + st-info (from the stlink release .deb) ----------------------
if [[ $FORCE -eq 1 || ! -x "$DEST/st-flash" ]]; then
    echo "st-flash / st-info (stlink 1.8.0):"
    fetch \
        "https://github.com/stlink-org/stlink/releases/download/v1.8.0/stlink_1.8.0-1_amd64.deb" \
        "293916cbd51f08e45585cf97a2d0add7febdb669931a576b9da28fdda23f9c45" \
        "$WORK/stlink.deb"
    dpkg-deb -x "$WORK/stlink.deb" "$WORK/stlink"
    cp "$WORK/stlink/usr/bin/st-flash" "$WORK/stlink/usr/bin/st-info" "$DEST/"
    # st-flash links libstlink.so.1; ship it alongside and give it the soname flash.sh's
    # LD_LIBRARY_PATH will look for.
    cp "$WORK"/stlink/usr/lib/libstlink.so.* "$DEST/"
    ( cd "$DEST" && ln -sf libstlink.so.1.* libstlink.so.1 )
    chmod +x "$DEST/st-flash" "$DEST/st-info"
fi

# ---- dfu-util (from the Debian package) -------------------------------------
if [[ $FORCE -eq 1 || ! -x "$DEST/dfu-util" ]]; then
    echo "dfu-util (0.11-3):"
    # deb.debian.org serves current packages; if this 404s later, bump the version (and sha) or use
    # the immutable snapshot.debian.org mirror.
    fetch \
        "https://deb.debian.org/debian/pool/main/d/dfu-util/dfu-util_0.11-3_amd64.deb" \
        "bd7cb51a089998678d02e0327fd5886b6e779d7b7f7b729c162a1e2d42eb0987" \
        "$WORK/dfu.deb"
    dpkg-deb -x "$WORK/dfu.deb" "$WORK/dfu"
    cp "$WORK/dfu/usr/bin/dfu-util" "$DEST/"
    chmod +x "$DEST/dfu-util"
fi

# ---- stm32flash (built from source — no libusb, trivial) --------------------
if [[ $FORCE -eq 1 || ! -x "$DEST/stm32flash" ]]; then
    echo "stm32flash (0.7, from source):"
    fetch \
        "https://sourceforge.net/projects/stm32flash/files/stm32flash-0.7.tar.gz/download" \
        "c4c9cd8bec79da63b111d15713ef5cc2cd947deca411d35d6e3065e227dc414a" \
        "$WORK/stm32flash.tar.gz"
    tar xzf "$WORK/stm32flash.tar.gz" -C "$WORK"
    make -C "$WORK/stm32flash-0.7" >/dev/null
    cp "$WORK/stm32flash-0.7/stm32flash" "$DEST/"
    chmod +x "$DEST/stm32flash"
fi

echo
echo "Done. Bundled in tools/$OS-$ARCH/:"
for b in st-flash st-info dfu-util stm32flash; do
    [[ -x "$DEST/$b" ]] && echo "  ✓ $b" || echo "  ✗ $b (missing)"
done
echo
echo "flash.sh now uses these before anything on PATH. cubeprog is not bundled (proprietary)."
