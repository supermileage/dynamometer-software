#!/usr/bin/env python3
"""Prove the generated header matches a reference, ignoring comments + whitespace.

Used two ways:
  * one-off faithfulness check against the hand-written header
  * CI drift guard: regenerate to a temp file, then `verify.py committed.h temp.h`
    fails the build if someone hand-edited the generated header instead of the schema.

It compares the two files as C token streams: comments are stripped, all whitespace
(including brace placement) is irrelevant, and a trailing comma before `}` is treated
as optional (it is insignificant in C). An empty diff therefore means the two files
declare the same C -- same types, fields, enum values, sizes and static_asserts --
regardless of formatting, which the hand-written header is not internally consistent
about anyway.
"""

from __future__ import annotations

import difflib
import re
import sys
from pathlib import Path

_BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.DOTALL)
# A C token: string literal, identifier/number, or a single punctuation char.
_TOKEN = re.compile(r'"[^"]*"|[A-Za-z_]\w*|\d+|\S')


def normalize(src: str) -> list[str]:
    src = _BLOCK_COMMENT.sub("", src)
    src = "\n".join(line.split("//", 1)[0] for line in src.splitlines())
    toks = _TOKEN.findall(src)
    out: list[str] = []
    for i, tok in enumerate(toks):
        if tok == "," and i + 1 < len(toks) and toks[i + 1] == "}":
            continue  # drop trailing comma before a closing brace
        out.append(tok)
    return out


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: verify.py <reference.h> <generated.h>", file=sys.stderr)
        return 2
    ref, gen = (normalize(Path(p).read_text()) for p in argv)
    diff = list(difflib.unified_diff(ref, gen, argv[0], argv[1], lineterm=""))
    if diff:
        print("\n".join(diff))
        print(f"\nFAIL: {len(diff)} normalized diff lines", file=sys.stderr)
        return 1
    print(f"OK: semantically identical ({len(ref)} normalized lines)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
