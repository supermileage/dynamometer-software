---
module: SessionController
summary: Central orchestrator — runs the UI state machine and coordinates every task.
code:
  - Core/Src/Tasks/SessionController/SessionController.cpp
  - Core/Inc/Tasks/SessionController/SessionController.hpp
  - Core/Inc/Tasks/SessionController/sessioncontroller_main.h
  - Core/Src/Tasks/SessionController/FiniteStateMachine.cpp
  - Core/Inc/Tasks/SessionController/FiniteStateMachine.hpp
  - Core/Src/Tasks/SessionController/input_manager_interrupts.c
  - Core/Inc/Tasks/SessionController/input_manager_interrupts.h
entry_point: sessioncontroller_main()
task_offset: TASK_OFFSET_SESSION_CONTROLLER
consumes: [button/encoder GPIO interrupts, pid_controller_ack queue, forcesensor_circular_buffer, optical_encoder_circular_buffer]
produces: [commands to usb/sd/bpm/pid/display/force_sensor/optical_sensor queues, task_error_circular_buffer]
related: [BPM, PID, USB, LCD, ForceSensor, OpticalSensor, TimeKeeping]
---

# SessionController — orchestrator

The top-level task. Validates every queue handle, runs the UI/FSM, dispatches commands to all
other tasks, and drives the LCD readout. It stamps samples from the [[TimeKeeping]] counter but
no longer starts it — that is shared with the display task and started in `main()`.

## Sub-modules
- **input_manager_interrupts** (C) — button + rotary-encoder GPIO ISRs write `button_press_data`
  into `button_press_circular_buffer`; the FSM drains it in task context (keeps ISRs tiny). The
  three push buttons share `register_button()`; they differ only in active level and in whether
  the press edge is reported (BRAKE reports both, because the session lasts as long as it is held).
- **FiniteStateMachine** — `MainDynoState` (`IDLE` / `SETTINGS_MENU` / `IN_SESSION`) + settings
  sub-states; the state model is drawn at the top of `FiniteStateMachine.hpp`. Owns the LCD UI
  (`session_controller_to_display` messages) and target RPM editing. `Show*` methods each enter
  one screen and redraw it; `Handle*Input` methods each take one input and dispatch on state.

## Run() loop (per iteration)
Each step below is a method of the same name; all are edge-triggered against the `_prev*` fields,
so a steady state produces no queue traffic. `PublishStartupState()` runs once before the loop.

1. `_fsm.HandleUserInputs()` — process pending button/encoder events.
2. `PublishSessionTransition()` on a session start/stop edge (`GetInSessionStatus`): tell [[USB]]
   whether a session is running (it streams sensor data only then), reset the display, and stop
   [[BPM]] (`STOP_PWM`) on the way out. Sensor sampling itself is enabled once at startup and
   never gated, so a session starts against sensors that are already warm.

   There is **no USB-logging option**: USB streaming follows the session, and nothing can turn it
   off. There is no SD-logging option either — that menu page was removed, because no SD task
   exists to receive it (`SD_CONTROLLER_TASK_ENABLE` is 0 and the queue is `NULL`).

   Outside a session the iteration ends here — nothing below may drive an actuator.
3. `PublishPidInstruction()` — send `session_controller_to_pid_controller` (enable + desired ω)
   when **either** moves; `AwaitPidAck()` then waits for `pid_controller_ack` before pointing the
   BPM at the PID output. The setpoint is republished because it is a runtime sysconfig parameter
   the host can move mid-session, not only a menu value fixed before the run.
4. `DriveManualBrake()` — PID option off only: brake duty cycle to the BPM queue (`START_PWM`),
   clamped by the FSM to the same envelope `BPM::SetDutyCycle` enforces.
5. `UpdateMeasurementDisplay()` — drain to the newest `forcesensor_output_data` +
   `optical_encoder_output_data`, then push angular velocity and force to the LCD, each only when
   its value changed. An iteration with no new samples keeps the last reading.

## Queues out — `session_controller_os_task_queues`
`usb_controller, sd_controller, force_sensor, optical_sensor, bpm_controller, pid_controller, pid_controller_ack, display`

## Nothing is derived here
Torque and power used to be computed in this task and shown on the LCD. They are not: the device
streams what it *measures* — angular velocity, acceleration, force — and the host derives the rest
(`src/Dyno.Core/Derived/DerivedQuantities.cs`, protocol v6 onward).

The constants those formulas need — moment of inertia, lever arm, gear ratio — therefore live on
the PC, which is the point: a past run can be recomputed after correcting one, where a value baked
into the firmware at capture time would have been unrecoverable without a re-run.

The LCD still shows the two measured quantities directly, so the dyno reads out usefully with no
computer attached — it just no longer shows numbers it would have to derive to get.

## Errors
- `ERROR_SESSION_CONTROLLER_TIMESTAMP_TIMER_START_FAILURE`, `_INVALID_TASK_QUEUE_POINTER`.
  A failed `Init()` suspends the task.

## Related
[[BPM]] · [[PID]] · [[USB]] · [[LCD]] · [[ForceSensor]] · [[OpticalSensor]] · [[TimeKeeping]]
