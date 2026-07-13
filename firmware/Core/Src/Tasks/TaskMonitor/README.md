---
module: TaskMonitor
summary: Periodically reports every task's RTOS state + free stack to the USB stream.
code:
  - Core/Src/Tasks/TaskMonitor/TaskMonitor.cpp
  - Core/Inc/Tasks/TaskMonitor/TaskMonitor.hpp
  - Core/Inc/Tasks/TaskMonitor/taskmonitor_main.h
entry_point: taskmonitor_main()
task_offset: TASK_OFFSET_TASK_MONITOR
consumes: [thread handles of all tasks (taskmonitor_osthreadids)]
produces: [taskMonitorToUsbController queue (task_monitor_output_data), task_error_circular_buffer]
related: [USB, MessagePassing]
---

# TaskMonitor — per-task health

Every `TASK_MONITOR_TASK_OSDELAY` ms, samples each task's RTOS state and stack high-water
mark and forwards a `task_monitor_output_data` to [[USB]].

## Flow
1. `taskmonitor_main(osthreadids, toUsbQueue)` → construct, `Init()` (null-checks thread handles), `Run()`.
2. `GetTaskDataAndSendToUsbController(task_offset_t, osThreadId_t)` fills `{ timestamp, task_offset,
   task_state, free_bytes }` and queues it to the USB controller. Called once per enabled task + itself.

## Inputs — `taskmonitor_osthreadids`
Thread handles: `session_controller, usb_controller, sd_controller, force_sensor, optical_sensor,
bpm_controller, pid_controller, lumex_lcd`. The single `force_sensor` handle is whichever variant
is enabled; it's reported with the matching offset (`TASK_OFFSET_FORCE_SENSOR_ADS1115` or `_ADC`).

## Errors
- `ERROR_TASK_MONITOR_INVALID_THREAD_ID_POINTER` → `task_error_circular_buffer`.
- `vApplicationStackOverflowHook()` halts on stack overflow (if `configCHECK_FOR_STACK_OVERFLOW`).

## Related
[[USB]] · [[MessagePassing]]
