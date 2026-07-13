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
}


def render(schema_path: Path, template_name: str) -> str:
    schema = yaml.safe_load(schema_path.read_text())
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
