#ifndef INC_CONFIG_DEBUG_H_
#define INC_CONFIG_DEBUG_H_

// Peripheral enable/disables
// ===== GPIO =====
#define STM32_PERIPHERAL_GPIO_ENABLE      1

// ===== TIMERS =====
#define STM32_PERIPHERAL_TIM1_ENABLE      1
#define STM32_PERIPHERAL_TIM2_ENABLE      1
#define STM32_PERIPHERAL_TIM13_ENABLE     1
#define STM32_PERIPHERAL_TIM14_ENABLE     1
#define STM32_PERIPHERAL_TIM16_ENABLE     1

// ===== ADC =====
#define STM32_PERIPHERAL_ADC2_ENABLE      1
#define STM32_PERIPHERAL_ADC3_ENABLE      1

// ===== SPI =====
#define STM32_PERIPHERAL_SPI1_ENABLE      1
#define STM32_PERIPHERAL_SPI2_ENABLE      1

// ===== UART =====
#define STM32_PERIPHERAL_USART1_ENABLE    1

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

// Lumex LCD Task
#define LUMEX_LCD_TASK_ENABLE 1

// USB Controller task settings
#define USB_CONTROLLER_TASK_ENABLE 1
#define DEBUG_USB_CONTROLLER_MOCK_MESSAGES 0

// Led Blink Task
#define LED_BLINK_TASK_ENABLE 0

// Task Monitoring Task
#define TASK_MONITOR_TASK_ENABLE 1

#endif /* INC_CONFIG_DEBUG_H_ */
