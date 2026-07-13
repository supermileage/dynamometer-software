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
- `HAL_StatusTypeDef start_timestamp_timer()` — starts the hardware timer; called once by [[SessionController]] in `Init()`.
- `get_timestamp_scale()`, `get_apb1_timer_clock()`, `get_apb2_timer_clock()`, `get_timer_clock(TIMx)` — clock-rate helpers (used by OpticalSensor to convert ticks → seconds).

## Behavior
- **Resolution:** 1 tick = 1 µs.
- **Range:** wraps after ~1.2 h (`UINT32_MAX` µs). Overflow is **not** handled yet — consumers must tolerate wrap.
- Clock-rate helpers may be inaccurate if the RCC tree gets more complex; revisit if clocks change.

## Related
[[SessionController]] · [[OpticalSensor]] · [[MessagePassing]]
