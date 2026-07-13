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

## Errors / warnings
- `WARNING_PID_CONTROLLER_MESSAGE_QUEUE_FULL`

## Key constants (config.h)
- `K_P`, `K_I`, `K_D`, `PID_MAX_OUTPUT`, `BRAKE_GAIN`, `THROTTLE_GAIN`, `PID_TASK_OSDELAY`, `PID_INITIAL_STATUS`

## Notes
- Brake-only today; throttle / dual-output mixing is a planned extension.
- State machine diagram: `pid_brake_controller.puml`.

## Related
[[SessionController]] · [[BPM]] · [[OpticalSensor]]
