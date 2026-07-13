---
module: OpticalSensor
summary: Measures shaft angular velocity + acceleration by counting optical-encoder pulses.
code:
  - Core/Src/Tasks/OpticalSensor/OpticalSensor.cpp
  - Core/Inc/Tasks/OpticalSensor/OpticalSensor.hpp
  - Core/Inc/Tasks/OpticalSensor/opticalsensor_main.h
entry_point: opticalsensor_main()
task_offset: TASK_OFFSET_OPTICAL_ENCODER
consumes: [SessionController enable/disable queue, optical-encoder GPIO interrupt]
produces: [optical_encoder_circular_buffer, task_error_circular_buffer]
related: [SessionController, PID, TimeKeeping]
---

# OpticalSensor — angular velocity / acceleration

Counts pulses from a slotted optical encoder and derives angular velocity and acceleration,
writing `optical_encoder_output_data { timestamp, angular_velocity, raw_value, angular_acceleration }`
to `optical_encoder_circular_buffer` (consumed by [[PID]] and [[SessionController]]).

## Flow
1. `opticalsensor_main(enableQueue)` → construct, `Init()`, `Run()`.
2. GPIO EXTI calls `opticalsensor_input_interrupt()` on each aperture edge → increments a volatile pulse count.
3. `Run()` blocks on the SessionController enable queue; when enabled, every `OPTICAL_ENCODER_TASK_OSDELAY`:
   - critical-section snapshot + reset of the pulse count,
   - `CalculateAngularVelocity()` = `(counts / NUM_APERTURES) · 2π / Δt`, `Δt` from [[TimeKeeping]] (`get_timestamp` / `get_timestamp_scale`),
   - `CalculateAngularAcceleration()` from the previous sample,
   - write the sample to the buffer.

## Key constants (config.h)
- `NUM_APERTURES` (encoder slots), `OPTICAL_ENCODER_TASK_OSDELAY`,
  `OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE`, `OPTICAL_MAX_NUM_OVERFLOWS`

## Notes
- Pulse counting is in ISR context; the task snapshots under `taskENTER_CRITICAL()` to avoid races.
- `CalculateRPM()` exists as an alternative readout (rev/min).

## Related
[[SessionController]] · [[PID]] · [[TimeKeeping]]
