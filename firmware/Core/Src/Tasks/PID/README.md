---
module: PID
summary: Closed-loop brake controller; drives BPM duty cycle from optical-encoder feedback.
code:
  - Core/Src/Tasks/PID/PID.cpp
  - Core/Inc/Tasks/PID/PID.hpp
  - Core/Inc/Tasks/PID/pid_main.h
entry_point: pid_main()
task_offset: TASK_OFFSET_PID_CONTROLLER
consumes: [session_controller_to_pid_controller (SessionController), optical_encoder_circular_buffer]
produces: [pid->bpm duty-cycle queue, pid->session ack queue, task_error_circular_buffer]
related: [SessionController, BPM, OpticalSensor]
diagram: Core/Src/Tasks/PID/pid_brake_controller.puml
---

# PID — brake feedback controller

Computes a brake duty cycle from the error between desired and measured angular velocity
and feeds it to [[BPM]]. Enabled/disabled by [[SessionController]].

## I/O
- **in:** `session_controller_to_pid_controller` (enable + desired angular velocity);
  latest `optical_encoder_output_data` from `optical_encoder_circular_buffer`.
- **out:** duty cycle → BPM queue; enable/disable ack → SessionController ack queue;
  errors → `task_error_circular_buffer`.

## Flow
1. `pid_main(scToPid, pidToScAck, pidToBpm, initialState)` → construct, `Run()`.
2. **Enabled:** read encoder velocity → compute P/I/D terms → brake duty cycle → BPM queue; ACK SessionController.
3. **Disabled:** empty the command queue, block until the next instruction.

An enable instruction calls `Reset()`, which clears `_havePreviousSample`; the first sample after
it establishes the baseline and drives nothing. See below for why.

## Errors / warnings
- `WARNING_PID_CONTROLLER_MESSAGE_QUEUE_FULL`

## Key constants (config.h)
- `K_P`, `K_I`, `K_D`, `PID_MAX_OUTPUT`, `BRAKE_GAIN`, `THROTTLE_GAIN`, `PID_TASK_OSDELAY`, `PID_INITIAL_STATUS`
- `PID_ENABLE`, `PID_DESIRED_RPM` — the two runtime (sysconfig) settings, editable from the
  board's settings menu *and* by the host over USB. See below.

## The two enables
- **`PID_CONTROLLER_TASK_ENABLE`** (`debug.h`, compile time) — whether this task exists. Off, the
  thread suspends at entry; nothing at runtime brings it back.
- **`PID_ENABLE` / `SYSCFG_PID_ENABLE`** (`config.h`, runtime) — whether [[SessionController]]
  *offers* the loop. Off, the task is alive but never armed and the encoder drives the brake by
  hand. This is the `PID CONTROL` menu page.

Both must be on for the loop to drive anything. `config.h` carries the long-form note.

## Three fixed bugs, recorded so they are not reintroduced

- **The first sample after an enable slammed the brake to full.** `Reset()` zeroes
  `_prevTimestamp`, but the sample that follows carries a live microsecond counter, so
  `GetTimeDelta()` returned *time since boot* — up to 4.29e9. `integral += _error * timeDelta`
  therefore took a term the size of the whole timestamp range on the very first pass, the output
  saturated, and `BPM::SetDutyCycle` clamped it to the maximum duty cycle, which is where the
  brake stayed while the integral unwound. `K_I` defaults to `1.0f`, so this fired on every
  arming. Fixed with `_havePreviousSample`: the first sample sets the baseline and produces no
  output. A sample that *predates* the reset hit the same bug through `GetTimeDelta`'s wrap
  branch, which is why the fix is a flag rather than seeding `_prevTimestamp` from the clock.
- **The loop stayed armed after a session ended.** Nothing cleared the FSM's `_pidEnabled`, and
  [[SessionController]] publishes PID instructions below its in-session gate — so the task was
  never told to stop. It went on computing against a finished session until every pass logged
  `WARNING_PID_CONTROLLER_MESSAGE_QUEUE_FULL`, and since `_prevPIDEnabled` stayed true the *next*
  session found no enable edge to publish: the BPM was never pointed at the PID output, so the
  panel read `PIDE` over a brake the controller no longer reached. Now `ShowSessionScreen()`
  disarms on entry and `PublishSessionTransition(false)` publishes the disable on the way out.
- **A setpoint change never reached a running loop.** See [[SessionController]]'s
  `PublishPidInstruction`.

## Notes
- Brake-only. The unfinished second output (throttle) and its mixing sketch were removed along
  with the manual throttle control in the [[SessionController]]; nothing was ever wired to
  receive either. `THROTTLE_GAIN`, `HORIZONTAL_BIAS`, `VERTICAL_BIAS` and `PID_MAX_OUTPUT`
  remain in the config schema for whoever revives it, but no code reads them.
- **The output is not bounded by this task.** `SendBrakeDutyCycle` passes the raw gain-scaled
  sum, and `BPM::SetDutyCycle` clamps it to the configured duty-cycle envelope. That is the only
  limit — there is no anti-windup here beyond the reset on enable, so a sustained error still
  grows the integral without bound. Left as-is because the actuator clamp makes it safe, not
  because it is good control.
- State machine diagram: `pid_brake_controller.puml`.

## Related
[[SessionController]] · [[BPM]] · [[OpticalSensor]]
