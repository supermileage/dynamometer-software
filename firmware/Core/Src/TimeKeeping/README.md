---
module: TimeKeeping
summary: Free-running microsecond timestamps used to tag all logged samples.
code: [Core/Inc/TimeKeeping/timestamps.h, Core/Src/TimeKeeping/timestamps.c]
api: [get_timestamp(), start_timestamp_timer(), get_timestamp_scale(), get_timer_clock()]
related: [SessionController, MessagePassing]
---

# TimeKeeping — microsecond timestamps

Provides the monotonic timestamp stamped onto every sensor / error / monitor record.

## API (timestamps.h)
- `uint32_t get_timestamp()` — current tick, `0 .. UINT32_MAX`.
- `HAL_StatusTypeDef start_timestamp_timer()` — starts the hardware timer. **Idempotent**: safe to
  call from more than one task's `Init()`, and returns `HAL_OK` if the counter is already running.
  Called by [[SessionController]] and, when that panel is fitted, by [[Lumex display]] — whose
  enable pulse is measured against this counter, so it cannot assume the session controller is
  compiled in. See Behavior below for why the idempotence is not free.
- `get_timestamp_scale()`, `get_apb1_timer_clock()`, `get_apb2_timer_clock()`, `get_timer_clock(TIMx)` — clock-rate helpers (used by OpticalSensor to convert ticks → seconds).

## Behavior
- **Resolution:** 1 tick = 1 µs. TIM2 runs off a 100 MHz APB1 clock, doubled to a 200 MHz timer
  clock, divided by a prescaler of 200. Derive the rate from `get_timestamp_scale()` rather than
  assuming 1 MHz — it is computed from the live clock tree, so it survives a CubeMX clock or
  prescaler change that a hard-coded constant would not.
- **Range:** the counter wraps every 2³² µs ≈ 71.6 minutes. There is no wrap *counter* — consumers
  handle it themselves, and both that matter already do:
  - [[OpticalSensor]] measures intervals with unsigned subtraction, which stays correct across a
    wrap (`firmware/tests/encoder_math_tests.cpp` proves it).
  - The host unwraps the stream into a monotonic timeline (`TimestampUnwrapper`,
    `src/Dyno.Core/README.md`), which is what lets a run longer than 71.6 minutes plot and export
    correctly.

  Anything new that measures across timestamps must do the same; a signed difference is the bug
  this note exists to prevent.
- **Starting twice:** `start_timestamp_timer()` checks `TIM2->CR1.CEN` and only calls
  `HAL_TIM_Base_Start` if the counter is stopped. That wrapper is not decoration. The HAL call
  returns `HAL_ERROR` whenever the handle is not in `READY` state, and a timer someone has already
  started is `BUSY` — so it reports "already running" and "failed to start" with the same value.
  The Lumex display took that at face value, logged `ERROR_DISPLAY_INIT_FAILURE` and suspended its
  own task, leaving a blank panel driven by a timer that was working perfectly. Asking the hardware
  whether the counter is running answers the question the callers are actually asking. A handle
  that was never initialised still fails: `CEN` stays clear and `HAL_ERROR` comes back.
- Clock-rate helpers may be inaccurate if the RCC tree gets more complex; revisit if clocks change.

## Related
[[SessionController]] · [[OpticalSensor]] · [[MessagePassing]]
