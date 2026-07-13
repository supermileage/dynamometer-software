---
module: BPM
summary: Drives the motor PWM (duty cycle) from SessionController / PID commands.
code:
  - Core/Src/Tasks/BPM/BPM.cpp
  - Core/Inc/Tasks/BPM/BPM.hpp
  - Core/Inc/Tasks/BPM/bpm_main.h
entry_point: bpm_main()
task_offset: TASK_OFFSET_BPM_CONTROLLER
consumes: [session_controller_to_bpm (SessionController), pid->bpm duty cycle (PID)]
produces: [bpm_circular_buffer, task_error_circular_buffer]
related: [SessionController, PID, MessagePassing]
---

# BPM — motor PWM controller

Owns the PWM timer that sets motor duty cycle. Receives start/stop/read-from-PID
commands from [[SessionController]] and, when enabled, tracks the [[PID]] output.

## Flow
1. `bpm_main(scToBpm, pidToBpm)` constructs `BPM`, calls `Init()`, then `Run()`.
2. `Run()` blocks on `session_controller_to_bpm`; opcodes: `READ_FROM_PID`, `START_PWM`, `STOP_PWM`.
3. When reading from PID, pulls the latest duty cycle and updates the timer compare register.
4. Writes each applied duty cycle to `bpm_circular_buffer`; delays `BPM_TASK_OSDELAY`.

## I/O
- **in:** `session_controller_to_bpm` queue; PID→BPM duty-cycle queue.
- **out:** `bpm_circular_buffer` (samples); `task_error_circular_buffer` (errors).

## Errors
- `ERROR_BPM_PWM_START_FAILURE`, `ERROR_BPM_PWM_STOP_FAILURE`

## Key constants (config.h)
- `BPM_TASK_OSDELAY`, `MIN_DUTY_CYCLE_PERCENT`, `MAX_DUTY_CYCLE_PERCENT`

## Notes
- `SetDutyCycle` clamps to the MIN/MAX limits before writing the compare register.
- `TogglePWM` only starts/stops when the state actually changes.

## Related
[[SessionController]] · [[PID]] · [[MessagePassing]]
