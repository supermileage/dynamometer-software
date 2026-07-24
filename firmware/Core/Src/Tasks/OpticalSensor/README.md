---
module: OpticalSensor
summary: Measures shaft angular velocity + acceleration by counting optical-encoder pulses.
code:
  - Core/Src/Tasks/OpticalSensor/OpticalSensor.cpp
  - Core/Inc/Tasks/OpticalSensor/OpticalSensor.hpp
  - Core/Inc/Tasks/OpticalSensor/opticalsensor_main.h
  - Core/Src/Tasks/OpticalSensor/encoder_math.c
  - Core/Inc/Tasks/OpticalSensor/encoder_math.h
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
2. GPIO EXTI calls `opticalsensor_input_interrupt()` on each aperture edge → increments a volatile
   pulse count **and stamps the edge** into `last_pulse_timestamp`.
3. `Run()` blocks on the SessionController enable queue; when enabled, every
   `SYSCFG_OPTICAL_ENCODER_TASK_OSDELAY`:
   - critical-section snapshot + reset of the pulse count, the last-pulse stamp and `now`,
   - `encoder_angular_velocity()` over the interval between the *previous* pass's last pulse and
     this one's,
   - `encoder_angular_acceleration()` against the previous sample,
   - write the sample to the buffer, and adopt this pass's last pulse as the next reference.

## Measured between pulse edges, not per task period
The interval is bounded by real pulse edges rather than by when the task happened to run. Over an
interval that starts and ends on a pulse the angle travelled is exactly `counts · 2π/apertures` —
no partial aperture at either end — so the only error left is the timestamps' 1 µs resolution.
Sampling on a fixed task period instead leaves a ±1 count ambiguity, which at 64 apertures and a
10 ms window is ±9.8 rad/s regardless of how fast the shaft is actually turning.

Three cases fall out of that, all in `Run()`:
- **Pulses, and a reference edge** — the normal path; `Δt = lastPulse − reference`, computed with
  unsigned subtraction so it stays correct across a counter wrap.
- **Pulses, but no reference yet** (first pass, or just re-enabled) — adopt the edge and report
  nothing derived from it. There is nothing to measure against; the old code used 0 here, so the
  first sample was divided by the whole time since boot.
- **No pulses** — the shaft has not covered another aperture, so `encoder_velocity_upper_bound()`
  reports the fastest it *could* be turning given the silence, a bound that decays toward zero on
  its own instead of snapping there. Below ~94 RPM the fixed-window counter used to flap between
  "stopped" and a full quantum.

The enable flag drops the reference with it: the ISR keeps counting while disabled, so resuming
against a stale edge would attribute a whole idle period's pulses to one interval.

## Arithmetic (`encoder_math.h`)
The three functions above are pure — no HAL, no FreeRTOS, no globals — so the host test suite
(`firmware/tests/encoder_math_tests.cpp`) compiles and proves them directly. The task owns the
timing and the hardware; `encoder_math` owns the numbers, which are the part worth proving.

## Key parameters
- Runtime ([[Config]] SysConfig): `SYSCFG_NUM_APERTURES` (encoder slots),
  `SYSCFG_OPTICAL_ENCODER_TASK_OSDELAY`
- Compile-time (config.h): `OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE`

## Notes
- Pulse count and edge stamp are written in ISR context; the task snapshots both under
  `taskENTER_CRITICAL()` so they cannot disagree about which pulses the stamp belongs to.
- Angular velocity is reported in rad/s. Torque and power are **not** derived here, nor anywhere
  on the device — the host computes them from this stream (see `src/Dyno.Core/README.md`).

## Related
[[SessionController]] · [[PID]] · [[TimeKeeping]]
