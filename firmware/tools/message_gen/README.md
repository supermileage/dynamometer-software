# message_gen

Generates the MessagePassing C headers from a YAML schema, so the wire-protocol
contract lives in one declarative place instead of being hand-maintained (and
hand-mirrored on the PC side).

```
schema/*.yaml  ──(templates/header.h.j2)──►  Core/Inc/MessagePassing/*.h
```

## Targets

| target             | schema                          | output                                   |
|--------------------|---------------------------------|------------------------------------------|
| `messages_public`  | `schema/messages_public.yaml`   | `Core/Inc/MessagePassing/messages_public.h`  |
| `messages_private` | `schema/messages_private.yaml`  | `Core/Inc/MessagePassing/messages_private.h` |

Both render through the single generic template `templates/header.h.j2`.

## Usage

```sh
# one-time: deps (Jinja2 + PyYAML)
python3 -m venv .venv && .venv/bin/pip install jinja2 pyyaml

.venv/bin/python generate.py                       # regenerate every header
.venv/bin/python generate.py --target messages_public --stdout   # preview one
```

## Schema reference

`sections:` is an ordered list; each entry has a `kind`:

| kind            | fields                                            | emits                                  |
|-----------------|---------------------------------------------------|----------------------------------------|
| `comment`       | `text`                                            | a `//` block                           |
| `define`        | `name`, `value`, `comment?`                       | `#define`                              |
| `code`          | `text`                                            | verbatim C (extern "C" guards, helpers)|
| `enum`          | `name`, `base`, `comment?`, `values[]`            | `typedef enum : base { ... }`          |
| `struct`        | `name`, `packed?`, `comment?`, `fields[]`         | `typedef struct [packed] { ... }`      |
| `static_assert` | `expr`, `message`                                 | `_Static_assert(expr, "message");`     |

- enum value: `{ name, value?, comment?, blank_before? }`
- struct field: `{ type, name, comment?, array? }` (`array: N` -> `type name[N];`)
- include entry: `"stdint.h"` -> `<stdint.h>`; `{ local: "x.h" }` -> `"x.h"`

`static_assert` is its own ordered section so the size checks land where the original
header puts them, and so the assert spelling exists in exactly one place (the
template) instead of being copied per type.

## Verifying / CI drift guard

`verify.py` compares two headers as C token streams (comments, whitespace and a
trailing comma before `}` are ignored), so an empty diff proves the same C is
declared regardless of formatting:

```sh
# regenerate to a temp file and fail if someone hand-edited the committed header
.venv/bin/python generate.py --target messages_public --out /tmp/gen.h
.venv/bin/python verify.py Core/Inc/MessagePassing/messages_public.h /tmp/gen.h
```

## Notes

- Asserts are emitted as `_Static_assert` to match the existing headers. That is a C
  keyword; mainline g++ rejects it, but these headers are also included by C++ task
  TUs, so if a host/C++ build ever trips on it, switch the one line in
  `header.h.j2` to bare `static_assert` (valid in both C23 and C++11+).
- The PC-side parser can be added as a third target (its own schema/template, same
  generator) so the firmware header and host parser can never drift.
