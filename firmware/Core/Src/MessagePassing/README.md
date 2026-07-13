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

## Headers
- **messages_public.h** — the contract shared with the **PC software**: `task_offset_t`,
  the packed error code, `usb_msg_header_t`, and the per-stream output structs
  (`optical_encoder_output_data`, `forcesensor_output_data`, `bpm_output_data`,
  `task_monitor_output_data`). Any change here must be mirrored in the PC-side parser.
- **messages_private.h** — firmware-internal queue payloads:
  `session_controller_to_lumex_lcd`, `session_controller_to_bpm`,
  `session_controller_to_pid_controller` (+ their opcode enums). Includes the public header.

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

## USB header
`usb_msg_header_t { usb_msg_type_t msg_type; task_offset_t task_offset; uint32_t payload_len; }`
prefixes every payload. `task_offset` identifies the producing module (stream routing);
`msg_type` is `USB_MSG_STREAM` / `USB_MSG_ERROR` / `USB_MSG_WARNING` / … (see [[USB]]).

## Queue helpers (osqueue_helpers.h)
- `EmptyQueue(qHandle, itemSize)` — drop all queued items.
- `GetLatestFromQueue(qHandle, out, itemSize, timeout)` — keep only the newest item; returns bool.

## Circular buffers (circular_buffers.c)
Defines the shared arrays + writer indices for the optical / force / bpm / task-error streams;
consumers declare them `extern`. Mechanics: [[CircularBuffer]].

## Related
[[USB]] · [[CircularBuffer]] · [[TaskMonitor]] · [[Config]]
