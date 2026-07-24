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
produces: [commands to usb/sd/bpm/pid/lumex/force_sensor/optical_sensor queues, task_error_circular_buffer]
related: [BPM, PID, USB, LCD, ForceSensor, OpticalSensor, TimeKeeping]
---

# SessionController — orchestrator

The top-level task. Starts the timestamp timer, validates every queue handle, runs the
UI/FSM, dispatches commands to all other tasks, and drives the LCD readout.

## Sub-modules
- **input_manager_interrupts** (C) — button + rotary-encoder GPIO ISRs write `button_press_data`
  into `button_press_circular_buffer`; the FSM drains it in task context (keeps ISRs tiny).
- **FiniteStateMachine** — `MainDynoState` (`IDLE` / `SETTINGS_MENU` / `IN_SESSION`) + settings
  sub-states. Owns the LCD UI (`session_controller_to_lumex_lcd` messages) and target RPM editing.

## Run() loop (per iteration)
1. `_fsm.HandleUserInputs()` — process pending button/encoder events.
2. Edge-detect SD-logging enable → notify the SD queue only on change.
3. Session start/stop edge (`GetInSessionStatus`): tell [[USB]] whether a session is running (it
   streams sensor data only then), reset the display, and command [[BPM]] (`START_PWM` /
   `STOP_PWM`). Sensor sampling itself is enabled once at startup and never gated, so a session
   starts against sensors that are already warm.

   There is **no USB-logging option**: USB streaming follows the session, and nothing can turn it
   off. (SD logging remains a togglable setting in the menu.)
4. On PID enable change: send `session_controller_to_pid_controller` (enable + desired ω); await `pid_controller_ack`.
5. Manual mode: push throttle/BPM duty cycle to the BPM queue.
6. Drain latest `forcesensor_output_data` + `optical_encoder_output_data` (via `GetLatestFromQueue`-style buffer reads).
7. Push angular velocity and force to the LCD, each only when its value changed.

## Queues out — `session_controller_os_task_queues`
`usb_controller, sd_controller, force_sensor, optical_sensor, bpm_controller, pid_controller, pid_controller_ack, lumex_lcd`

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
- `ERROR_SESSION_CONTROLLER_TIMESTAMP_TIMER_START_FAILURE`, `_INVALID_TASK_QUEUE_POINTER`, `_INVALID_UART1_MUTEX_POINTER`.
  A failed `Init()` suspends the task.

## Related
[[BPM]] · [[PID]] · [[USB]] · [[LCD]] · [[ForceSensor]] · [[OpticalSensor]] · [[TimeKeeping]]
