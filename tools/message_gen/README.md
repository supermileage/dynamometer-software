---
module: message_gen
summary: Generates the C# wire-protocol types from the firmware's YAML schema, so the host and firmware can't drift.
code:
  - tools/message_gen/generate.py
  - tools/message_gen/error_msg_generate.py
  - tools/message_gen/templates/csharp.cs.j2
  - tools/message_gen/templates/error_catalog.cs.j2
  - tools/message_gen/verify.py
  - tools/message_gen/check.py
related: [Dyno.Core]
---

# message_gen

The USB / message-passing wire contract is defined once, as a YAML schema, in the
**firmware** repo. That repo renders it to a C header (`messages_public.h`); this tool
renders the **same schema** to C# so the PC software and the firmware can never disagree
about the protocol.

```
firmware/tools/message_gen/schema/messages_public.yaml
        │  (read from the firmware tree — the single source of truth)
        ├─ generate.py ─────────── templates/csharp.cs.j2
        │                          src/Dyno.Core/Messages/Generated/Messages.cs
        │                          templates/sysconfig_catalog.cs.j2
        │                          src/Dyno.Core/SysConfig/Generated/SysConfigCatalog.cs
        └─ error_msg_generate.py ─ templates/error_catalog.cs.j2
                                   src/Dyno.Core/Messages/Generated/ErrorCatalog.cs
                                   (all committed, all CI-checked)
```

Two generators, because they render two different things. `generate.py` renders the
**wire contract** — the constants, enums and packed structs both sides must agree on byte
for byte — and every value in it is a C expression it has to evaluate.
`error_msg_generate.py` renders **documentation**: the fault enums flattened into rows of
packed code + name + prose, one row per enum *value* across enums, answering to the UI
rather than to the protocol. It imports the C-expression evaluator and the string-literal
helper from `generate.py` rather than copying them, so the two cannot come to disagree
about what a schema value means.

## Usage

```sh
# one-time: deps (Jinja2 + PyYAML)
python3 -m venv .venv && .venv/bin/pip install -r requirements.txt

../../scripts/generate.sh                                       # every generator (usual case)
.venv/bin/python generate.py                                    # just the wire contract
.venv/bin/python error_msg_generate.py                          # just the fault catalog
.venv/bin/python generate.py --target messages_public --stdout  # preview, don't write
.venv/bin/python check.py                                       # CI drift guard (no temp files)
```

`scripts/generate.sh` (and `generate.ps1`) hand the same arguments to every generator;
each skips a `--target` it does not own, so `--target error_catalog --stdout` reaches the
one that has it.

The schema lives in the firmware tree at `firmware/tools/message_gen/schema/`.
After a protocol change, rerun the generators and commit what they wrote.

## What it emits

The C# target emits the **data contract only**:

| schema kind     | emitted C#                                                              |
|-----------------|------------------------------------------------------------------------|
| `define`        | `public const uint` field on `MessageConstants`                        |
| `enum`          | `public enum Name : <base>` (values evaluated to literals)             |
| `struct`        | `[StructLayout(Sequential[, Pack = 1])] public struct`                 |
| `static_assert` | an entry in `MessageContract.ExpectedSizes` (asserted by the tests)    |
| `comment`       | `//` / `///` lines                                                     |
| `code`          | **skipped** (see below)                                                |

### Why values are evaluated, not copied

The schema's expressions are C (`0xFFFFu << TASK_OFFSET_SHIFT`, `WARNING_FLAG`).
C and C# disagree on integer suffixes and shift-operand types, so a verbatim copy would
not compile. `generate.py` evaluates each constant expression (trusted, schema-only
input) against the running symbol table — replicating C's enum auto-increment — and emits
a concrete C# literal, keeping the original C expression as a trailing comment.

### The fault catalog

`ErrorCatalog.cs` is one `ErrorMessageDef` per value of every `*_error_ids` enum: the
packed 32-bit code as it arrives from the board, the task, the schema name without its
severity prefix, and the sentence the app prints beside it. The packed code is the key,
because an error *number* is task-local — `#1` is a different fault for every task — so
nothing narrower identifies a fault.

Two schema keys feed it, both required and both enforced by the generator: `task:` on the
enum section, naming the `task_offset_t` its numbers belong to, and `description:` on
every value. It also refuses a name that starts with neither `ERROR_` nor `WARNING_`, or
one whose prefix disagrees with the warning bit in its value. All of it fails the build
rather than emitting a blank row, because a blank row is exactly the thing this exists to
prevent: a fault reaching a user as a number with nothing to look it up in.

### Why `code` sections are skipped

`code` sections are verbatim C (`extern "C"` guards, the firmware `PopulateTaskError…`
helper, the `usb_frame_crc16` body) with no C# meaning. The one piece the host needs —
the frame CRC — is re-implemented once in `Dyno.Core/Protocol` from the generated
`USB_FRAME_CRC_POLY` / `USB_FRAME_CRC_INIT` / `USB_FRAME_SOF` constants, so the algorithm
parameters still come from the schema.

## Drift guard (CI)

`check.py` renders every target of every generator here from the schema and compares
against the committed file as a token stream (`verify.normalize` ignores comments,
whitespace and trailing-comma style), so an empty diff proves the same C# is declared
regardless of formatting. CI runs it; if someone hand-edits a generated file instead of
the schema, the build fails and names both the file and the generator that owns it. The
error catalog is covered for a reason of its own: its descriptions read like prose, which
makes the generated file the tempting place to fix a typo.

## Related

[[Dyno.Core]] consumes `Messages.cs` and `ErrorCatalog.cs`. The schema and its full
reference live in the firmware tree under `firmware/tools/message_gen/`.
