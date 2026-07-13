---
module: ForceSensor
summary: On-board force measurement — two interchangeable variants (i2c ADS1115 or internal ADC).
code:
  - Core/Src/Tasks/ForceSensor/ADS1115/ForceSensor_ADS1115.cpp
  - Core/Inc/Tasks/ForceSensor/ADS1115/ForceSensor_ADS1115.hpp
  - Core/Inc/Tasks/ForceSensor/ADS1115/forcesensor_ads1115_main.h
  - Core/Src/Tasks/ForceSensor/ADC/ForceSensor_ADC.cpp
  - Core/Inc/Tasks/ForceSensor/ADC/ForceSensor_ADC.hpp
  - Core/Inc/Tasks/ForceSensor/ADC/forcesensor_adc_main.h
entry_point: forcesensor_ads1115_main() | forcesensor_adc_main()
task_offset: [TASK_OFFSET_FORCE_SENSOR_ADS1115, TASK_OFFSET_FORCE_SENSOR_ADC]
consumes: [SessionController enable/disable queue]
produces: [forcesensor_circular_buffer, task_error_circular_buffer]
related: [SessionController, ADS1115 driver, MessagePassing]
---

# ForceSensor — on-board force (ADS1115 or ADC)

Two **interchangeable** implementations of the same job: read the load cell, convert to
Newtons, and write `forcesensor_output_data { timestamp, force, raw_value }` to the shared
`forcesensor_circular_buffer`. Enable **exactly one** via `debug.h`
(`FORCE_SENSOR_ADS1115_TASK_ENABLE` xor `FORCE_SENSOR_ADC_TASK_ENABLE`). They have **distinct
task offsets** because their failure modes differ.

Both block on the SessionController enable queue (`GetLatestFromQueue`); when disabled they
idle, when enabled they sample every `FORCESENSOR_TASK_OSDELAY` and convert via
`force = f(raw) … * MAX_FORCE_LBF * LBF_TO_NEWTON`.

## ADS1115 variant (i2c) — `TASK_OFFSET_FORCE_SENSOR_ADS1115`
- Uses the [[ADS1115 driver]] over HAL I2C (`forceSensorADS1115Handle`), single-shot at `ADS1115_SAMPLE_SPEED`.
- `triggerConversion()` → wait on the ALERT/RDY GPIO (EXTI sets `ads1115_alert_status`) → `getConversion()`.
- Errors: `ERROR_FORCE_SENSOR_ADS1115_INIT_FAILURE`;
  warnings `WARNING_FORCE_SENSOR_ADS1115_TRIGGER_CONVERSION_FAILURE`, `_GET_CONVERSION_FAILURE`.

## ADC variant (internal) — `TASK_OFFSET_FORCE_SENSOR_ADC`
- Uses the STM32 internal ADC (`forceSensorADCHandle`); `HAL_ADC_Start_IT` → conversion-complete
  ISR (`forcesensor_adc_interrupt`) latches the value.
- Error: `ERROR_FORCE_SENSOR_ADC_START_FAILURE`.

## Key constants (config.h)
- `MAX_FORCE_LBF`, `FORCESENSOR_TASK_OSDELAY`, `FORCESENSOR_CIRCULAR_BUFFER_SIZE`, `ADS1115_SAMPLE_SPEED`

## Related
[[SessionController]] · [[ADS1115 driver]] · [[MessagePassing]]
