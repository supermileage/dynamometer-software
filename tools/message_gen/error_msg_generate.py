#!/usr/bin/env python3
"""Generate the C# catalog of every fault the firmware can report, from the same schema.

The event log used to print faults as `TASK #3`. The number is task-local, so it means
nothing on its own, and what a reader actually wants -- what broke, and what it means for
the run in front of them -- lived in prose hand-written in the app, for the handful of
codes someone had got round to. This renders all of it from
    firmware/tools/message_gen/schema/messages_public.yaml
into src/Dyno.Core/Messages/Generated/ErrorCatalog.cs, so the sentence a user reads is
written once, next to the code it belongs to, by whoever added the fault.

Kept separate from generate.py deliberately. That script renders the *wire contract* --
the constants, enums and packed structs both sides must agree on byte for byte -- and it
is C-shaped: every value is a C expression it has to evaluate. This one renders
documentation: the fault enums flattened into rows of packed code + name + prose, which
is a different shape (one row per enum *value*, across enums) and answers to the UI
rather than to the protocol. The C-expression evaluator and the string-literal helper are
imported from generate.py rather than copied, so the two cannot disagree about what a
schema value means.

Two schema keys drive it, both required on a fault enum and both checked here (see
_faults): the section's `task:` and each value's `description:`.

Usage:
    error_msg_generate.py                        # regenerate into the tree
    error_msg_generate.py --stdout               # print instead (don't touch the tree)
    error_msg_generate.py --out f                # write somewhere else (the diff check)
    error_msg_generate.py --target error_catalog # a target belonging to a sibling
                                                 # generator is skipped, not an error
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

import yaml
from jinja2 import Environment, FileSystemLoader

from generate import _cs_string, _eval_c

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
SCHEMA_DIR = REPO / "firmware" / "tools" / "message_gen" / "schema"
OUT_DIR = REPO / "src" / "Dyno.Core" / "Messages" / "Generated"

# Same shape as generate.py's TARGETS, so check.py can drive both generators through one
# loop: target -> (schema file, template file, default output).
TARGETS = {
    "error_catalog": (
        SCHEMA_DIR / "messages_public.yaml",
        "error_catalog.cs.j2",
        OUT_DIR / "ErrorCatalog.cs",
    ),
}

# A fault enum is named for its task; the values inside it are named for their severity.
_FAULT_ENUM = re.compile(r"_error_ids$")
_SEVERITY = re.compile(r"^(ERROR|WARNING)_")


def _symbols(schema: dict) -> dict[str, int]:
    """Every #define and enum value in the schema, by name. Built in file order because
    later expressions reference earlier ones (`WARNING_FLAG`, `TASK_OFFSET_SHIFT`),
    exactly as C does -- the same reason generate.py accumulates as it walks."""
    symbols: dict[str, int] = {}
    for section in schema["sections"]:
        kind = section.get("kind")
        if kind == "define":
            symbols[section["name"]] = _eval_c(section["value"], symbols)
        elif kind == "enum":
            current = -1
            for value in section["values"]:
                if value.get("value") is not None:
                    current = _eval_c(value["value"], symbols)
                else:
                    current += 1  # C enum auto-increment
                symbols[value["name"]] = current
    return symbols


def _faults(schema: dict, symbols: dict[str, int]) -> list[dict]:
    """One row per fault: the packed code the firmware sends, and what to say about it.

    Everything this rejects is a fault that would otherwise reach a user as a bare number
    with nothing to look it up in -- which is the whole problem the catalog exists to
    solve, so it is worth failing the build over rather than emitting a blank row."""
    warning_flag = symbols["WARNING_FLAG"]
    rows: list[dict] = []

    for section in schema["sections"]:
        if section.get("kind") != "enum" or not _FAULT_ENUM.search(section["name"]):
            continue
        name = section["name"]
        task = section.get("task")
        if task is None:
            raise KeyError(
                f"fault enum {name} has no `task:`. Its numbers are task-local, so without "
                f"the task_offset_t they belong to there is no way to tell {name}'s #1 from "
                "any other task's"
            )
        if task not in symbols:
            raise KeyError(f"fault enum {name}: `task: {task}` is not a task_offset_t value")

        for value in section["values"]:
            member = value["name"]
            if "description" not in value:
                raise KeyError(
                    f"{member} has no `description:`. It is what the app prints beside the "
                    "fault; without one the event log can only show the number"
                )
            severity = _SEVERITY.match(member)
            if severity is None:
                raise ValueError(
                    f"{member} starts with neither ERROR_ nor WARNING_; the app reads the "
                    "prefix as the severity, so a fault has to declare one"
                )
            is_warning = bool(symbols[member] & warning_flag)
            if is_warning != (severity.group(1) == "WARNING"):
                raise ValueError(
                    f"{member} says {severity.group(1)} but its value "
                    f"{'has' if is_warning else 'does not have'} the warning flag set. One of "
                    "the two is wrong, and the wire value is what the app believes"
                )
            rows.append(
                {
                    # The full 32-bit code as it arrives from the board, which is what makes
                    # this a lookup key: the offset disambiguates numbers reused across tasks.
                    "code": f"0x{symbols[task] | symbols[member]:X}u",
                    "task": task,
                    # Without the severity prefix: the line already reads ERR or WARN, and
                    # repeating it in the name buys nothing.
                    "name": _cs_string(_SEVERITY.sub("", member)),
                    "is_warning": "true" if is_warning else "false",
                    "description": _cs_string(value["description"]),
                    "enum_name": name,
                    "member": member,
                }
            )
    return rows


def render(schema_path: Path, template_name: str) -> str:
    schema = yaml.safe_load(schema_path.read_text())
    faults = _faults(schema, _symbols(schema))
    env = Environment(
        loader=FileSystemLoader(HERE / "templates"),
        trim_blocks=True,
        lstrip_blocks=True,
        keep_trailing_newline=True,
    )
    return env.get_template(template_name).render(
        schema_name=schema_path.name, faults=faults
    )


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--target", help="only this target (default: all of this script's)")
    ap.add_argument("--stdout", action="store_true", help="print instead of writing")
    ap.add_argument("--out", type=Path, help="override output path (single target only)")
    args = ap.parse_args(argv)

    if args.target and args.target not in TARGETS:
        # The wrapper scripts hand the same arguments to every generator, so being asked
        # for someone else's target is the ordinary way of being told "not you".
        print(f"{Path(__file__).name}: no target named {args.target!r} here; skipping")
        return 0

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
