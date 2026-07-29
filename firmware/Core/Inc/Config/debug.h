#ifndef INC_CONFIG_DEBUG_H_
#define INC_CONFIG_DEBUG_H_

// Peripheral enable/disables
// ===== GPIO =====
#define STM32_PERIPHERAL_GPIO_ENABLE      1

// ===== TIMERS =====
#define STM32_PERIPHERAL_TIM1_ENABLE      1
#define STM32_PERIPHERAL_TIM2_ENABLE      1
#define STM32_PERIPHERAL_TIM14_ENABLE     1
#define STM32_PERIPHERAL_TIM16_ENABLE     1

// ===== ADC =====
#define STM32_PERIPHERAL_ADC2_ENABLE      1
#define STM32_PERIPHERAL_ADC3_ENABLE      1

// ===== SPI =====
#define STM32_PERIPHERAL_SPI1_ENABLE      1
#define STM32_PERIPHERAL_SPI2_ENABLE      1

// ===== SDMMC / STORAGE =====
#define STM32_PERIPHERAL_SDMMC1_ENABLE    0

// ===== I2C =====
#define STM32_PERIPHERAL_I2C4_ENABLE      1


// Task enable/disables
#define FORCE_SENSOR_ADS1115_TASK_ENABLE 1

// Force Sensor ADC Task
#define FORCE_SENSOR_ADC_TASK_ENABLE 0

// Force Sensor Task
#define OPTICAL_ENCODER_TASK_ENABLE 1

// Session Controller Task
#define SESSION_CONTROLLER_TASK_ENABLE 1

// SD Controller Task
#define SD_CONTROLLER_TASK_ENABLE 0

// PID Controller Task
#define PID_CONTROLLER_TASK_ENABLE 1

// BPM Controller Task
#define BPM_CONTROLLER_TASK_ENABLE 1

// Display task -- at most one driver, chosen here and flashed.
//
// Both panels consume the same session_controller_to_display message, so the SessionController
// and its FSM are identical either way; only the driver linked in changes. There is no runtime
// switch because there is no runtime question: a board has one panel soldered to it.
//
// ROT_EN_B (PI8) had no pull resistor while every other user input had one, so the direction
// bit the encoder ISR samples came off a floating pin. It read correctly while SPI1 was idle
// and randomly once the panel drove it, which is why the brake random-walked to 0% only on this
// branch and only while the encoder was turning. Fixed in the .ioc; the panel is back on.
#define LUMEX_LCD_TASK_ENABLE   0
#define ILI9341_LCD_TASK_ENABLE 1

#if (LUMEX_LCD_TASK_ENABLE + ILI9341_LCD_TASK_ENABLE) > 1
#error "At most one display driver may be enabled: set at most one of LUMEX_LCD_TASK_ENABLE / ILI9341_LCD_TASK_ENABLE to 1."
#endif

// "A display task exists", which is what everything outside the two drivers actually wants to
// know: null checks on the display queue and thread id, and the task monitor's stack-usage
// report. Those must not be gated on one panel's own enable -- selecting the other panel then
// silently compiles them out, which is exactly what happened when this board moved to the
// ILI9341 and took the display task's stack high-water mark off the USB stream with it.
#define DISPLAY_TASK_ENABLE (LUMEX_LCD_TASK_ENABLE || ILI9341_LCD_TASK_ENABLE)

// USB Controller task settings
// The mock-message stream used to live here as DEBUG_USB_CONTROLLER_MOCK_MESSAGES. It is now the
// runtime parameter SYSCFG_USB_MOCK_MESSAGES (schema: sysconfig_params), so exercising the link
// no longer costs a rebuild and a flash -- and a board running fabricated data now says so over
// USB instead of looking like any other build.
#define USB_CONTROLLER_TASK_ENABLE 1

// Led Blink Task
//
// It has no LED of its own: it blinks by toggling ILI_SPI2_SD_CS (PH7), which is the microSD
// slot's chip select on the ILI9341 module. That was picked as a convenient scope point back
// when nothing else used the pin. It is not a free pin once that module is fitted, so the
// check below refuses the combination rather than leaving someone to find it with a scope.
#define LED_BLINK_TASK_ENABLE 0

// Guarded on definedness too: an undefined macro is 0 to the preprocessor, so moving either
// #define below this point would silently switch the check off rather than break the build.
#if !defined(ILI9341_LCD_TASK_ENABLE)
#error "ILI9341_LCD_TASK_ENABLE must be defined above LED_BLINK_TASK_ENABLE -- the pin-conflict check below reads it."
#endif

#if LED_BLINK_TASK_ENABLE && ILI9341_LCD_TASK_ENABLE
#error "LED_BLINK_TASK_ENABLE toggles ILI_SPI2_SD_CS (PH7), a chip select on the ILI9341 module. Disable one of LED_BLINK_TASK_ENABLE / ILI9341_LCD_TASK_ENABLE, or point the blink task at a pin of its own."
#endif

// Task Monitoring Task
#define TASK_MONITOR_TASK_ENABLE 1

// See the note at the foot of config.h: the desktop app's overrides land here, applied last so they
// win over the defaults above. Generated, git-ignored, and absent unless something is overridden.
#if __has_include("debug_overrides.h")
#include "debug_overrides.h"
#endif

#endif /* INC_CONFIG_DEBUG_H_ */
