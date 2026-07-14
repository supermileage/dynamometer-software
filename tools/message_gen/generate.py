#!/usr/bin/env python3
"""Generate the C# message-passing / USB wire-protocol types from the firmware's YAML schema.

Single source of truth: the firmware's schema, which lives in this repo at
    firmware/tools/message_gen/schema/messages_public.yaml
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
SCHEMA_DIR = REPO / "firmware" / "tools" / "message_gen" / "schema"
OUT_DIR = REPO / "src" / "Dyno.Core" / "Messages" / "Generated"
# Boot defaults for the sysconfig catalog: each runtime parameter's #define, by the
# same name, in the firmware's config header.
CONFIG_H = REPO / "firmware" / "Core" / "Inc" / "Config" / "config.h"

# target -> (schema file, template file, default output). One generic template drives
# every header; add messages_private the same way if the host ever needs those types.
TARGETS = {
    "messages_public": (
        SCHEMA_DIR / "messages_public.yaml",
        "csharp.cs.j2",
        OUT_DIR / "Messages.cs",
    ),
    # The host-side metadata for every runtime parameter (kind, range, category, unit,
    # description from the schema; boot default from config.h) -- so the app's editor
    # can never disagree with the firmware about a parameter.
    "sysconfig_catalog": (
        SCHEMA_DIR / "messages_public.yaml",
        "sysconfig_catalog.cs.j2",
        REPO / "src" / "Dyno.Core" / "SysConfig" / "Generated" / "SysConfigCatalog.cs",
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


def _cs_double(value) -> str:
    """A YAML number as a C# double literal (repr of a Python float is always one)."""
    return repr(float(value))


def _cs_string(text: str) -> str:
    """A C# string literal."""
    return '"' + str(text).replace("\\", "\\\\").replace('"', '\\"') + '"'


def _config_defaults() -> dict[str, float]:
    """Every simple numeric `#define NAME value` in config.h, as name -> value. These are
    the boot defaults the sysconfig store seeds itself from; baking them into the
    generated catalog keeps config.h the single place a default is written, and the CI
    drift guard flags a config.h change whose catalog wasn't regenerated."""
    defaults: dict[str, float] = {}
    define = re.compile(r"^\s*#define\s+(\w+)\s+([0-9.+-eE]+)[fFuUlL]*\s*(?://.*)?$")
    for line in CONFIG_H.read_text().splitlines():
        m = define.match(line)
        if m:
            try:
                defaults[m.group(1)] = float(m.group(2))
            except ValueError:
                pass  # not a plain numeric literal (an expression or token); not a default
    return defaults


def _prepare_sysconfig(schema: dict, symbols: dict[str, int], defines: list) -> None:
    """Annotates sysconfig_params sections with their C# fields: enum member names and
    positional ids, plus the typed literals the catalog rows need. Also registers the
    derived SYSCFG_PARAM_COUNT constant so it lands in MessageConstants like any
    schema #define."""
    defaults = None
    for s in schema["sections"]:
        if s.get("kind") != "sysconfig_params":
            continue
        defaults = _config_defaults() if defaults is None else defaults
        for i, p in enumerate(s["params"]):
            name = p["name"]
            if name not in defaults:
                raise KeyError(
                    f"sysconfig param {name} has no numeric #define in {CONFIG_H}; "
                    "every runtime parameter needs its boot default there"
                )
            p["_enum_name"] = "SYSCFG_" + name
            p["_csval"] = str(i)
            p["_cs_isfloat"] = "true" if p["type"] == "float" else "false"
            p["_cs_default"] = _cs_double(defaults[name])
            p["_cs_min"] = _cs_double(p["min"])
            p["_cs_max"] = _cs_double(p["max"])
            p["_cs_category"] = _cs_string(p["category"])
            p["_cs_unit"] = _cs_string(p["unit"])
            p["_cs_desc"] = _cs_string(p["description"])
            symbols[p["_enum_name"]] = i
        count = len(s["params"])
        symbols["SYSCFG_PARAM_COUNT"] = count
        defines.append(
            {
                "name": "SYSCFG_PARAM_COUNT",
                "value": f"{count}u",
                "comment": "one past the highest sysconfig_param_t id; sizes the firmware store",
                "_csval": _fmt(count) + "u",
            }
        )


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

    _prepare_sysconfig(schema, symbols, defines)

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
