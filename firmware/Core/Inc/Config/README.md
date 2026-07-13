---
module: Config
summary: Compile-time tunables (config.h) and task/peripheral enable flags (debug.h).
code: [Core/Inc/Config/config.h, Core/Inc/Config/debug.h]
related: [Core, SessionController, ForceSensor]
---

# Config — constants & feature flags

## config.h — tunables
- **Buffer sizes:** `*_CIRCULAR_BUFFER_SIZE` (optical/force/bpm), `TASK_ERROR_CIRCULAR_BUFFER_SIZE`, `USER_INPUT_CIRCULAR_BUFFER_SIZE`
- **Task periods:** `*_TASK_OSDELAY`, `TASK_WARNING_RETRY_OSDELAY`
- **PID:** `K_P` / `K_I` / `K_D`, `PID_MAX_OUTPUT`, `THROTTLE_GAIN`, `BRAKE_GAIN`, `PID_INITIAL_STATUS`
- **BPM limits:** `MIN_DUTY_CYCLE_PERCENT`, `MAX_DUTY_CYCLE_PERCENT`
- **Sensors:** `MAX_FORCE_LBF`, `ADS1115_SAMPLE_SPEED`, `NUM_APERTURES`, `OPTICAL_MAX_NUM_OVERFLOWS`
- **Physics:** `MOMENT_OF_INERTIA_KG_M2`, `DISTANCE_FROM_FORCE_SENSOR_TO_CENTER_OF_SHAFT_M`, `VREF`
- Includes `ADS1115_main.h` for the driver's register/gain defines.

## debug.h — enable flags (0/1)
- **Per task:** `SESSION_CONTROLLER_TASK_ENABLE`, `USB_CONTROLLER_TASK_ENABLE`,
  `SD_CONTROLLER_TASK_ENABLE`, `BPM_CONTROLLER_TASK_ENABLE`, `PID_CONTROLLER_TASK_ENABLE`,
  `LUMEX_LCD_TASK_ENABLE`, `TASK_MONITOR_TASK_ENABLE`, `OPTICAL_ENCODER_TASK_ENABLE`,
  `FORCE_SENSOR_ADS1115_TASK_ENABLE`, `FORCE_SENSOR_ADC_TASK_ENABLE`
- **Peripherals:** e.g. `STM32_PERIPHERAL_I2C4_ENABLE`, `STM32_PERIPHERAL_SDMMC1_ENABLE`
- `DEBUG_*` switches (e.g. USB mock-message mode).

## Invariants
- Enable **exactly one** force-sensor variant (`ADS1115` xor `ADC`); `main.c` `#error`s otherwise.
- These flags are read in `#if` guards throughout `main.c` and every task — flipping one
  adds/removes the task, its queue, and its queue-null checks at compile time.

## Related
[[Core]] · [[SessionController]] · [[ForceSensor]]
