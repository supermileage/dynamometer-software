---
module: ADS1115 driver
summary: I2C driver for the ADS1115 16-bit ADC, used by the on-board i2c force sensor.
code: [Drivers/ADS1115/ADS1115.hpp, Drivers/ADS1115/ADS1115.cpp, Drivers/ADS1115/ADS1115_main.h]
used_by: ForceSensor (ADS1115 variant)
related: [ForceSensor, Config]
---

# ADS1115 — I2C ADC driver

C++ driver for the TI ADS1115 16-bit ADC. Adapted from the Addicore/jrowberg
Arduino library, ported to the STM32 HAL I2C API.

## Construction
`ADS1115(I2C_HandleTypeDef* i2cHandle, uint8_t address = ADS1115_DEFAULT_ADDRESS)`

## Key methods
- Config: `setMultiplexer`, `setGain`, `setRate`, `setMode`, `setComparator*`, `setConversionReadyPinMode`.
- Convert (single-shot): `triggerConversion()` → wait for ALERT → `getConversion(int16_t&, false)`.
- Helpers: `getMvPerCount()`, `getMilliVolts()`, `testConnection()`.

## Register/setting defines
`ADS1115_main.h` — addresses, MUX/PGA/rate/comparator constants, `ADS1115_DEFAULT_ADDRESS`,
mV-per-count tables. `config.h` selects `ADS1115_SAMPLE_SPEED`.

## Notes
- The force-sensor task drives it in **single-shot** mode and waits on the ALERT/RDY GPIO
  (EXTI) rather than polling — see [[ForceSensor]].
- Upstream: https://github.com/addicore/ADS1115

## Related
[[ForceSensor]] · [[Config]]
