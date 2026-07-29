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
- `HAL_StatusTypeDef start_timestamp_timer()` — starts the hardware timer. **Called once from
  `main()`**, in `USER CODE BEGIN 2`, before the scheduler starts. Tasks should assume the counter
  is already running and must not start it themselves. Idempotent regardless — see Behavior.
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
- **Who starts it:** `main()`, once, before any task exists. The counter is shared by
  [[SessionController]] (which stamps every sample) and [[Lumex display]] (whose enable pulse is
  timed against it), and owned by neither — so neither starts it.

  It used to be started by whichever task's `Init()` ran first, and that was a bug rather than a
  tidy piece of laziness. `HAL_TIM_Base_Start` returns `HAL_ERROR` whenever the handle is not in
  `READY` state, and a timer someone has already started is `BUSY` — it answers "already running"
  and "failed to start" with the same value. `SessionController` runs at `osPriorityHigh` against
  the display's `osPriorityBelowNormal`, so it always won; the display read the `HAL_ERROR`,
  logged `ERROR_DISPLAY_INIT_FAILURE` and suspended its own task, leaving a blank panel driven by
  a timer that was working perfectly. A shared resource started from a task's `Init()` makes
  startup depend on a scheduling race, whatever the HAL returns.
- **Starting twice:** still safe. `start_timestamp_timer()` checks `TIM2->CR1.CEN` and only calls
  `HAL_TIM_Base_Start` if the counter is stopped, so it asks the hardware whether the counter is
  running rather than asking the HAL whether this caller is the one who started it. A handle that
  was never initialised — `STM32_PERIPHERAL_TIM2_ENABLE 0` — still fails honestly: `CEN` stays
  clear and `HAL_ERROR` comes back.
- Clock-rate helpers may be inaccurate if the RCC tree gets more complex; revisit if clocks change.

## Related
[[SessionController]] · [[OpticalSensor]] · [[MessagePassing]]
