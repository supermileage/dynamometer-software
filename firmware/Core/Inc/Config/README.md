---
module: Config
summary: Compile-time tunables (config.h), task/peripheral enable flags (debug.h), and the runtime SysConfig store.
code:
  - Core/Inc/Config/config.h
  - Core/Inc/Config/debug.h
  - Core/Inc/Config/config_overrides.h
  - Core/Inc/Config/debug_overrides.h
  - Core/Inc/Config/sysconfig.h
  - Core/Src/Config/sysconfig.c
  - Core/Inc/Config/sysconfig_table.inc
related: [Core, SessionController, ForceSensor, USB, MessagePassing]
---

# Config — constants, feature flags & the runtime store

## config.h — tunables
- **Buffer sizes:** `*_CIRCULAR_BUFFER_SIZE` (optical/force/bpm), `TASK_ERROR_CIRCULAR_BUFFER_SIZE`, `USER_INPUT_CIRCULAR_BUFFER_SIZE`
- **Task periods:** `*_TASK_OSDELAY`, `TASK_WARNING_RETRY_OSDELAY`
- **PID:** `K_P` / `K_I` / `K_D`, `PID_MAX_OUTPUT`, `THROTTLE_GAIN`, `BRAKE_GAIN`, `PID_INITIAL_STATUS`
- **BPM limits:** `MIN_DUTY_CYCLE_PERCENT`, `MAX_DUTY_CYCLE_PERCENT`
- **Sensors:** `MAX_FORCE_LBF`, `ADS1115_RATE`, `NUM_APERTURES`, `VREF`, `OPTICAL_MAX_NUM_OVERFLOWS`
- Includes `ADS1115_main.h` for the driver's register/gain defines.

The physics constants (moment of inertia, lever arm, gear ratio) are **not** here. The device
streams what it measures and the host derives torque and power, so those live on the PC — see
[[SessionController]].

## debug.h — enable flags (0/1)
- **Per task:** `SESSION_CONTROLLER_TASK_ENABLE`, `USB_CONTROLLER_TASK_ENABLE`,
  `SD_CONTROLLER_TASK_ENABLE`, `BPM_CONTROLLER_TASK_ENABLE`, `PID_CONTROLLER_TASK_ENABLE`,
  `LUMEX_LCD_TASK_ENABLE`, `TASK_MONITOR_TASK_ENABLE`, `OPTICAL_ENCODER_TASK_ENABLE`,
  `FORCE_SENSOR_ADS1115_TASK_ENABLE`, `FORCE_SENSOR_ADC_TASK_ENABLE`
- **Peripherals:** e.g. `STM32_PERIPHERAL_I2C4_ENABLE`, `STM32_PERIPHERAL_SDMMC1_ENABLE`

`*_overrides.h` sit alongside both headers for local, uncommitted changes, so trying something on a
bench board does not mean editing a tracked file you then have to remember not to commit.

The USB mock-message stream was `DEBUG_USB_CONTROLLER_MOCK_MESSAGES` here; it is now the runtime
parameter `SYSCFG_USB_MOCK_MESSAGES` (below), so putting a board into it needs no rebuild.

## SysConfig — the runtime store (`sysconfig.h` / `sysconfig.c`)
The tunable quantities above (gains, task delays, thresholds) also exist as RAM values, seeded from
their `config.h` macros by `sysconfig_init()` before the scheduler starts. Tasks read the store every
loop iteration — `sysconfig_get_u32(SYSCFG_PID_TASK_OSDELAY)` rather than the macro — so a host write
(`USB_CMD_SET_SYSCONFIG`, handled by the [[USB]] task) takes effect on the task's next pass, with no
queueing or task wake-up involved.

- `sysconfig_init()` — seed every parameter with its `config.h` default; call once, before any task runs.
- `sysconfig_get_float(id)` / `sysconfig_get_u32(id)` — typed reads; unknown ids read as 0.
- `sysconfig_set_raw(id, raw)` — kind-aware range check (NaN/Inf rejected for floats); returns false
  and stores nothing for an unknown id or an out-of-range value.

**Concurrency:** each value is one 32-bit aligned word, and aligned 32-bit loads/stores are atomic on
the Cortex-M7, so a reader can never observe a torn value. Parameters are deliberately independent —
there is no multi-parameter transaction, so there is no lock to need.

**Persistence:** none on the board. The host keeps the values and re-pushes them after every
handshake; until then a freshly-booted board runs the `config.h` defaults.

**Ranges** are a fact about the value's type, not an opinion about a sensible setting: a millisecond
delay is a `uint16_t`, so `0..65535`; a retry count a `uint8_t`, so `0..255`. `type:` in the schema
stays uint32/float because that is the wire encoding. They exist to stop a value that cannot be
*represented*, not one that is a bad idea — a 0 ms delay (a task that stops yielding) is reachable
on purpose.

`sysconfig_table.inc` is **generated** from `messages_public.yaml`; edit the schema, not the table.
See [[MessagePassing]] and `firmware/tools/message_gen/README.md`.

## Invariants
- Enable **exactly one** force-sensor variant (`ADS1115` xor `ADC`); `main.c` `#error`s otherwise.
- These flags are read in `#if` guards throughout `main.c` and every task — flipping one
  adds/removes the task, its queue, and its queue-null checks at compile time.

## Related
[[Core]] · [[SessionController]] · [[ForceSensor]]
