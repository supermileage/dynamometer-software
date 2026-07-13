#!/usr/bin/env python3
"""CI drift guard: every committed header must match what its schema generates.

For each target it renders the header from the schema and compares it against the
committed file as a C token stream (comments, whitespace and trailing-comma style are
ignored -- see verify.normalize). Exits non-zero on any drift, names the file, prints
the offending diff, and says how to fix it. No temp files, no compiler needed.

    python tools/message_gen/check.py
"""

from __future__ import annotations

import difflib
import sys

from generate import TARGETS, render
from verify import normalize


def main() -> int:
    failed: list[str] = []
    for name in sorted(TARGETS):
        schema_path, template_name, out_path = TARGETS[name]
        if not out_path.exists():
            print(f"FAIL {name}: {out_path} does not exist")
            failed.append(name)
            continue

        committed = normalize(out_path.read_text())
        generated = normalize(render(schema_path, template_name))
        if committed == generated:
            print(f"OK   {name}: {out_path} matches {schema_path.name}")
            continue

        failed.append(name)
        print(f"FAIL {name}: {out_path} is out of sync with {schema_path.name}")
        diff = difflib.unified_diff(
            committed, generated,
            f"committed:{out_path.name}", f"from-schema:{schema_path.name}",
            lineterm="",
        )
        print("\n".join(diff))

    if failed:
        print(
            f"\n{len(failed)} header(s) out of sync: {', '.join(failed)}\n"
            "Regenerate and commit:  python tools/message_gen/generate.py",
            file=sys.stderr,
        )
        return 1

    print(f"\nAll {len(TARGETS)} generated headers are in sync with their schemas.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
