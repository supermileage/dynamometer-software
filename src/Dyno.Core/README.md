---
module: Dyno.Core
summary: UI-agnostic device layer — serial link, USB wire-protocol parser + frame builder, and the schema-generated message types.
code:
  - src/Dyno.Core/DeviceClient.cs
  - src/Dyno.Core/Serial/SerialConnection.cs
  - src/Dyno.Core/Protocol/StreamParser.cs
  - src/Dyno.Core/Protocol/UsbFrame.cs
  - src/Dyno.Core/Protocol/ErrorDecoder.cs
  - src/Dyno.Core/Messages/Generated/Messages.cs
related: [message_gen, Dyno.App]
---

# Dyno.Core — device layer

Everything needed to talk to the STM32 over USB-CDC, with **no UI dependency** — so it is
unit-testable and headless-runnable. The Avalonia app ([[Dyno.App]]) is a thin shell on top.

## Layout
| Area | Type | Role |
|---|---|---|
| `Serial/` | `ISerialConnection`, `SerialConnection` | `System.IO.Ports` wrapper (115200 8-N-1); `COM3` ↔ `/dev/ttyACM0`. The interface lets tests inject a fake. |
| `Protocol/` | `StreamParser` | Decodes the **unframed** STM32 → PC stream into typed `DeviceMessage`s. |
| `Protocol/` | `UsbFrame` | Builds **framed** PC → STM32 commands (`[SOF][header][payload][crc16]`) and ports the CRC. |
| `Protocol/` | `ErrorDecoder` | Unpacks the 32-bit `error_code` (task / number / warning-flag). |
| `Messages/Generated/` | `Messages.cs` | Enums, packed structs and constants — **generated** from the firmware schema by [[message_gen]]. Do not hand-edit. |
| (root) | `DeviceClient` | Owns the connection, pumps bytes through the parser on a background loop, and correlates command RESPONSEs by `msg_id`. |

## Receive path (STM32 → PC)
The firmware emits `usb_msg_header_t`(12 B) + payload back-to-back with **no SOF/CRC** (USB
CDC is already reliable). `StreamParser.Append` accumulates bytes, reads each header, waits
for `payload_len` bytes, then dispatches on `(msg_type, task_offset)` to an
`OpticalEncoderSample` / `ForceSensorSample` / `BpmSample` / `TaskMonitorSample` /
`DeviceFault` / `CommandResponse` / `DeviceReady` / `SessionState` / `UnknownMessage`. A header
plausibility check gives best-effort resync if the stream is ever corrupted.

## Transmit path (PC → STM32)
`UsbFrame.BuildCommandFrame` wraps a `usb_cmd_header_t` (opcode + msg_id) + body in the
framed envelope with a CRC-16/CCITT-FALSE over header+payload. `DeviceClient.SendCommandAsync`
allocates a `msg_id`, writes the frame, and awaits the matching `USB_MSG_RESPONSE`.

### Saying what a command was
A `USB_MSG_RESPONSE` carries only an opcode, the `msg_id` it echoes, and a status — so it cannot,
on its own, say *which* sysconfig parameter a write set or to what. Only the sender knows that, and
only until it stops caring. `SendCommandAsync` therefore takes a `description` ("sysconfig
K_P = 2.5", built from `SysConfigCatalog`), holds it with the pending request, and reports the
command's whole life against it:

- `CommandSent` as it goes out (once per command, not per retry),
- `CommandResponse.Request` on the reply that matches its `msg_id` — so a subscriber sees the ack
  next to what it acks, and a non-OK status is a *named* parameter being refused,
- `CommandFailed` if it runs out of attempts unanswered. A firmware *rejection* is not this: the
  device replied, and the reply speaks for itself.

The reply is published to `MessageReceived` **before** the pending command completes, so a sender
cannot resume — and announce its next write — ahead of the ack for its last one. Applying a page of
sysconfig edits is exactly that: one write after another, whose acks would otherwise log a beat late
and read as the following parameter's. Undescribed commands (the handshake ack, the heartbeats built
on it) raise neither event, which is what keeps routine link traffic out of the app's event log.

## Handshake (device-announced)
The firmware streams **nothing** until the host acknowledges it. On connect it repeatedly
emits a `usb_device_ready_event` (`USB_MSG_EVENT` / `TASK_OFFSET_USB_CONTROLLER`) carrying its
`USB_PROTOCOL_VERSION`. `DeviceClient` decodes it to a `DeviceReady`, and — once per link —
checks the version against the host's generated `MessageConstants.USB_PROTOCOL_VERSION`:
- **match** → sends `USB_CMD_ACK` (body = the host version); on `USB_RSP_OK` it sets
  `IsHandshaked` and raises `Handshaked`, after which telemetry flows.
- **mismatch** → raises `ProtocolMismatch` and refuses to ack (the schemas are out of sync, so
  decoding the stream would be unsafe). This is the **runtime** counterpart to the build-time
  struct-size checks in `MessageContract`.
- **silence** → the device speaks first, so a board in the bootloader, a wedged firmware or simply
  the wrong port would leave the host waiting forever. `HandshakeTimeout` (5 s from `Start`) bounds
  that wait and raises `HandshakeTimedOut`. The link keeps listening afterwards — a device that
  turns up late still handshakes — and a refused version doesn't also fire it.

### Why the host also probes
**The firmware announces only while it has not been acked, and it never un-acks.** `_appReady` is
set by the first `USB_CMD_ACK` and nothing clears it — the board cannot even see the host leave,
since its `CDC_SET_CONTROL_LINE_STATE` handler (where DTR would say so) is empty. So on every
*re*-connect to a board that stayed powered, no announcement is coming, and an announce-driven
handshake would hang forever.

So the host asks rather than waits: if no announcement arrives within `HandshakeProbeInterval`
(1 s), it sends an unsolicited `USB_CMD_ACK`. The firmware answers that in any state and
re-applying it is a no-op, so the probe both proves the device is alive and re-arms its streaming.
A freshly-booted board (announcing every 200 ms) handshakes long before the first probe is due, so
the probe is a fallback, not the normal path. A probe refused with `USB_RSP_VERSION_MISMATCH`
raises `ProtocolMismatch(null)` — the device rejected us without saying what it speaks.

*The firmware fix (clear `_appReady` on DTR deassert, so the board resumes announcing) is still
worth doing; the probe means the host works against boards already in the field.*

## Session state
The dyno streams sensor data **only while a session is running**, so the absence of samples means
nothing on its own — an idle board and a dead one look the same. The device therefore announces a
`session_state_event` (`USB_MSG_EVENT` / `TASK_OFFSET_SESSION_CONTROLLER`), decoded to a
`SessionState`: on every start/stop, *and* after every host `USB_CMD_ACK` even when unchanged.
`DeviceClient` tracks it as `IsSessionActive` and raises `SessionStateChanged` — but **only when
the value actually moves**, since the firmware re-states it on every 5 s heartbeat and the repeats
are not news.

That per-ack repeat is what makes the state recoverable rather than merely observable: a host
connecting to a steady board (idle, or already mid-session) learns the state without waiting for an
edge, and a host that declared the link lost — which clears `IsSessionActive`, since a device we
cannot reach is not one we can claim is running a session — has it restored by the ack that answers
the next heartbeat, with no reconnect.

Because `StreamParser` decodes in wire order and `DeviceClient` applies the session state *before*
re-publishing each message, `IsSessionActive` is already correct when the samples framed behind an
event are delivered. The app relies on that: it renders sensor readouts only while a session is
active, so a lagging flag would drop the first samples of every session.

## Heartbeat (liveness)
An open serial port does not mean a live device, and an idle device streams nothing — so after
the handshake `DeviceClient` re-sends `USB_CMD_ACK` every `HeartbeatInterval` (5 s) as a ping.
The firmware answers it from the USB controller itself and applying it twice is a no-op, so it
needs no dedicated opcode. Each answered ping raises `HeartbeatAcked` with its round-trip time
(the app logs a `[PING]` line); after `HeartbeatMissesBeforeLost` (2) unanswered ones the link is
declared lost: `IsHandshaked` clears and `ConnectionLost` fires. Polling **continues** through
the outage, so a device that comes back is picked up and re-raises `Handshaked` with no
reconnect. The raw response to this ack is a link keep-alive, not a command ack — the app filters
it out of the event log by `CommandResponse.Source` so it isn't logged twice.

## Threading
`DeviceClient.MessageReceived` fires on the read-loop thread; UI consumers must marshal to
their own thread (the app uses Avalonia's `Dispatcher`). `StreamParser` is single-reader,
not reentrant.

## Related
[[message_gen]] (generates `Messages.cs`) · [[Dyno.App]] (consumes this layer)
