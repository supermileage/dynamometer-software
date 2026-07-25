#!/usr/bin/env python3
"""Render the application icon: the same "D" badge the title bar draws.

The badge in MainWindow.axaml is markup, not an asset, so the window icon cannot simply
reference it -- it has to be redrawn as a bitmap. That means two copies of one mark, and
copies drift. The constants below are that mark's spec, transcribed from the XAML, and the
comment beside each names the attribute it mirrors; change the badge and change these
together, then re-run. The letter is rendered with the very font Avalonia lays the badge
out in -- Inter Bold, carved straight out of the Avalonia.Fonts.Inter assembly -- so the
glyph is the glyph, not a lookalike.

Usage: scripts/generate-icon.sh [--preview]
"""

from __future__ import annotations

import argparse
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

# --- The badge, as MainWindow.axaml draws it -------------------------------------------
# <Border Width="22" Height="22" CornerRadius="6"> with a LinearGradientBrush and a
# bold white "D" at FontSize 13. Everything is kept as a ratio of the 22px box so the
# same proportions hold at every icon size.
BADGE = 22.0  # Border Width/Height
CORNER_RADIUS = 6.0 / BADGE  # Border CornerRadius
FONT_SIZE = 13.0 / BADGE  # TextBlock FontSize
LETTER = "D"  # TextBlock Text
LETTER_COLOR = (255, 255, 255)  # TextBlock Foreground="White"
# LinearGradientBrush StartPoint="0%,0%" EndPoint="100%,100%", stops at offset 0 and 1.
GRADIENT_FROM = (0x4C, 0x8D, 0xFF)
GRADIENT_TO = (0x63, 0x66, 0xF1)

# Sizes Windows picks between for the taskbar, alt-tab, explorer and the title bar, plus
# the 256 that shows up in large-icon views. Each is drawn at its own size rather than
# resampled from one master, so the corner radius stays true down at 16px.
ICON_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)

SUPERSAMPLE = 8  # draw this many times larger, then average down for the antialiasing

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_OUT = ROOT / "src" / "Dyno.App" / "Assets" / "dyno.ico"


def find_inter_bold() -> bytes:
    """Carve Inter Bold out of the Avalonia.Fonts.Inter assembly.

    The app calls .WithInterFont(), so this is the exact file Avalonia renders the badge
    with. Avalonia ships the .ttf embedded in the DLL rather than beside it, hence the
    carving: find the sfnt headers, size each font from its table directory, and keep the
    one whose name table says "Inter Bold". Vendoring a copy of the font instead would be
    one more thing that can quietly fall out of step with the package.
    """
    packages = Path.home() / ".nuget" / "packages" / "avalonia.fonts.inter"
    candidates = sorted(packages.glob("*/lib/net*/Avalonia.Fonts.Inter.dll"))
    if not candidates:
        sys.exit(
            f"no Avalonia.Fonts.Inter assembly under {packages}\n"
            "run a `dotnet restore` first so the package is in the nuget cache"
        )
    blob = candidates[-1].read_bytes()

    for font in _sfnt_fonts(blob):
        if _full_name(font) == "Inter Bold":
            return font
    sys.exit(f"no 'Inter Bold' font found inside {candidates[-1]}")


def _sfnt_fonts(blob: bytes):
    """Yield every TrueType font embedded in blob, longest-plausible first."""
    offset = -1
    while True:
        offset = blob.find(b"\x00\x01\x00\x00", offset + 1)
        if offset < 0:
            return
        header = _read_sfnt(blob, offset)
        if header is not None:
            yield header


def _read_sfnt(blob: bytes, offset: int) -> bytes | None:
    """Return the font starting at offset, or None if that is not really a font there.

    A bare \\x00\\x01\\x00\\x00 turns up all over a DLL, so a match only counts once the
    table directory behind it parses: ASCII tags, the two tables every font must have, and
    extents that stay inside the file.
    """
    if offset + 12 > len(blob):
        return None
    (table_count,) = struct.unpack(">H", blob[offset + 4 : offset + 6])
    directory = offset + 12
    if not 5 <= table_count <= 64 or directory + 16 * table_count > len(blob):
        return None

    tags, end = set(), 0
    for i in range(table_count):
        tag, _checksum, table_at, length = struct.unpack(
            ">4sIII", blob[directory + 16 * i : directory + 16 * i + 16]
        )
        if not all(0x20 <= byte < 0x7F for byte in tag):
            return None
        tags.add(tag)
        end = max(end, table_at + length)
    if not {b"head", b"cmap", b"name"} <= tags or offset + end > len(blob):
        return None
    return blob[offset : offset + end]


def _full_name(font: bytes) -> str | None:
    """The font's name-table entry 4 ("full name"), e.g. "Inter Bold"."""
    (table_count,) = struct.unpack(">H", font[4:6])
    for i in range(table_count):
        tag, _checksum, table_at, length = struct.unpack(">4sIII", font[12 + 16 * i : 28 + 16 * i])
        if tag != b"name":
            continue
        names = font[table_at : table_at + length]
        record_count, strings_at = struct.unpack(">HH", names[2:6])
        for record in range(record_count):
            platform, _encoding, _language, name_id, size, name_at = struct.unpack(
                ">6H", names[6 + 12 * record : 18 + 12 * record]
            )
            if name_id != 4:
                continue
            raw = names[strings_at + name_at : strings_at + name_at + size]
            return raw.decode("utf-16-be" if platform == 3 else "latin-1")
    return None


def render(size: int, font_file: bytes) -> Image.Image:
    """Draw the badge at size x size pixels."""
    canvas = size * SUPERSAMPLE

    # The gradient runs corner to corner, so a pixel's position along it is how far it
    # sits along (1,1) -- that is (x + y) / 2 once both are fractions of the canvas.
    # Avalonia interpolates its stops in sRGB, which is what a plain byte-wise blend does.
    gradient = Image.new("RGB", (canvas, canvas))
    pixels = gradient.load()
    for y in range(canvas):
        for x in range(canvas):
            along = (x + y) / (2 * (canvas - 1))
            pixels[x, y] = tuple(
                round(start + (stop - start) * along)
                for start, stop in zip(GRADIENT_FROM, GRADIENT_TO)
            )

    mask = Image.new("L", (canvas, canvas), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (0, 0, canvas - 1, canvas - 1), radius=CORNER_RADIUS * canvas, fill=255
    )

    badge = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    badge.paste(gradient, mask=mask)

    _draw_letter(badge, canvas, font_file)
    return badge.resize((size, size), Image.LANCZOS)


def _draw_letter(badge: Image.Image, canvas: int, font_file: bytes) -> None:
    """Centre the "D" on its ink, not on its advance width.

    Avalonia centres the TextBlock's layout box, which includes the glyph's side bearings
    and the line's ascent and descent. For Inter Bold's "D" those happen to cancel out to
    within a fiftieth of a pixel at 22px, so centring the ink lands where the badge already
    puts it -- and stays put if the letter is ever changed to one whose bearings do not.
    """
    import io

    font = ImageFont.truetype(io.BytesIO(font_file), FONT_SIZE * canvas)
    left, top, right, bottom = font.getbbox(LETTER)
    ImageDraw.Draw(badge).text(
        ((canvas - (left + right)) / 2, (canvas - (top + bottom)) / 2),
        LETTER,
        font=font,
        fill=LETTER_COLOR,
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT, help="path of the .ico to write")
    parser.add_argument(
        "--preview", action="store_true", help="also write a 256px .png beside the .ico"
    )
    args = parser.parse_args()

    font_file = find_inter_bold()
    frames = [render(size, font_file) for size in ICON_SIZES]

    args.out.parent.mkdir(parents=True, exist_ok=True)
    # Pillow rescales `sizes` out of the image it is given, so hand it the largest frame
    # and append the rest -- that way every size is one this script drew at that size.
    frames[-1].save(args.out, format="ICO", sizes=[(s, s) for s in ICON_SIZES], append_images=frames)
    print(f"wrote {args.out} ({', '.join(f'{s}x{s}' for s in ICON_SIZES)})")

    if args.preview:
        preview = args.out.with_suffix(".png")
        frames[-1].save(preview)
        print(f"wrote {preview}")


if __name__ == "__main__":
    main()
