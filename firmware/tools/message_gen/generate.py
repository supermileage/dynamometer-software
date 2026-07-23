#!/usr/bin/env python3
"""Generate C headers for the USB / message-passing wire protocol from a YAML schema.

Single source of truth: tools/message_gen/schema/*.yaml. Edit the schema, run this,
and the C header is regenerated. Later the same schema can also emit the host-side
parser from a second template, so the two can never drift.

Usage:
    generate.py                      # regenerate every target into the tree
    generate.py --target X           # regenerate just target X
    generate.py --target X --stdout  # print X to stdout (don't touch the tree)
    generate.py --target X --out f   # write X somewhere else (used by the diff check)
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import yaml
from jinja2 import Environment, FileSystemLoader

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
MSG_DIR = REPO / "Core" / "Inc" / "MessagePassing"
CONFIG_DIR = REPO / "Core" / "Inc" / "Config"

# target -> (schema file, template file, default output). One generic template drives
# every header; add the C++ host parser here later as its own (schema, template, out).
TARGETS = {
    "messages_public": (
        HERE / "schema" / "messages_public.yaml",
        "header.h.j2",
        MSG_DIR / "messages_public.h",
    ),
    "messages_private": (
        HERE / "schema" / "messages_private.yaml",
        "header.h.j2",
        MSG_DIR / "messages_private.h",
    ),
    # The sysconfig store's per-parameter validation entries, from the same schema
    # section that defines sysconfig_param_t -- so the enum, the table and the host
    # catalog can never disagree about a parameter's id, kind or range.
    "sysconfig_table": (
        HERE / "schema" / "messages_public.yaml",
        "sysconfig_table.inc.j2",
        CONFIG_DIR / "sysconfig_table.inc",
    ),
}


def _c_float(value) -> str:
    """A YAML number as a C float literal: 1e-06 -> '1e-06f', 1000 -> '1000.0f'."""
    text = f"{float(value):g}"
    if "." not in text and "e" not in text:
        text += ".0"
    return text + "f"


def _annotate_sysconfig(schema: dict) -> None:
    """Computes the C-side fields of each sysconfig_params entry: the enum member name,
    the min/max literals typed to the parameter's kind, and the expression the store
    seeds itself from."""
    for s in schema["sections"]:
        if s.get("kind") != "sysconfig_params":
            continue
        for p in s["params"]:
            p["_enum_name"] = "SYSCFG_" + p["name"]
            if p["type"] == "float":
                p["_c_macro"] = "SYSCFG_F32"
                p["_c_min"] = _c_float(p["min"])
                p["_c_max"] = _c_float(p["max"])
            elif p["type"] == "uint32":
                p["_c_macro"] = "SYSCFG_U32"
                p["_c_min"] = f"{int(p['min'])}u"
                p["_c_max"] = f"{int(p['max'])}u"
            elif p["type"] == "enum":
                # A uint32 restricted to its option codes (contiguous from 0), so the store
                # range-checks it exactly like any other uint32; the labels are host-only.
                p["_c_macro"] = "SYSCFG_U32"
                p["_c_min"] = "0u"
                p["_c_max"] = f"{len(p['options']) - 1}u"
            else:
                raise ValueError(f"sysconfig param {p['name']}: unknown type {p['type']!r}")

            # Most parameters name a config.h #define, which is also compiled into the task
            # that reads them, so the macro is the default and the two cannot drift. A
            # parameter that exists *only* at runtime has no such macro to name -- writing
            # one would invite the idea that rebuilding with it changed something -- so it
            # carries `default:` in the schema and the literal is emitted here instead.
            if "default" in p:
                p["_c_default"] = (
                    _c_float(p["default"])
                    if p["type"] == "float"
                    else f"{int(p['default'])}u"
                )
            else:
                p["_c_default"] = p["name"]


def render(schema_path: Path, template_name: str) -> str:
    schema = yaml.safe_load(schema_path.read_text())
    _annotate_sysconfig(schema)
    env = Environment(
        loader=FileSystemLoader(HERE / "templates"),
        trim_blocks=True,
        lstrip_blocks=True,
        keep_trailing_newline=True,
    )
    return env.get_template(template_name).render(schema_name=schema_path.name, **schema)


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
