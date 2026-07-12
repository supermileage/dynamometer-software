---
module: USB
summary: Serializes all data + error streams into framed USB-CDC messages to the PC.
code:
  - Core/Src/Tasks/USB/USBController.cpp
  - Core/Inc/Tasks/USB/USBController.hpp
  - Core/Inc/Tasks/USB/usbcontroller_main.h
entry_point: usbcontroller_main()
task_offset: TASK_OFFSET_USB_CONTROLLER
consumes: [optical_encoder/forcesensor/bpm/task_error circular buffers, taskMonitor queue, SessionController in-session flag]
produces: [USB CDC stream to PC (CDC_Transmit_FS)]
related: [MessagePassing, TaskMonitor, SessionController]
---

# USB — host data link

Drains the data/error streams, prefixes each with a `usb_msg_header_t`, and transmits
over USB CDC. Each record = header + payload (see the wire protocol in [[MessagePassing]]).

A full sequence of every message — handshake, streaming, and command routing (including the
routed "non-posted" ack) — is in [`usb_message_flow.puml`](usb_message_flow.puml).

## Flow (`Run()`)
Each iteration:
0. `HandleHostDetach()` — if the host closed the port (CDC DTR dropped), un-ack the link.
1. `ProcessIncomingFrames()` — pull + route CRC-validated host frames (always, so the host can
   handshake/configure whether or not a session is running).
2. `ProcessCompletions()` — relay applied-command acks as `USB_MSG_RESPONSE`.
3. **Handshake gate:** while `!_appReady`, `AnnounceReadyIfDue()` emits a `usb_device_ready_event`
   (`USB_MSG_EVENT`, `TASK_OFFSET_USB_CONTROLLER`) about every `DEVICE_READY_ANNOUNCE_MS`.
4. Pick up the SessionController's in-session flag; on session entry, `SkipBufferedSensorData()`.
5. **Sensor data** — only while `_appReady && inSession`:
   - `optical_encoder_output_data` (`USB_MSG_STREAM`, `TASK_OFFSET_OPTICAL_ENCODER`)
   - `forcesensor_output_data` (`USB_MSG_STREAM`, active force-sensor offset)
   - `bpm_output_data`
6. **Health + faults** — whenever `_appReady`, session or not: `task_monitor_output_data` and
   errors via `ProcessErrorsAndWarnings()`.
7. `CDC_Transmit_FS(_txBuffer, …)`; delay `USB_TASK_OSDELAY`.

## What is gated on a session (and what is not)
Sensor data is streamed **only while a session runs** — that is the only gate, and it is the
session, not a setting: there is no USB-logging on/off switch, so a host connected during a session
always receives that session's data.

Everything else the link carries is *not* session-gated, because it is not sensor data: the
device-ready announcement, command RESPONSEs, task-monitor health and faults all flow to any
handshaked host. A task dying or a stack running out is most worth seeing while the dyno sits
idle, and draining the task-monitor queue unconditionally also stops it filling up between
sessions.

Because the sensor tasks sample continuously (SessionController enables them once, at startup),
their circular buffers keep filling while no session runs. `SkipBufferedSensorData()` catches each
reader up to its writer on session entry, so a session opens with live data instead of flushing a
backlog of samples recorded — and timestamped — before it began.

## Handshake (device-announced)
The firmware streams nothing until the host acknowledges it. While `_appReady` is false the
device repeatedly announces `usb_device_ready_event{ USB_PROTOCOL_VERSION }`; the host answers
with a framed `USB_CMD_ACK` whose body is its own `USB_PROTOCOL_VERSION`. `HandleUsbLocalCommand`
sets `_appReady` and replies `USB_RSP_OK` when the versions match, or `USB_RSP_VERSION_MISMATCH`
(and keeps announcing) when they differ — so a host built against a stale schema is rejected at
the link instead of silently mis-decoding the stream. `MockMessages()` gates on the same
handshake via `WaitForHandshake()`.

`USB_CMD_ACK` is answered **in any state**, and applying it twice is a no-op. That is deliberate:
it lets the host re-send it as a keep-alive (below) and as a probe, without a dedicated opcode.

## Host disconnect (`HandleHostDetach`)
USB CDC keeps the cable enumerated when the host closes the port, so nothing tells the firmware the
host is gone. Left to itself it would hold `_appReady` forever — still streaming into a port
nobody reads, and (since `AnnounceReadyIfDue()` only runs while `!_appReady`) never announcing
itself again, so the *next* connect would find a device that never introduces itself and so never
handshakes. The one signal available is the CDC control line: `CDC_SET_CONTROL_LINE_STATE`
(`usbd_cdc_if.c`) tracks **DTR**, which a host asserts while it holds the port open, and latches an
edge when it drops. `HandleHostDetach()` consumes that edge via `usb_host_detached()`, clears
`_appReady`, and drops the TX buffer and RX ring — a half-written frame belongs to the dead
session, and replaying it into the next one desyncs the parser.

This requires the host to actually hold DTR (`SerialPort.DtrEnable`); a host that never asserts it
simply never produces the edge, and the link falls back on the host-side probe below.

## Keep-alive and probe (host side)
Complementary halves of the same idea, both in `DeviceClient` (see `src/Dyno.Core/README.md`):
- **Keep-alive** — once handshaked, the host re-sends `USB_CMD_ACK` every 5 s. An open port does
  not mean a live device, and an idle one streams nothing, so silence is otherwise
  indistinguishable from health. Two unanswered pings and the host reports the link lost.
- **Probe** — if no announcement arrives within 1 s of connecting, the host sends an unsolicited
  `USB_CMD_ACK` rather than waiting. This is what rescues a reconnect to a board running firmware
  *without* the detach fix above (which is still silently `_appReady` and so never announces).

## Error/warning framing
`ProcessErrorsAndWarnings()` reads `task_error_data` from `task_error_circular_buffer` and sets
the header from the packed code: `msg_type = (error_code & WARNING_FLAG) ? USB_MSG_WARNING : USB_MSG_ERROR`,
`task_offset = error_code & TASK_OFFSET_MASK`. (Encoding: [[MessagePassing]].)

## Helpers / config
- `ProcessTaskData<T>(reader|queue, task_offset)`, `AddToBuffer<T>`, `IsBufferFull` / `StallIfIsBufferFull`.
- `ACTIVE_FORCE_SENSOR_TASK_OFFSET` selects ADS1115 vs ADC offset for the shared force stream.
- `MockMessages()` emits synthetic data when `DEBUG_USB_CONTROLLER_MOCK_MESSAGES` is set.
- `USB_TX_BUFFER_SIZE`, `USB_TASK_OSDELAY` (config.h).

## Related
[[MessagePassing]] · [[TaskMonitor]] · [[SessionController]]
