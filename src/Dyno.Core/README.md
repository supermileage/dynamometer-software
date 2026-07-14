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
`DeviceFault` / `CommandResponse` / `DeviceReady` / `SessionState` / `UnknownMessage`.

`msg_type` is the protocol-level intent and decides the direction: `COMMAND` and `CONFIG` only ever
go PC → STM32; `RESPONSE` (a reply to one of those), `EVENT` (an async announcement — device-ready,
session start/stop), `STREAM` (continuous sensor data), `STATUS`, `ERROR` and `WARNING` only ever
come back. `task_offset` says which module the payload belongs to, and the two together pick the
record: `EVENT` + `USB_CONTROLLER` is a device-ready announce, `STREAM` + `OPTICAL_ENCODER` is an
encoder sample, and so on.

### Resync, and why it has to be strict
Nothing in this direction is framed — no start marker, no CRC, no length prefix beyond the header's
own — so **any 12 bytes that look like a header are a header**, and the parser will consume a
payload's worth of real data behind them. If the board ever drops bytes (a full USB ring is the
likely cause), every record after the gap is misaligned, and a window onto the middle of one record
can pass for the start of another.

`IsPlausible` is therefore as strict as the format allows: an inbound-only `msg_type`, a
`task_offset` the firmware actually has (they are sparse — 0, 0x10000, 0x20000 …), a payload within
bounds, and — for every record we can decode — **exactly** the size that record must be. That last
check matters more than it looks: `TASK_OFFSET_TASK_MONITOR` is 0, so without it a run of zeros is a
"valid" task, and a stray `4` nearby is a "valid" STREAM type. A real link produced
`RESPONSE / task 252 / len 4` — impossible twice over (no such task; a RESPONSE is always 8 bytes) —
and a laxer check reported it upward as a message the device never sent.

Bytes skipped to regain alignment are counted and raised on `Resynced` **once alignment is regained**
(not per read — one lost record is re-scanned across several serial chunks, and reporting per chunk
turns one fault into a stutter of warnings). `DeviceClient` re-raises it as `StreamResynced`, and the
app logs it as a warning: dropped bytes are the *only* evidence this link ever loses data, so they
are a link fault to surface, not noise to swallow. An `UnknownMessage` now means what it says — a
well-formed record this host has no decoder for.

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

## Sysconfig: the host is the memory
The firmware's runtime-tunable parameters (`SysConfigCatalog`, generated from the schema) live on the
board in **RAM with no flash behind it**. It boots on its config.h defaults, keeps whatever the host
wrote only while it stays powered, and cannot tell anyone what it currently holds. So the PC's SQLite
store is not a cache of the device's settings — it is the only copy, and the device is the copy.

That splits saving from applying. Saving is the app's Apply button — the **only** thing that writes
to `SysConfigStore`, and it touches no device. Every edit on the page is staged until it: typing a
value, and Reset too, which stages the firmware default rather than committing it (a reset that wrote
straight through would be the one edit that bypassed the button, and would leave Apply greyed out on
a change the user had just made). Applying is a reconciliation pass over `SysConfigDeviceMirror`,
which tracks what the board is *believed* to hold and yields the parameters whose saved value it
isn't known to have. One pass therefore serves both cases:

- **A value changed** → the mirror is current except for that one parameter, so exactly one write
  goes out (announced, so the event log names it).
- **A device connected** → `Forget()` first, because a link that dropped may have dropped *because*
  the board reset. Nothing is believed, so the whole catalog goes out — **defaults included**, since
  a board that stayed powered through a host restart is still holding the last session's values, and
  a parameter the user never overrode is exactly the one nobody would think to re-send. All 27 are
  written unannounced and reported as a single summary line.

A write that is never acked is simply never confirmed, so it stays outstanding and goes out again on
the next pass — which is the only recovery available to a board that cannot remember anything.

### Compile-time settings are saved here, and applied by a build
The same page also lists the `#define`s from `config.h` / `debug.h` (`FirmwareConfigFile` parses
them). These *cannot* be applied to a running board — buffer sizes dimension static arrays on a
heapless firmware, and debug.h decides what code is compiled in at all — so Apply saves them to the
store's second table (`compiletime`, keyed by define name, values kept as text because
`ADS1115_RATE_475` and `16 + 1` are as much a `#define` value as `100u`), and the **Firmware page's
Build** is what carries them into a compiler. See below.

The page shows what the header actually has beside any setting whose saved value differs, and offers
Reset to drop the row. A value equal to the header's is stored as no row at all: wanting what you
already have is not worth remembering, and that is how the row a Reset undid actually disappears.

## Firmware: build and flash
`firmware/Scripts/` already knows how this board is built and programmed — the tool matrix, the
ROM-bootloader rules, which build tree holds the newer image — and it is what CI and the terminal
use. So the app **drives those scripts** rather than reimplementing any of it: `FirmwareCommands`
constructs the exact invocation (bash on Linux/macOS, PowerShell on Windows) and `ProcessRunner`
streams its output line by line, unbuffered, because a Docker build takes minutes and both jobs fail
in ways only their own output explains. The command is echoed before it runs, so what the app did is
always something the user could have typed.

The app adds only what a script can't: which tools go with which method (`ToolsFor` — and never
`cubeprog` first, since it is the one tool needing an ST account), whether the board must be put into
its bootloader by hand (`NeedsBootloader` — everything but SWD), and which device-selection arguments
a given method/tool pair actually reads. A DFU index is passed to `cubeprog` and to nothing else,
because `dfu-util` has no such notion and would ignore it while the user believed it had taken
effect.

### Getting the compile-time settings into the compiler
The firmware's `#define`s are not `#ifndef`-guarded, so a `-D` on the command line loses to the
header. Rather than rewrite the committed headers (which would make every build dirty the working
tree), `config.h` and `debug.h` each end with:

```c
#if __has_include("config_overrides.h")
#include "config_overrides.h"
#endif
```

`ConfigOverrides` generates that file — one `#undef`/`#define` pair per changed setting, nothing else
— immediately before a build. Being included **last**, it wins; the C sources are untouched and still
read the plain names; and a clean checkout builds exactly what the headers say, because the file is
absent and `__has_include` is false. It is git-ignored: an override is one machine's intent, not the
project's.

Three details the tests pin down:
- **Both files are always written**, even with nothing to override. A file left over from an earlier
  build would otherwise keep applying a setting the user had since reset — the board would come back
  holding it, with nothing on screen saying so.
- **The text is stable and only written when it changes.** Ninja keys off mtimes, so a file that
  churned would recompile the whole firmware on every build.
- **A value that could rewrite the header around it is refused.** The generated file is C that nobody
  reviews; a value carrying `//` or a newline could define anything it liked.

Bad *combinations* are not the app's business: the firmware already enforces them itself (`#error
"Cannot enable both ADS1115 and ADC Force Sensor modules at the same time!"` and ~19 others), and an
override that trips one fails the build with that message, which is the right one.

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
