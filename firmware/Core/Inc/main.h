/* USER CODE BEGIN Header */
/**
  ******************************************************************************
  * @file           : main.h
  * @brief          : Header for main.c file.
  *                   This file contains the common defines of the application.
  ******************************************************************************
  * @attention
  *
  * Copyright (c) 2025 STMicroelectronics.
  * All rights reserved.
  *
  * This software is licensed under terms that can be found in the LICENSE file
  * in the root directory of this software component.
  * If no LICENSE file comes with this software, it is provided AS-IS.
  *
  ******************************************************************************
  */
/* USER CODE END Header */

/* Define to prevent recursive inclusion -------------------------------------*/
#ifndef __MAIN_H
#define __MAIN_H

#ifdef __cplusplus
extern "C" {
#endif

/* Includes ------------------------------------------------------------------*/
#include "stm32h7xx_hal.h"

/* Private includes ----------------------------------------------------------*/
/* USER CODE BEGIN Includes */

/* USER CODE END Includes */

/* Exported types ------------------------------------------------------------*/
/* USER CODE BEGIN ET */

/* USER CODE END ET */

/* Exported constants --------------------------------------------------------*/
/* USER CODE BEGIN EC */

/* USER CODE END EC */

/* Exported macro ------------------------------------------------------------*/
/* USER CODE BEGIN EM */

/* USER CODE END EM */

void HAL_TIM_MspPostInit(TIM_HandleTypeDef *htim);

/* Exported functions prototypes ---------------------------------------------*/
void Error_Handler(void);

/* USER CODE BEGIN EFP */

/* USER CODE END EFP */

/* Private defines -----------------------------------------------------------*/
#define ROT_EN_A_Pin GPIO_PIN_3
#define ROT_EN_A_GPIO_Port GPIOE
#define ROT_EN_A_EXTI_IRQn EXTI3_IRQn
#define ROT_EN_SW_Pin GPIO_PIN_4
#define ROT_EN_SW_GPIO_Port GPIOE
#define ROT_EN_SW_EXTI_IRQn EXTI4_IRQn
#define BTN_SELECT_Pin GPIO_PIN_5
#define BTN_SELECT_GPIO_Port GPIOE
#define BTN_SELECT_EXTI_IRQn EXTI9_5_IRQn
#define BTN_BACK_Pin GPIO_PIN_6
#define BTN_BACK_GPIO_Port GPIOE
#define BTN_BACK_EXTI_IRQn EXTI9_5_IRQn
#define ROT_EN_B_Pin GPIO_PIN_8
#define ROT_EN_B_GPIO_Port GPIOI
#define ADS1115_ALERT_Pin GPIO_PIN_11
#define ADS1115_ALERT_GPIO_Port GPIOI
#define ADS1115_ALERT_EXTI_IRQn EXTI15_10_IRQn
#define BPM_CTRL_MCU_Pin GPIO_PIN_6
#define BPM_CTRL_MCU_GPIO_Port GPIOF
#define ADC3_BPM_FB_Pin GPIO_PIN_7
#define ADC3_BPM_FB_GPIO_Port GPIOF
#define OP_IN_Pin GPIO_PIN_9
#define OP_IN_GPIO_Port GPIOF
#define OP_IN_EXTI_IRQn EXTI9_5_IRQn
#define ILI_SPI2_MOSI_Pin GPIO_PIN_1
#define ILI_SPI2_MOSI_GPIO_Port GPIOC
#define ILI_SPI2_MISO_Pin GPIO_PIN_2
#define ILI_SPI2_MISO_GPIO_Port GPIOC
#define LUMEX_LCD_D0_Pin GPIO_PIN_0
#define LUMEX_LCD_D0_GPIO_Port GPIOA
#define LUMEX_LCD_D1_Pin GPIO_PIN_1
#define LUMEX_LCD_D1_GPIO_Port GPIOA
#define LUMEX_LCD_D2_Pin GPIO_PIN_2
#define LUMEX_LCD_D2_GPIO_Port GPIOA
#define LUMEX_LCD_D3_Pin GPIO_PIN_3
#define LUMEX_LCD_D3_GPIO_Port GPIOA
#define LUMEX_LCD_D4_Pin GPIO_PIN_4
#define LUMEX_LCD_D4_GPIO_Port GPIOA
#define LUMEX_LCD_D5_Pin GPIO_PIN_5
#define LUMEX_LCD_D5_GPIO_Port GPIOA
#define LUMEX_LCD_D6_Pin GPIO_PIN_6
#define LUMEX_LCD_D6_GPIO_Port GPIOA
#define LUMEX_LCD_D7_Pin GPIO_PIN_7
#define LUMEX_LCD_D7_GPIO_Port GPIOA
#define LUMEX_LCD_EN_Pin GPIO_PIN_4
#define LUMEX_LCD_EN_GPIO_Port GPIOC
#define LUMEX_LCD_RS_Pin GPIO_PIN_5
#define LUMEX_LCD_RS_GPIO_Port GPIOC
#define ADC2_FORCE_IN_Pin GPIO_PIN_1
#define ADC2_FORCE_IN_GPIO_Port GPIOB
#define ILI_SPI2_SCK_Pin GPIO_PIN_10
#define ILI_SPI2_SCK_GPIO_Port GPIOB
#define ILI_SPI2_TOUCH_CS_Pin GPIO_PIN_6
#define ILI_SPI2_TOUCH_CS_GPIO_Port GPIOH
#define ILI_SPI2_SD_CS_Pin GPIO_PIN_7
#define ILI_SPI2_SD_CS_GPIO_Port GPIOH
#define ADS1115_I2C4_SCL_Pin GPIO_PIN_11
#define ADS1115_I2C4_SCL_GPIO_Port GPIOH
#define ADS1115_I2C4_SDA_Pin GPIO_PIN_12
#define ADS1115_I2C4_SDA_GPIO_Port GPIOH
#define ILI_LCD_DC_Pin GPIO_PIN_5
#define ILI_LCD_DC_GPIO_Port GPIOD
#define ILI_LCD_RST_Pin GPIO_PIN_6
#define ILI_LCD_RST_GPIO_Port GPIOD
#define ILI_SPI1_MOSI_Pin GPIO_PIN_7
#define ILI_SPI1_MOSI_GPIO_Port GPIOD
#define ILI_SPI1_MISO_Pin GPIO_PIN_9
#define ILI_SPI1_MISO_GPIO_Port GPIOG
#define ILI_SPI1_LCD_CS_Pin GPIO_PIN_10
#define ILI_SPI1_LCD_CS_GPIO_Port GPIOG
#define ILI_SPI1_SCK_Pin GPIO_PIN_11
#define ILI_SPI1_SCK_GPIO_Port GPIOG
#define ILI_TOUCH_IRQ_Pin GPIO_PIN_14
#define ILI_TOUCH_IRQ_GPIO_Port GPIOG
#define ILI_TOUCH_IRQ_EXTI_IRQn EXTI15_10_IRQn
#define LED_BACK_Pin GPIO_PIN_4
#define LED_BACK_GPIO_Port GPIOI
#define LED_SELECT_Pin GPIO_PIN_5
#define LED_SELECT_GPIO_Port GPIOI
#define LED_BRAKE_Pin GPIO_PIN_6
#define LED_BRAKE_GPIO_Port GPIOI
#define BTN_BRAKE_Pin GPIO_PIN_7
#define BTN_BRAKE_GPIO_Port GPIOI
#define BTN_BRAKE_EXTI_IRQn EXTI9_5_IRQn

/* USER CODE BEGIN Private defines */

/* USER CODE END Private defines */

#ifdef __cplusplus
}
#endif

#endif /* __MAIN_H */
