#!/usr/bin/env python3
"""CI drift guard: every committed generated file must match what its schema generates.

For each target it renders from the schema and compares against the committed file as a
token stream (comments, whitespace and trailing-comma style ignored -- see
verify.normalize). Exits non-zero on any drift, names the file, prints the diff, and says
how to fix it. No temp files, no compiler needed.

Covers every generator in this directory, not just generate.py: a file nobody checks is
one that drifts, and the error catalog's descriptions are the kind of thing edited in the
generated file by mistake precisely because they read like prose rather than like code.

    python tools/message_gen/check.py
"""

from __future__ import annotations

import difflib
import sys

import error_msg_generate
import generate
from verify import normalize

# Each generator owns its own targets and its own render(); the fix hint has to name the
# script that actually produces the file, so they are carried together.
GENERATORS = [
    ("tools/message_gen/generate.py", generate.TARGETS, generate.render),
    (
        "tools/message_gen/error_msg_generate.py",
        error_msg_generate.TARGETS,
        error_msg_generate.render,
    ),
]


def main() -> int:
    failed: list[str] = []
    checked = 0
    for script, targets, render in GENERATORS:
        for name in sorted(targets):
            checked += 1
            schema_path, template_name, out_path = targets[name]
            if not out_path.exists():
                print(f"FAIL {name}: {out_path} does not exist")
                failed.append(f"{name} ({script})")
                continue

            committed = normalize(out_path.read_text())
            generated = normalize(render(schema_path, template_name))
            if committed == generated:
                print(f"OK   {name}: {out_path} matches {schema_path.name}")
                continue

            failed.append(f"{name} ({script})")
            print(f"FAIL {name}: {out_path} is out of sync with {schema_path.name}")
            diff = difflib.unified_diff(
                committed, generated,
                f"committed:{out_path.name}", f"from-schema:{schema_path.name}",
                lineterm="",
            )
            print("\n".join(diff))

    if failed:
        print(
            f"\n{len(failed)} file(s) out of sync: {', '.join(failed)}\n"
            "Regenerate and commit:  ./scripts/generate.sh  (runs every generator)",
            file=sys.stderr,
        )
        return 1

    print(f"\nAll {checked} generated file(s) are in sync with their schemas.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
