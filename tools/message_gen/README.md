---
module: message_gen
summary: Generates the C# wire-protocol types from the firmware's YAML schema, so the host and firmware can't drift.
code:
  - tools/message_gen/generate.py
  - tools/message_gen/templates/csharp.cs.j2
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
stm32_dyno_firmware_v2/tools/message_gen/schema/messages_public.yaml
        │  (read from the firmware tree — the single source of truth)
        ▼  templates/csharp.cs.j2
src/Dyno.Core/Messages/Generated/Messages.cs   (committed, CI-checked)
```

## Usage

```sh
# one-time: deps (Jinja2 + PyYAML)
python3 -m venv .venv && .venv/bin/pip install -r requirements.txt

.venv/bin/python generate.py                                  # regenerate Messages.cs
.venv/bin/python generate.py --target messages_public --stdout  # preview, don't write
.venv/bin/python check.py                                     # CI drift guard (no temp files)
```

The schema lives in the firmware tree at `stm32_dyno_firmware_v2/tools/message_gen/schema/`.
After a protocol change, rerun `generate.py` and commit the regenerated `Messages.cs`.

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

### Why `code` sections are skipped

`code` sections are verbatim C (`extern "C"` guards, the firmware `PopulateTaskError…`
helper, the `usb_frame_crc16` body) with no C# meaning. The one piece the host needs —
the frame CRC — is re-implemented once in `Dyno.Core/Protocol` from the generated
`USB_FRAME_CRC_POLY` / `USB_FRAME_CRC_INIT` / `USB_FRAME_SOF` constants, so the algorithm
parameters still come from the schema.

## Drift guard (CI)

`check.py` renders from the schema and compares against the committed `Messages.cs` as a
token stream (`verify.normalize` ignores comments, whitespace and trailing-comma style),
so an empty diff proves the same C# is declared regardless of formatting. CI runs it; if
someone hand-edits `Messages.cs` instead of the schema, the build fails and names the file.

## Related

[[Dyno.Core]] consumes `Messages.cs`. The schema and its full reference live in the
firmware tree under `stm32_dyno_firmware_v2/tools/message_gen/`.
