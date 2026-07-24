---
module: MessagePassing
summary: Inter-task queues, shared circular buffers, and the USB wire protocol (incl. the packed error code).
code:
  - Core/Inc/MessagePassing/messages_public.h
  - Core/Inc/MessagePassing/messages_private.h
  - Core/Inc/MessagePassing/osqueue_helpers.h
  - Core/Src/MessagePassing/osqueue_helpers.c
  - Core/Src/MessagePassing/circular_buffers.c
related: [USB, CircularBuffer, Config, TaskMonitor]
---

# MessagePassing — protocol, queues, buffers

## Headers — generated, not written
Both headers are **generated from `firmware/tools/message_gen/schema/messages_public.yaml`**, which is
the single source of truth for every struct, enum and constant on the wire. Edit the schema and
re-run `firmware/tools/message_gen/generate.py`; editing a header directly is overwritten on the next
run, and CI (`check.py`) fails when a committed header does not match its schema.

The same schema generates the PC side — `Messages.cs`, `SysConfigCatalog.cs`, `ErrorCatalog.cs` —
so the two ends cannot drift apart by hand. See `tools/message_gen/README.md`.

- **messages_public.h** — the contract shared with the **PC software**: `task_offset_t`,
  the packed error code, `usb_msg_header_t`, the framing constants, and the per-stream output structs
  (`optical_encoder_output_data`, `forcesensor_output_data`, `bpm_output_data`,
  `task_monitor_output_data`).
- **messages_private.h** — firmware-internal queue payloads:
  `session_controller_to_lumex_lcd`, `session_controller_to_bpm`,
  `session_controller_to_pid_controller` (+ their opcode enums). Includes the public header.
- **sysconfig_table.inc** — the runtime store's parameter table ([[Config]]), from the same schema.

## Error / warning encoding (the packed code)
A task error is a single 32-bit `error_code = task_offset | (warning ? WARNING_FLAG : 0) | error_number`:

```
bits 31..16   task offset            (task_offset_t, index << TASK_OFFSET_SHIFT)
bit  15       WARNING_FLAG           (set ⇒ warning, clear ⇒ error)
bits 14..0    task-local error num   (per-task enum, e.g. ERROR_BPM_PWM_START_FAILURE)
```

- `PopulateTaskErrorDataStruct(ts, TASK_OFFSET_X, error_id)` ORs offset | error into `error_code`.
- `task_error_data` is `{ uint32_t timestamp; uint32_t error_code; }` (**8 bytes**) — the task id
  is no longer a separate field, it lives in the high bits of `error_code`.
- **PC decode:** `is_warning = error_code & WARNING_FLAG`; `task = error_code & TASK_OFFSET_MASK`;
  `err = error_code & TASK_ERROR_NUM_MASK`.
- Each task owns one offset (`TASK_OFFSET_*`). The ADC and ADS1115 force sensors have
  **distinct** offsets because they report different errors.
- Every fault is **described in the schema**, which generates `ErrorCatalog.cs` for the host. So the
  app shows "USB optical-encoder buffer overflow" rather than a task offset and a number the reader
  has to look up in firmware source. A packed number only has meaning inside the enum of the task it
  is attributed to, so a fault must be added to that task's id list, not to a global one.

## USB header
`usb_msg_header_t { usb_msg_type_t msg_type; task_offset_t task_offset; uint32_t payload_len; }`
prefixes every payload. `task_offset` identifies the producing module (stream routing); `msg_type` is
one of:

| Direction | Types |
|---|---|
| PC → STM32 | `USB_MSG_COMMAND`, `USB_MSG_CONFIG` |
| STM32 → PC | `USB_MSG_RESPONSE`, `USB_MSG_EVENT`, `USB_MSG_STREAM`, `USB_MSG_STATUS`, `USB_MSG_ERROR`, `USB_MSG_WARNING` |

`msg_id` correlates each `RESPONSE` with its `COMMAND`; `msg_id` 0 is firmware-internal and is never
acked to the host. Message flow in full: [`usb_message_flow.puml`](../Tasks/USB/usb_message_flow.puml).

## Framing (both directions)
Every record is wrapped so a receiver can resync after losing bytes:

```
[uint16 USB_FRAME_SOF][usb_msg_header_t][payload][uint16 CRC-16/CCITT-FALSE]
```

`SOF` is `0xA55A`; the CRC (poly `0x1021`, init `0xFFFF`) covers the header and payload bytes, not
the SOF or the CRC field itself. A receiver scans to a SOF, checks the CRC, and on a mismatch treats
that SOF as spurious — so a cut-off record is *detected* rather than inferred from a payload that
happens to look wrong. The schema carries the shared CRC routine so both ends compute it identically.

Device→host framing arrived in **v5**; before that, outbound records were bare header + payload on
the assumption USB-CDC is reliable, which the 16-byte head loss disproved.

Each CDC transfer is then closed by a `usb_tx_batch_trailer` (`USB_MSG_STATUS`) carrying `batch_seq`
and `batch_len`, which is what makes byte loss *attributable* rather than merely detectable — see
[[USB]].

## Protocol version
`USB_PROTOCOL_VERSION` (currently **7**) is exchanged at the handshake and the link is refused on a
mismatch, so a host built against a stale schema is rejected instead of silently mis-decoding. The
schema records what each bump changed and why it could not be silent — worth reading before adding
one, since the bar is "an old peer would misbehave in a way that does not look like a bug".

## Queue helpers (osqueue_helpers.h)
- `EmptyQueue(qHandle, itemSize)` — drop all queued items.
- `GetLatestFromQueue(qHandle, out, itemSize, timeout)` — keep only the newest item; returns bool.

## Circular buffers (circular_buffers.c)
Defines the shared arrays + writer indices for the optical / force / bpm / task-error streams;
consumers declare them `extern`. Mechanics: [[CircularBuffer]].

## Related
[[USB]] · [[CircularBuffer]] · [[TaskMonitor]] · [[Config]]
