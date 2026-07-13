#!/usr/bin/env python3
"""Generate the C# message-passing / USB wire-protocol types from the firmware's YAML schema.

Single source of truth: the firmware's schema, which lives in this repo at
    stm32_dyno_firmware_v2/tools/message_gen/schema/messages_public.yaml
The firmware renders that same schema to a C header (messages_public.h); this script
renders it to C# (src/Dyno.Core/Messages/Generated/Messages.cs) so the host and the
firmware can never drift.

Why the values are evaluated instead of copied verbatim: the schema's #define / enum
expressions are C (e.g. `0xFFFFu << TASK_OFFSET_SHIFT`). C and C# disagree on integer
suffixes and shift-operand types, so a verbatim copy would not compile. Each constant
expression is therefore evaluated here (small, trusted, schema-only inputs) and emitted
as a concrete C# literal, with the original C expression preserved as a comment.

C-only `code` sections (extern "C" guards, the firmware populate helper, the
usb_frame_crc16 body) are skipped; the CRC is re-implemented once in Dyno.Core using
the generated USB_FRAME_CRC_* constants.

Usage:
    generate.py                      # regenerate every target into the tree
    generate.py --target X           # regenerate just target X
    generate.py --target X --stdout  # print X to stdout (don't touch the tree)
    generate.py --target X --out f   # write X somewhere else (used by the diff check)
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

import yaml
from jinja2 import Environment, FileSystemLoader

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
SCHEMA_DIR = REPO / "stm32_dyno_firmware_v2" / "tools" / "message_gen" / "schema"
OUT_DIR = REPO / "src" / "Dyno.Core" / "Messages" / "Generated"

# target -> (schema file, template file, default output). One generic template drives
# every header; add messages_private the same way if the host ever needs those types.
TARGETS = {
    "messages_public": (
        SCHEMA_DIR / "messages_public.yaml",
        "csharp.cs.j2",
        OUT_DIR / "Messages.cs",
    ),
}

# C type -> C# type for struct fields. Names not in the map (e.g. task_offset_t,
# usb_msg_type_t) are generated enums and pass through unchanged.
_CS_TYPES = {
    "uint8_t": "byte", "int8_t": "sbyte",
    "uint16_t": "ushort", "int16_t": "short",
    "uint32_t": "uint", "int32_t": "int",
    "uint64_t": "ulong", "int64_t": "long",
    "int": "int", "float": "float", "double": "double",
}
# C enum base -> C# enum base.
_CS_BASE = {
    "uint8_t": "byte", "int8_t": "sbyte",
    "uint16_t": "ushort", "int16_t": "short",
    "uint32_t": "uint", "int32_t": "int",
}

# An integer literal with a C suffix (123u, 0xFFFFUL): drop the suffix so Python can eval it.
_SUFFIX = re.compile(r"\b(0[xX][0-9a-fA-F]+|\d+)[uUlL]+")
_SIZEOF = re.compile(r"\s*sizeof\(\s*(\w+)\s*\)\s*==\s*(.+)")


def _eval_c(expr: str, symbols: dict[str, int]) -> int:
    """Evaluate a C constant integer expression against already-defined symbols."""
    return int(eval(_SUFFIX.sub(r"\1", str(expr)), {"__builtins__": {}}, dict(symbols)))


def _fmt(val: int) -> str:
    """Format an unsigned int as a readable C# literal (hex for mask-sized values)."""
    return f"0x{val:X}" if val >= 256 else str(val)


def prepare(schema: dict) -> tuple[list, list]:
    """Annotate the schema in place with computed C# literals/types and return
    (defines, sizes) for the template. `symbols` accumulates every #define and enum
    value so later expressions can reference earlier ones, exactly as C does."""
    symbols: dict[str, int] = {}
    defines: list = []

    for s in schema["sections"]:
        kind = s.get("kind")
        if kind == "define":
            val = _eval_c(s["value"], symbols)
            symbols[s["name"]] = val
            s["_csval"] = _fmt(val) + "u"
            defines.append(s)
        elif kind == "enum":
            current = -1
            for v in s["values"]:
                if v.get("value") is not None:
                    current = _eval_c(v["value"], symbols)
                else:
                    current += 1  # C enum auto-increment
                v["_csval"] = _fmt(current)
                symbols[v["name"]] = current
            s["_csbase"] = _CS_BASE[s["base"]]
        elif kind == "struct":
            for f in s["fields"]:
                if f.get("array"):
                    raise NotImplementedError(
                        f"array struct fields are not supported by the C# target yet: "
                        f"{s['name']}.{f['name']}"
                    )
                f["_cstype"] = _CS_TYPES.get(f["type"], f["type"])

    sizes: list = []
    for s in schema["sections"]:
        if s.get("kind") == "static_assert":
            m = _SIZEOF.match(s["expr"])
            if m:
                sizes.append((m.group(1), _eval_c(m.group(2), symbols)))
    return defines, sizes


def render(schema_path: Path, template_name: str) -> str:
    schema = yaml.safe_load(schema_path.read_text())
    defines, sizes = prepare(schema)
    env = Environment(
        loader=FileSystemLoader(HERE / "templates"),
        trim_blocks=True,
        lstrip_blocks=True,
        keep_trailing_newline=True,
    )
    return env.get_template(template_name).render(
        schema_name=schema_path.name, defines=defines, sizes=sizes, **schema
    )


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--target", choices=sorted(TARGETS),
                    help="only this target (default: all)")
    ap.add_argument("--stdout", action="store_true", help="print instead of writing")
    ap.add_argument("--out", type=Path, help="override output path (single target only)")
    args = ap.parse_args(argv)

    targets = [args.target] if args.target else sorted(TARGETS)
    if (args.stdout or args.out) and len(targets) != 1:
        ap.error("--stdout/--out require a single --target")

    for name in targets:
        schema_path, template_name, default_out = TARGETS[name]
        text = render(schema_path, template_name)
        if args.stdout:
            sys.stdout.write(text)
            continue
        out = args.out or default_out
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(text)
        print(f"wrote {out} ({len(text.splitlines())} lines)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
