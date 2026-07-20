/* USER CODE BEGIN Header */
/**
  ******************************************************************************
  * @file           : main.c
  * @brief          : Main program body
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
/* Includes ------------------------------------------------------------------*/
#include "main.h"
#include "cmsis_os.h"
#include "usb_device.h"

/* Private includes ----------------------------------------------------------*/
/* USER CODE BEGIN Includes */
#include <Tasks/BPM/bpm_main.h>
#include <Tasks/ForceSensor/ADC/forcesensor_adc_main.h>
#include <Tasks/ForceSensor/ADS1115/forcesensor_ads1115_main.h>
#include <Tasks/LCD/lumexlcd_main.h>
#include <Tasks/PID/pid_main.h>
#include <Tasks/OpticalSensor/opticalsensor_main.h>

#include <Tasks/SessionController/input_manager_interrupts.h>
#include <Tasks/SessionController/sessioncontroller_main.h>

#include <Tasks/TaskMonitor/taskmonitor_main.h>

#include <Tasks/USB/usbcontroller_main.h>

#include <Config/debug.h>
#include <Config/sysconfig.h>

#include <MessagePassing/messages_private.h>

#include <TimeKeeping/timestamps.h>

/* USER CODE END Includes */

/* Private typedef -----------------------------------------------------------*/
/* USER CODE BEGIN PTD */

/* USER CODE END PTD */

/* Private define ------------------------------------------------------------*/
/* USER CODE BEGIN PD */

/* USER CODE END PD */

/* Private macro -------------------------------------------------------------*/
/* USER CODE BEGIN PM */

/* USER CODE END PM */

/* Private variables ---------------------------------------------------------*/
ADC_HandleTypeDef hadc2;
ADC_HandleTypeDef hadc3;

I2C_HandleTypeDef hi2c4;

SD_HandleTypeDef hsd1;

SPI_HandleTypeDef hspi1;
SPI_HandleTypeDef hspi2;

TIM_HandleTypeDef htim1;
TIM_HandleTypeDef htim2;
TIM_HandleTypeDef htim13;
TIM_HandleTypeDef htim16;

UART_HandleTypeDef huart1;

/* Definitions for usbTask */
osThreadId_t usbTaskHandle;
const osThreadAttr_t usbTask_attributes = {
  .name = "usbTask",
  .stack_size = 512 * 4,
  .priority = (osPriority_t) osPriorityHigh,
};
/* Definitions for bpmTask */
osThreadId_t bpmTaskHandle;
const osThreadAttr_t bpmTask_attributes = {
  .name = " bpmTask",
  .stack_size = 128 * 4,
  .priority = (osPriority_t) osPriorityHigh,
};
/* Definitions for forceSensorTask */
osThreadId_t forceSensorTaskHandle;
const osThreadAttr_t forceSensorTask_attributes = {
  .name = " forceSensorTask",
  .stack_size = 256 * 4,
  .priority = (osPriority_t) osPriorityAboveNormal,
};
/* Definitions for pidTask */
osThreadId_t pidTaskHandle;
const osThreadAttr_t pidTask_attributes = {
  .name = " pidTask",
  .stack_size = 256 * 4,
  .priority = (osPriority_t) osPriorityHigh,
};
/* Definitions for opticalSensorTask */
osThreadId_t opticalSensorTaskHandle;
const osThreadAttr_t opticalSensorTask_attributes = {
  .name = " opticalSensorTask",
  .stack_size = 256 * 4,
  .priority = (osPriority_t) osPriorityAboveNormal,
};
/* Definitions for sessionControllerTask */
osThreadId_t sessionControllerTaskHandle;
const osThreadAttr_t sessionControllerTask_attributes = {
  .name = "sessionControllerTask",
  .stack_size = 256 * 4,
  .priority = (osPriority_t) osPriorityHigh,
};
/* Definitions for lcdDisplayTask */
osThreadId_t lcdDisplayTaskHandle;
const osThreadAttr_t lcdDisplayTask_attributes = {
  .name = "lcdDisplayTask",
  .stack_size = 128 * 4,
  .priority = (osPriority_t) osPriorityBelowNormal,
};
/* Definitions for ledBlinkTask */
osThreadId_t ledBlinkTaskHandle;
const osThreadAttr_t ledBlinkTask_attributes = {
  .name = "ledBlinkTask",
  .stack_size = 128 * 4,
  .priority = (osPriority_t) osPriorityLow,
};
/* Definitions for taskMonitorTask */
osThreadId_t taskMonitorTaskHandle;
const osThreadAttr_t taskMonitorTask_attributes = {
  .name = "taskMonitorTask",
  .stack_size = 128 * 4,
  .priority = (osPriority_t) osPriorityHigh,
};
/* Definitions for sessionControllerToLumexLcd */
osMessageQueueId_t sessionControllerToLumexLcdHandle;
const osMessageQueueAttr_t sessionControllerToLumexLcd_attributes = {
  .name = "sessionControllerToLumexLcd"
};
/* Definitions for sessionControllerToBpm */
osMessageQueueId_t sessionControllerToBpmHandle;
const osMessageQueueAttr_t sessionControllerToBpm_attributes = {
  .name = " sessionControllerToBpm"
};
/* Definitions for sessionControllerToForceSensor */
osMessageQueueId_t sessionControllerToForceSensorHandle;
const osMessageQueueAttr_t sessionControllerToForceSensor_attributes = {
  .name = " sessionControllerToForceSensor"
};
/* Definitions for sessionControllerToPidController */
osMessageQueueId_t sessionControllerToPidControllerHandle;
const osMessageQueueAttr_t sessionControllerToPidController_attributes = {
  .name = " sessionControllerToPidController"
};
/* Definitions for opticalEncoderToPidController */
osMessageQueueId_t opticalEncoderToPidControllerHandle;
const osMessageQueueAttr_t opticalEncoderToPidController_attributes = {
  .name = " opticalEncoderToPidController"
};
/* Definitions for pidControllerToBpm */
osMessageQueueId_t pidControllerToBpmHandle;
const osMessageQueueAttr_t pidControllerToBpm_attributes = {
  .name = " pidControllerToBpm"
};
/* Definitions for sessionControllerToOpticalSensor */
osMessageQueueId_t sessionControllerToOpticalSensorHandle;
const osMessageQueueAttr_t sessionControllerToOpticalSensor_attributes = {
  .name = " sessionControllerToOpticalSensor"
};
/* Definitions for sessionControllertoUsbController */
osMessageQueueId_t sessionControllertoUsbControllerHandle;
const osMessageQueueAttr_t sessionControllertoUsbController_attributes = {
  .name = "sessionControllertoUsbController"
};
/* Definitions for taskMonitorToUsbController */
osMessageQueueId_t taskMonitorToUsbControllerHandle;
const osMessageQueueAttr_t taskMonitorToUsbController_attributes = {
  .name = "taskMonitorToUsbController"
};
/* Definitions for usbToForceSensorCommand (host settings routed to the force sensor) */
osMessageQueueId_t usbToForceSensorCommandHandle;
const osMessageQueueAttr_t usbToForceSensorCommand_attributes = {
  .name = "usbToForceSensorCommand"
};
/* Definitions for taskToUsbController (applied-command acks back to the host) */
osMessageQueueId_t taskToUsbControllerResponseHandle;
const osMessageQueueAttr_t taskToUsbControllerResponse_attributes = {
  .name = "taskToUsbControllerResponse"
};
/* Definitions for pidControllerToSessionControllerAck */
osMessageQueueId_t pidControllerToSessionControllerAckHandle;
const osMessageQueueAttr_t pidControllerToSessionControllerAck_attributes = {
  .name = "pidControllerToSessionControllerAck"
};
/* Definitions for usart1Mutex */
osMutexId_t usart1MutexHandle;
const osMutexAttr_t usart1Mutex_attributes = {
  .name = "usart1Mutex"
};
/* USER CODE BEGIN PV */
// Force sensor ADC Handle
ADC_HandleTypeDef* forceSensorADCHandle = &hadc2;

// Force sensor ADS1115 I2C Handle
I2C_HandleTypeDef* forceSensorADS1115Handle = &hi2c4;

TIM_HandleTypeDef* timestampTimer = &htim2;

TIM_HandleTypeDef* lumexLcdTimer = &htim13;

TIM_TypeDef* lumexLcdTimInstance = TIM13;

TIM_HandleTypeDef* bpmTimer = &htim16;

/* USER CODE END PV */

/* Private function prototypes -----------------------------------------------*/
void SystemClock_Config(void);
void PeriphCommonClock_Config(void);
static void MPU_Config(void);
static void MX_GPIO_Init(void);
static void MX_TIM16_Init(void);
static void MX_SDMMC1_SD_Init(void);
static void MX_USART1_UART_Init(void);
static void MX_SPI1_Init(void);
static void MX_SPI2_Init(void);
static void MX_TIM1_Init(void);
static void MX_TIM13_Init(void);
static void MX_ADC2_Init(void);
static void MX_TIM2_Init(void);
static void MX_ADC3_Init(void);
static void MX_I2C4_Init(void);
void usbTaskEntryFunction(void *argument);
extern void bpmTaskEntryFunction(void *argument);
extern void forceSensorTaskEntryFunction(void *argument);
extern void pidControllerTaskEntryFunction(void *argument);
extern void opticalSensorTaskEntryFunction(void *argument);
extern void sessionControllerTaskEntryFunction(void *argument);
extern void lcdDisplayTaskEntryFunction(void *argument);
extern void ledBlinkTaskEntryFunction(void *argument);
extern void taskMonitorEntryFunction(void *argument);

/* USER CODE BEGIN PFP */

/* USER CODE END PFP */

/* Private user code ---------------------------------------------------------*/
/* USER CODE BEGIN 0 */

/* USER CODE END 0 */

/**
  * @brief  The application entry point.
  * @retval int
  */
int main(void)
{

  /* USER CODE BEGIN 1 */

  /* USER CODE END 1 */

  /* MPU Configuration--------------------------------------------------------*/
  MPU_Config();

  /* MCU Configuration--------------------------------------------------------*/

  /* Reset of all peripherals, Initializes the Flash interface and the Systick. */
  HAL_Init();

  /* USER CODE BEGIN Init */

  /* USER CODE END Init */

  /* Configure the system clock */
  SystemClock_Config();

  /* Configure the peripherals common clocks */
  PeriphCommonClock_Config();

  /* USER CODE BEGIN SysInit */

  /* USER CODE END SysInit */

  /* Initialize all configured peripherals */
  MX_GPIO_Init();
  MX_TIM16_Init();
  MX_SDMMC1_SD_Init();
  MX_USART1_UART_Init();
  MX_SPI1_Init();
  MX_SPI2_Init();
  MX_TIM1_Init();
  MX_TIM13_Init();
  MX_ADC2_Init();
  MX_TIM2_Init();
  MX_ADC3_Init();
  MX_I2C4_Init();
  /* USER CODE BEGIN 2 */
  HAL_GPIO_WritePin(LED_BRAKE_GPIO_Port, LED_BRAKE_Pin, (GPIO_PinState)!HAL_GPIO_ReadPin(BTN_BRAKE_GPIO_Port, BTN_BRAKE_Pin));
  /* Seed the runtime sysconfig store from the config.h defaults before any task runs. */
  sysconfig_init();
  /* USER CODE END 2 */

  /* Init scheduler */
  osKernelInitialize();
  /* Create the mutex(es) */
  /* creation of usart1Mutex */
  usart1MutexHandle = osMutexNew(&usart1Mutex_attributes);

  /* USER CODE BEGIN RTOS_MUTEX */
  /* add mutexes, ... */
  /* USER CODE END RTOS_MUTEX */

  /* USER CODE BEGIN RTOS_SEMAPHORES */
  /* add semaphores, ... */
  /* USER CODE END RTOS_SEMAPHORES */

  /* USER CODE BEGIN RTOS_TIMERS */
  /* start timers, add new ones, ... */
  /* USER CODE END RTOS_TIMERS */

  /* Create the queue(s) */
  /* creation of sessionControllerToLumexLcd */
  sessionControllerToLumexLcdHandle = osMessageQueueNew (25, sizeof(session_controller_to_lumex_lcd), &sessionControllerToLumexLcd_attributes);

  /* creation of sessionControllerToBpm */
   sessionControllerToBpmHandle = osMessageQueueNew (10, sizeof(session_controller_to_bpm), & sessionControllerToBpm_attributes);

  /* creation of sessionControllerToForceSensor */
   sessionControllerToForceSensorHandle = osMessageQueueNew (16, sizeof(bool), & sessionControllerToForceSensor_attributes);

  /* creation of sessionControllerToPidController */
   sessionControllerToPidControllerHandle = osMessageQueueNew (5, sizeof(session_controller_to_pid_controller), & sessionControllerToPidController_attributes);

  /* creation of opticalEncoderToPidController */
   opticalEncoderToPidControllerHandle = osMessageQueueNew (10, sizeof(optical_encoder_output_data), & opticalEncoderToPidController_attributes);

  /* creation of pidControllerToBpm */
   pidControllerToBpmHandle = osMessageQueueNew (10, sizeof(float), & pidControllerToBpm_attributes);

  /* creation of sessionControllerToOpticalSensor */
   sessionControllerToOpticalSensorHandle = osMessageQueueNew (16, sizeof(uint16_t), & sessionControllerToOpticalSensor_attributes);

  /* creation of sessionControllertoUsbController */
  sessionControllertoUsbControllerHandle = osMessageQueueNew (16, sizeof(uint16_t), &sessionControllertoUsbController_attributes);

  /* creation of taskMonitorToUsbController */
  taskMonitorToUsbControllerHandle = osMessageQueueNew (50, sizeof(task_monitor_output_data), &taskMonitorToUsbController_attributes);

  /* creation of usbToForceSensorCommand */
  usbToForceSensorCommandHandle = osMessageQueueNew (8, sizeof(usb_task_command), &usbToForceSensorCommand_attributes);

  /* creation of taskToUsbController */
  taskToUsbControllerResponseHandle = osMessageQueueNew (8, sizeof(usb_task_completion), &taskToUsbControllerResponse_attributes);

  /* creation of pidControllerToSessionControllerAck */
  pidControllerToSessionControllerAckHandle = osMessageQueueNew (5, sizeof(bool), &pidControllerToSessionControllerAck_attributes);

  /* USER CODE BEGIN RTOS_QUEUES */
//
  /* USER CODE END RTOS_QUEUES */

  /* Create the thread(s) */
  /* creation of usbTask */
  usbTaskHandle = osThreadNew(usbTaskEntryFunction, NULL, &usbTask_attributes);

  /* creation of bpmTask */
   bpmTaskHandle = osThreadNew(bpmTaskEntryFunction, NULL, & bpmTask_attributes);

  /* creation of forceSensorTask */
   forceSensorTaskHandle = osThreadNew(forceSensorTaskEntryFunction, NULL, & forceSensorTask_attributes);

  /* creation of pidTask */
   pidTaskHandle = osThreadNew(pidControllerTaskEntryFunction, NULL, & pidTask_attributes);

  /* creation of opticalSensorTask */
   opticalSensorTaskHandle = osThreadNew(opticalSensorTaskEntryFunction, NULL, & opticalSensorTask_attributes);

  /* creation of sessionControllerTask */
  sessionControllerTaskHandle = osThreadNew(sessionControllerTaskEntryFunction, NULL, &sessionControllerTask_attributes);

  /* creation of lcdDisplayTask */
  lcdDisplayTaskHandle = osThreadNew(lcdDisplayTaskEntryFunction, NULL, &lcdDisplayTask_attributes);

  /* creation of ledBlinkTask */
  ledBlinkTaskHandle = osThreadNew(ledBlinkTaskEntryFunction, NULL, &ledBlinkTask_attributes);

  /* creation of taskMonitorTask */
  taskMonitorTaskHandle = osThreadNew(taskMonitorEntryFunction, NULL, &taskMonitorTask_attributes);

  /* USER CODE BEGIN RTOS_THREADS */
//  /* add threads, ... */
  /* USER CODE END RTOS_THREADS */

  /* USER CODE BEGIN RTOS_EVENTS */
//  /* add events, ... */
  /* USER CODE END RTOS_EVENTS */

  /* Start scheduler */
  osKernelStart();

  /* We should never get here as control is now taken by the scheduler */

  /* Infinite loop */
  /* USER CODE BEGIN WHILE */
  while (1)
  {
    /* USER CODE END WHILE */

    /* USER CODE BEGIN 3 */
  }
  /* USER CODE END 3 */
}

/**
  * @brief System Clock Configuration
  * @retval None
  */
void SystemClock_Config(void)
{
  RCC_OscInitTypeDef RCC_OscInitStruct = {0};
  RCC_ClkInitTypeDef RCC_ClkInitStruct = {0};

  /** Supply configuration update enable
  */
  HAL_PWREx_ConfigSupply(PWR_LDO_SUPPLY);

  /** Configure the main internal regulator output voltage
  */
  __HAL_PWR_VOLTAGESCALING_CONFIG(PWR_REGULATOR_VOLTAGE_SCALE1);

  while(!__HAL_PWR_GET_FLAG(PWR_FLAG_VOSRDY)) {}

  /** Initializes the RCC Oscillators according to the specified parameters
  * in the RCC_OscInitTypeDef structure.
  */
  RCC_OscInitStruct.OscillatorType = RCC_OSCILLATORTYPE_HSE;
  RCC_OscInitStruct.HSEState = RCC_HSE_ON;
  RCC_OscInitStruct.PLL.PLLState = RCC_PLL_ON;
  RCC_OscInitStruct.PLL.PLLSource = RCC_PLLSOURCE_HSE;
  RCC_OscInitStruct.PLL.PLLM = 2;
  RCC_OscInitStruct.PLL.PLLN = 64;
  RCC_OscInitStruct.PLL.PLLP = 2;
  RCC_OscInitStruct.PLL.PLLQ = 4;
  RCC_OscInitStruct.PLL.PLLR = 2;
  RCC_OscInitStruct.PLL.PLLRGE = RCC_PLL1VCIRANGE_3;
  RCC_OscInitStruct.PLL.PLLVCOSEL = RCC_PLL1VCOWIDE;
  RCC_OscInitStruct.PLL.PLLFRACN = 0;
  if (HAL_RCC_OscConfig(&RCC_OscInitStruct) != HAL_OK)
  {
    Error_Handler();
  }

  /** Initializes the CPU, AHB and APB buses clocks
  */
  RCC_ClkInitStruct.ClockType = RCC_CLOCKTYPE_HCLK|RCC_CLOCKTYPE_SYSCLK
                              |RCC_CLOCKTYPE_PCLK1|RCC_CLOCKTYPE_PCLK2
                              |RCC_CLOCKTYPE_D3PCLK1|RCC_CLOCKTYPE_D1PCLK1;
  RCC_ClkInitStruct.SYSCLKSource = RCC_SYSCLKSOURCE_PLLCLK;
  RCC_ClkInitStruct.SYSCLKDivider = RCC_SYSCLK_DIV1;
  RCC_ClkInitStruct.AHBCLKDivider = RCC_HCLK_DIV2;
  RCC_ClkInitStruct.APB3CLKDivider = RCC_APB3_DIV2;
  RCC_ClkInitStruct.APB1CLKDivider = RCC_APB1_DIV2;
  RCC_ClkInitStruct.APB2CLKDivider = RCC_APB2_DIV2;
  RCC_ClkInitStruct.APB4CLKDivider = RCC_APB4_DIV2;

  if (HAL_RCC_ClockConfig(&RCC_ClkInitStruct, FLASH_LATENCY_2) != HAL_OK)
  {
    Error_Handler();
  }
}

/**
  * @brief Peripherals Common Clock Configuration
  * @retval None
  */
void PeriphCommonClock_Config(void)
{
  RCC_PeriphCLKInitTypeDef PeriphClkInitStruct = {0};

  /** Initializes the peripherals clock
  */
  PeriphClkInitStruct.PeriphClockSelection = RCC_PERIPHCLK_USB|RCC_PERIPHCLK_I2C4
                              |RCC_PERIPHCLK_ADC;
  PeriphClkInitStruct.PLL3.PLL3M = 2;
  PeriphClkInitStruct.PLL3.PLL3N = 32;
  PeriphClkInitStruct.PLL3.PLL3P = 2;
  PeriphClkInitStruct.PLL3.PLL3Q = 8;
  PeriphClkInitStruct.PLL3.PLL3R = 8;
  PeriphClkInitStruct.PLL3.PLL3RGE = RCC_PLL3VCIRANGE_3;
  PeriphClkInitStruct.PLL3.PLL3VCOSEL = RCC_PLL3VCOWIDE;
  PeriphClkInitStruct.PLL3.PLL3FRACN = 0;
  PeriphClkInitStruct.UsbClockSelection = RCC_USBCLKSOURCE_PLL3;
  PeriphClkInitStruct.I2c4ClockSelection = RCC_I2C4CLKSOURCE_PLL3;
  PeriphClkInitStruct.AdcClockSelection = RCC_ADCCLKSOURCE_PLL3;
  if (HAL_RCCEx_PeriphCLKConfig(&PeriphClkInitStruct) != HAL_OK)
  {
    Error_Handler();
  }
}

/**
  * @brief ADC2 Initialization Function
  * @param None
  * @retval None
  */
static void MX_ADC2_Init(void)
{

  /* USER CODE BEGIN ADC2_Init 0 */
  #if STM32_PERIPHERAL_ADC2_ENABLE == 0
    return;
  #endif

  /* USER CODE END ADC2_Init 0 */

  ADC_ChannelConfTypeDef sConfig = {0};

  /* USER CODE BEGIN ADC2_Init 1 */

  /* USER CODE END ADC2_Init 1 */

  /** Common config
  */
  hadc2.Instance = ADC2;
  hadc2.Init.ClockPrescaler = ADC_CLOCK_ASYNC_DIV2;
  hadc2.Init.Resolution = ADC_RESOLUTION_16B;
  hadc2.Init.ScanConvMode = ADC_SCAN_DISABLE;
  hadc2.Init.EOCSelection = ADC_EOC_SINGLE_CONV;
  hadc2.Init.LowPowerAutoWait = DISABLE;
  hadc2.Init.ContinuousConvMode = DISABLE;
  hadc2.Init.NbrOfConversion = 1;
  hadc2.Init.DiscontinuousConvMode = DISABLE;
  hadc2.Init.ExternalTrigConv = ADC_SOFTWARE_START;
  hadc2.Init.ExternalTrigConvEdge = ADC_EXTERNALTRIGCONVEDGE_NONE;
  hadc2.Init.ConversionDataManagement = ADC_CONVERSIONDATA_DR;
  hadc2.Init.Overrun = ADC_OVR_DATA_PRESERVED;
  hadc2.Init.LeftBitShift = ADC_LEFTBITSHIFT_NONE;
  hadc2.Init.OversamplingMode = DISABLE;
  hadc2.Init.Oversampling.Ratio = 1;
  if (HAL_ADC_Init(&hadc2) != HAL_OK)
  {
    Error_Handler();
  }

  /** Configure Regular Channel
  */
  sConfig.Channel = ADC_CHANNEL_5;
  sConfig.Rank = ADC_REGULAR_RANK_1;
  sConfig.SamplingTime = ADC_SAMPLETIME_1CYCLE_5;
  sConfig.SingleDiff = ADC_SINGLE_ENDED;
  sConfig.OffsetNumber = ADC_OFFSET_NONE;
  sConfig.Offset = 0;
  sConfig.OffsetSignedSaturation = DISABLE;
  if (HAL_ADC_ConfigChannel(&hadc2, &sConfig) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN ADC2_Init 2 */

  /* USER CODE END ADC2_Init 2 */

}

/**
  * @brief ADC3 Initialization Function
  * @param None
  * @retval None
  */
static void MX_ADC3_Init(void)
{

  /* USER CODE BEGIN ADC3_Init 0 */
  #if STM32_PERIPHERAL_ADC3_ENABLE == 0
    return;
  #endif
  /* USER CODE END ADC3_Init 0 */

  ADC_ChannelConfTypeDef sConfig = {0};

  /* USER CODE BEGIN ADC3_Init 1 */

  /* USER CODE END ADC3_Init 1 */

  /** Common config
  */
  hadc3.Instance = ADC3;
  hadc3.Init.ClockPrescaler = ADC_CLOCK_ASYNC_DIV2;
  hadc3.Init.Resolution = ADC_RESOLUTION_16B;
  hadc3.Init.ScanConvMode = ADC_SCAN_DISABLE;
  hadc3.Init.EOCSelection = ADC_EOC_SINGLE_CONV;
  hadc3.Init.LowPowerAutoWait = DISABLE;
  hadc3.Init.ContinuousConvMode = DISABLE;
  hadc3.Init.NbrOfConversion = 1;
  hadc3.Init.DiscontinuousConvMode = DISABLE;
  hadc3.Init.ExternalTrigConv = ADC_SOFTWARE_START;
  hadc3.Init.ExternalTrigConvEdge = ADC_EXTERNALTRIGCONVEDGE_NONE;
  hadc3.Init.ConversionDataManagement = ADC_CONVERSIONDATA_DR;
  hadc3.Init.Overrun = ADC_OVR_DATA_PRESERVED;
  hadc3.Init.LeftBitShift = ADC_LEFTBITSHIFT_NONE;
  hadc3.Init.OversamplingMode = DISABLE;
  hadc3.Init.Oversampling.Ratio = 1;
  if (HAL_ADC_Init(&hadc3) != HAL_OK)
  {
    Error_Handler();
  }

  /** Configure Regular Channel
  */
  sConfig.Channel = ADC_CHANNEL_3;
  sConfig.Rank = ADC_REGULAR_RANK_1;
  sConfig.SamplingTime = ADC_SAMPLETIME_1CYCLE_5;
  sConfig.SingleDiff = ADC_SINGLE_ENDED;
  sConfig.OffsetNumber = ADC_OFFSET_NONE;
  sConfig.Offset = 0;
  sConfig.OffsetSignedSaturation = DISABLE;
  if (HAL_ADC_ConfigChannel(&hadc3, &sConfig) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN ADC3_Init 2 */

  /* USER CODE END ADC3_Init 2 */

}

/**
  * @brief I2C4 Initialization Function
  * @param None
  * @retval None
  */
static void MX_I2C4_Init(void)
{

  /* USER CODE BEGIN I2C4_Init 0 */
  #if STM32_PERIPHERAL_I2C4_ENABLE == 0
    return;
  #endif
  /* USER CODE END I2C4_Init 0 */

  /* USER CODE BEGIN I2C4_Init 1 */

  /* USER CODE END I2C4_Init 1 */
  hi2c4.Instance = I2C4;
  hi2c4.Init.Timing = 0x00C0EAFF;
  hi2c4.Init.OwnAddress1 = 0;
  hi2c4.Init.AddressingMode = I2C_ADDRESSINGMODE_7BIT;
  hi2c4.Init.DualAddressMode = I2C_DUALADDRESS_DISABLE;
  hi2c4.Init.OwnAddress2 = 0;
  hi2c4.Init.OwnAddress2Masks = I2C_OA2_NOMASK;
  hi2c4.Init.GeneralCallMode = I2C_GENERALCALL_DISABLE;
  hi2c4.Init.NoStretchMode = I2C_NOSTRETCH_DISABLE;
  if (HAL_I2C_Init(&hi2c4) != HAL_OK)
  {
    Error_Handler();
  }

  /** Configure Analogue filter
  */
  if (HAL_I2CEx_ConfigAnalogFilter(&hi2c4, I2C_ANALOGFILTER_ENABLE) != HAL_OK)
  {
    Error_Handler();
  }

  /** Configure Digital filter
  */
  if (HAL_I2CEx_ConfigDigitalFilter(&hi2c4, 0) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN I2C4_Init 2 */

  /* USER CODE END I2C4_Init 2 */

}

/**
  * @brief SDMMC1 Initialization Function
  * @param None
  * @retval None
  */
static void MX_SDMMC1_SD_Init(void)
{

  /* USER CODE BEGIN SDMMC1_Init 0 */
  #if STM32_PERIPHERAL_SDMMC1_ENABLE == 0
    return;
  #endif
  /* USER CODE END SDMMC1_Init 0 */

  /* USER CODE BEGIN SDMMC1_Init 1 */

  /* USER CODE END SDMMC1_Init 1 */
  hsd1.Instance = SDMMC1;
  hsd1.Init.ClockEdge = SDMMC_CLOCK_EDGE_RISING;
  hsd1.Init.ClockPowerSave = SDMMC_CLOCK_POWER_SAVE_DISABLE;
  hsd1.Init.BusWide = SDMMC_BUS_WIDE_4B;
  hsd1.Init.HardwareFlowControl = SDMMC_HARDWARE_FLOW_CONTROL_DISABLE;
  hsd1.Init.ClockDiv = 0;
  if (HAL_SD_Init(&hsd1) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN SDMMC1_Init 2 */

  /* USER CODE END SDMMC1_Init 2 */

}

/**
  * @brief SPI1 Initialization Function
  * @param None
  * @retval None
  */
static void MX_SPI1_Init(void)
{

  /* USER CODE BEGIN SPI1_Init 0 */
  #if STM32_PERIPHERAL_SPI1_ENABLE == 0
    return;
  #endif
  /* USER CODE END SPI1_Init 0 */

  /* USER CODE BEGIN SPI1_Init 1 */

  /* USER CODE END SPI1_Init 1 */
  /* SPI1 parameter configuration*/
  hspi1.Instance = SPI1;
  hspi1.Init.Mode = SPI_MODE_MASTER;
  hspi1.Init.Direction = SPI_DIRECTION_2LINES;
  hspi1.Init.DataSize = SPI_DATASIZE_4BIT;
  hspi1.Init.CLKPolarity = SPI_POLARITY_LOW;
  hspi1.Init.CLKPhase = SPI_PHASE_1EDGE;
  hspi1.Init.NSS = SPI_NSS_SOFT;
  hspi1.Init.BaudRatePrescaler = SPI_BAUDRATEPRESCALER_2;
  hspi1.Init.FirstBit = SPI_FIRSTBIT_MSB;
  hspi1.Init.TIMode = SPI_TIMODE_DISABLE;
  hspi1.Init.CRCCalculation = SPI_CRCCALCULATION_DISABLE;
  hspi1.Init.CRCPolynomial = 0x0;
  hspi1.Init.NSSPMode = SPI_NSS_PULSE_ENABLE;
  hspi1.Init.NSSPolarity = SPI_NSS_POLARITY_LOW;
  hspi1.Init.FifoThreshold = SPI_FIFO_THRESHOLD_01DATA;
  hspi1.Init.TxCRCInitializationPattern = SPI_CRC_INITIALIZATION_ALL_ZERO_PATTERN;
  hspi1.Init.RxCRCInitializationPattern = SPI_CRC_INITIALIZATION_ALL_ZERO_PATTERN;
  hspi1.Init.MasterSSIdleness = SPI_MASTER_SS_IDLENESS_00CYCLE;
  hspi1.Init.MasterInterDataIdleness = SPI_MASTER_INTERDATA_IDLENESS_00CYCLE;
  hspi1.Init.MasterReceiverAutoSusp = SPI_MASTER_RX_AUTOSUSP_DISABLE;
  hspi1.Init.MasterKeepIOState = SPI_MASTER_KEEP_IO_STATE_DISABLE;
  hspi1.Init.IOSwap = SPI_IO_SWAP_DISABLE;
  if (HAL_SPI_Init(&hspi1) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN SPI1_Init 2 */

  /* USER CODE END SPI1_Init 2 */

}

/**
  * @brief SPI2 Initialization Function
  * @param None
  * @retval None
  */
static void MX_SPI2_Init(void)
{

  /* USER CODE BEGIN SPI2_Init 0 */
  #if STM32_PERIPHERAL_SPI2_ENABLE == 0
    return;
  #endif
  /* USER CODE END SPI2_Init 0 */

  /* USER CODE BEGIN SPI2_Init 1 */

  /* USER CODE END SPI2_Init 1 */
  /* SPI2 parameter configuration*/
  hspi2.Instance = SPI2;
  hspi2.Init.Mode = SPI_MODE_MASTER;
  hspi2.Init.Direction = SPI_DIRECTION_2LINES;
  hspi2.Init.DataSize = SPI_DATASIZE_4BIT;
  hspi2.Init.CLKPolarity = SPI_POLARITY_LOW;
  hspi2.Init.CLKPhase = SPI_PHASE_1EDGE;
  hspi2.Init.NSS = SPI_NSS_SOFT;
  hspi2.Init.BaudRatePrescaler = SPI_BAUDRATEPRESCALER_2;
  hspi2.Init.FirstBit = SPI_FIRSTBIT_MSB;
  hspi2.Init.TIMode = SPI_TIMODE_DISABLE;
  hspi2.Init.CRCCalculation = SPI_CRCCALCULATION_DISABLE;
  hspi2.Init.CRCPolynomial = 0x0;
  hspi2.Init.NSSPMode = SPI_NSS_PULSE_ENABLE;
  hspi2.Init.NSSPolarity = SPI_NSS_POLARITY_LOW;
  hspi2.Init.FifoThreshold = SPI_FIFO_THRESHOLD_01DATA;
  hspi2.Init.TxCRCInitializationPattern = SPI_CRC_INITIALIZATION_ALL_ZERO_PATTERN;
  hspi2.Init.RxCRCInitializationPattern = SPI_CRC_INITIALIZATION_ALL_ZERO_PATTERN;
  hspi2.Init.MasterSSIdleness = SPI_MASTER_SS_IDLENESS_00CYCLE;
  hspi2.Init.MasterInterDataIdleness = SPI_MASTER_INTERDATA_IDLENESS_00CYCLE;
  hspi2.Init.MasterReceiverAutoSusp = SPI_MASTER_RX_AUTOSUSP_DISABLE;
  hspi2.Init.MasterKeepIOState = SPI_MASTER_KEEP_IO_STATE_DISABLE;
  hspi2.Init.IOSwap = SPI_IO_SWAP_DISABLE;
  if (HAL_SPI_Init(&hspi2) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN SPI2_Init 2 */

  /* USER CODE END SPI2_Init 2 */

}

/**
  * @brief TIM1 Initialization Function
  * @param None
  * @retval None
  */
static void MX_TIM1_Init(void)
{

  /* USER CODE BEGIN TIM1_Init 0 */
  #if STM32_PERIPHERAL_TIM1_ENABLE == 0
    return;
  #endif
  /* USER CODE END TIM1_Init 0 */

  TIM_ClockConfigTypeDef sClockSourceConfig = {0};
  TIM_MasterConfigTypeDef sMasterConfig = {0};

  /* USER CODE BEGIN TIM1_Init 1 */

  /* USER CODE END TIM1_Init 1 */
  htim1.Instance = TIM1;
  htim1.Init.Prescaler = 0;
  htim1.Init.CounterMode = TIM_COUNTERMODE_UP;
  htim1.Init.Period = 65535;
  htim1.Init.ClockDivision = TIM_CLOCKDIVISION_DIV1;
  htim1.Init.RepetitionCounter = 0;
  htim1.Init.AutoReloadPreload = TIM_AUTORELOAD_PRELOAD_DISABLE;
  if (HAL_TIM_Base_Init(&htim1) != HAL_OK)
  {
    Error_Handler();
  }
  sClockSourceConfig.ClockSource = TIM_CLOCKSOURCE_INTERNAL;
  if (HAL_TIM_ConfigClockSource(&htim1, &sClockSourceConfig) != HAL_OK)
  {
    Error_Handler();
  }
  sMasterConfig.MasterOutputTrigger = TIM_TRGO_RESET;
  sMasterConfig.MasterOutputTrigger2 = TIM_TRGO2_RESET;
  sMasterConfig.MasterSlaveMode = TIM_MASTERSLAVEMODE_DISABLE;
  if (HAL_TIMEx_MasterConfigSynchronization(&htim1, &sMasterConfig) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN TIM1_Init 2 */

  /* USER CODE END TIM1_Init 2 */

}

/**
  * @brief TIM2 Initialization Function
  * @param None
  * @retval None
  */
static void MX_TIM2_Init(void)
{

  /* USER CODE BEGIN TIM2_Init 0 */
  #if STM32_PERIPHERAL_TIM2_ENABLE == 0
    return;
  #endif
  /* USER CODE END TIM2_Init 0 */

  TIM_ClockConfigTypeDef sClockSourceConfig = {0};
  TIM_MasterConfigTypeDef sMasterConfig = {0};

  /* USER CODE BEGIN TIM2_Init 1 */

  /* USER CODE END TIM2_Init 1 */
  htim2.Instance = TIM2;
  htim2.Init.Prescaler = 200-1;
  htim2.Init.CounterMode = TIM_COUNTERMODE_UP;
  htim2.Init.Period = 4294967295;
  htim2.Init.ClockDivision = TIM_CLOCKDIVISION_DIV1;
  htim2.Init.AutoReloadPreload = TIM_AUTORELOAD_PRELOAD_ENABLE;
  if (HAL_TIM_Base_Init(&htim2) != HAL_OK)
  {
    Error_Handler();
  }
  sClockSourceConfig.ClockSource = TIM_CLOCKSOURCE_INTERNAL;
  if (HAL_TIM_ConfigClockSource(&htim2, &sClockSourceConfig) != HAL_OK)
  {
    Error_Handler();
  }
  sMasterConfig.MasterOutputTrigger = TIM_TRGO_RESET;
  sMasterConfig.MasterSlaveMode = TIM_MASTERSLAVEMODE_DISABLE;
  if (HAL_TIMEx_MasterConfigSynchronization(&htim2, &sMasterConfig) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN TIM2_Init 2 */

  /* USER CODE END TIM2_Init 2 */

}

/**
  * @brief TIM13 Initialization Function
  * @param None
  * @retval None
  */
static void MX_TIM13_Init(void)
{

  /* USER CODE BEGIN TIM13_Init 0 */
  #if STM32_PERIPHERAL_TIM13_ENABLE == 0
    return;
  #endif
  /* USER CODE END TIM13_Init 0 */

  /* USER CODE BEGIN TIM13_Init 1 */

  /* USER CODE END TIM13_Init 1 */
  htim13.Instance = TIM13;
  htim13.Init.Prescaler = 400-1;
  htim13.Init.CounterMode = TIM_COUNTERMODE_UP;
  htim13.Init.Period = 40-1;
  htim13.Init.ClockDivision = TIM_CLOCKDIVISION_DIV1;
  htim13.Init.AutoReloadPreload = TIM_AUTORELOAD_PRELOAD_DISABLE;
  if (HAL_TIM_Base_Init(&htim13) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN TIM13_Init 2 */

  /* USER CODE END TIM13_Init 2 */

}

/**
  * @brief TIM16 Initialization Function
  * @param None
  * @retval None
  */
static void MX_TIM16_Init(void)
{

  /* USER CODE BEGIN TIM16_Init 0 */
  #if STM32_PERIPHERAL_TIM16_ENABLE == 0
    return;
  #endif
  /* USER CODE END TIM16_Init 0 */

  TIM_OC_InitTypeDef sConfigOC = {0};
  TIM_BreakDeadTimeConfigTypeDef sBreakDeadTimeConfig = {0};

  /* USER CODE BEGIN TIM16_Init 1 */

  /* USER CODE END TIM16_Init 1 */
  htim16.Instance = TIM16;
  htim16.Init.Prescaler = 200-1;
  htim16.Init.CounterMode = TIM_COUNTERMODE_UP;
  htim16.Init.Period = 200-1;
  htim16.Init.ClockDivision = TIM_CLOCKDIVISION_DIV1;
  htim16.Init.RepetitionCounter = 0;
  htim16.Init.AutoReloadPreload = TIM_AUTORELOAD_PRELOAD_ENABLE;
  if (HAL_TIM_Base_Init(&htim16) != HAL_OK)
  {
    Error_Handler();
  }
  if (HAL_TIM_PWM_Init(&htim16) != HAL_OK)
  {
    Error_Handler();
  }
  sConfigOC.OCMode = TIM_OCMODE_PWM1;
  sConfigOC.Pulse = 0;
  sConfigOC.OCPolarity = TIM_OCPOLARITY_HIGH;
  sConfigOC.OCNPolarity = TIM_OCNPOLARITY_HIGH;
  sConfigOC.OCFastMode = TIM_OCFAST_DISABLE;
  sConfigOC.OCIdleState = TIM_OCIDLESTATE_RESET;
  sConfigOC.OCNIdleState = TIM_OCNIDLESTATE_RESET;
  if (HAL_TIM_PWM_ConfigChannel(&htim16, &sConfigOC, TIM_CHANNEL_1) != HAL_OK)
  {
    Error_Handler();
  }
  sBreakDeadTimeConfig.OffStateRunMode = TIM_OSSR_DISABLE;
  sBreakDeadTimeConfig.OffStateIDLEMode = TIM_OSSI_DISABLE;
  sBreakDeadTimeConfig.LockLevel = TIM_LOCKLEVEL_OFF;
  sBreakDeadTimeConfig.DeadTime = 0;
  sBreakDeadTimeConfig.BreakState = TIM_BREAK_DISABLE;
  sBreakDeadTimeConfig.BreakPolarity = TIM_BREAKPOLARITY_HIGH;
  sBreakDeadTimeConfig.BreakFilter = 0;
  sBreakDeadTimeConfig.AutomaticOutput = TIM_AUTOMATICOUTPUT_DISABLE;
  if (HAL_TIMEx_ConfigBreakDeadTime(&htim16, &sBreakDeadTimeConfig) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN TIM16_Init 2 */

  /* USER CODE END TIM16_Init 2 */
  HAL_TIM_MspPostInit(&htim16);

}

/**
  * @brief USART1 Initialization Function
  * @param None
  * @retval None
  */
static void MX_USART1_UART_Init(void)
{

  /* USER CODE BEGIN USART1_Init 0 */
  #if STM32_PERIPHERAL_USART1_ENABLE == 0
    return;
  #endif
  /* USER CODE END USART1_Init 0 */

  /* USER CODE BEGIN USART1_Init 1 */

  /* USER CODE END USART1_Init 1 */
  huart1.Instance = USART1;
  huart1.Init.BaudRate = 115200;
  huart1.Init.WordLength = UART_WORDLENGTH_8B;
  huart1.Init.StopBits = UART_STOPBITS_1;
  huart1.Init.Parity = UART_PARITY_NONE;
  huart1.Init.Mode = UART_MODE_TX_RX;
  huart1.Init.HwFlowCtl = UART_HWCONTROL_NONE;
  huart1.Init.OverSampling = UART_OVERSAMPLING_16;
  huart1.Init.OneBitSampling = UART_ONE_BIT_SAMPLE_DISABLE;
  huart1.Init.ClockPrescaler = UART_PRESCALER_DIV1;
  huart1.AdvancedInit.AdvFeatureInit = UART_ADVFEATURE_NO_INIT;
  if (HAL_UART_Init(&huart1) != HAL_OK)
  {
    Error_Handler();
  }
  if (HAL_UARTEx_SetTxFifoThreshold(&huart1, UART_TXFIFO_THRESHOLD_1_8) != HAL_OK)
  {
    Error_Handler();
  }
  if (HAL_UARTEx_SetRxFifoThreshold(&huart1, UART_RXFIFO_THRESHOLD_1_8) != HAL_OK)
  {
    Error_Handler();
  }
  if (HAL_UARTEx_DisableFifoMode(&huart1) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN USART1_Init 2 */

  /* USER CODE END USART1_Init 2 */

}

/**
  * @brief GPIO Initialization Function
  * @param None
  * @retval None
  */
static void MX_GPIO_Init(void)
{
  GPIO_InitTypeDef GPIO_InitStruct = {0};
  /* USER CODE BEGIN MX_GPIO_Init_1 */
  #if STM32_PERIPHERAL_GPIO_ENABLE == 0
    return;
  #endif
  /* USER CODE END MX_GPIO_Init_1 */

  /* GPIO Ports Clock Enable */
  __HAL_RCC_GPIOE_CLK_ENABLE();
  __HAL_RCC_GPIOI_CLK_ENABLE();
  __HAL_RCC_GPIOC_CLK_ENABLE();
  __HAL_RCC_GPIOF_CLK_ENABLE();
  __HAL_RCC_GPIOH_CLK_ENABLE();
  __HAL_RCC_GPIOA_CLK_ENABLE();
  __HAL_RCC_GPIOB_CLK_ENABLE();
  __HAL_RCC_GPIOD_CLK_ENABLE();
  __HAL_RCC_GPIOG_CLK_ENABLE();

  /*Configure GPIO pin Output Level */
  HAL_GPIO_WritePin(GPIOA, LUMEX_LCD_D0_Pin|LUMEX_LCD_D1_Pin|LUMEX_LCD_D2_Pin|LUMEX_LCD_D3_Pin
                          |LUMEX_LCD_D4_Pin|LUMEX_LCD_D5_Pin|LUMEX_LCD_D6_Pin|LUMEX_LCD_D7_Pin, GPIO_PIN_RESET);

  /*Configure GPIO pin Output Level */
  HAL_GPIO_WritePin(GPIOC, LUMEX_LCD_EN_Pin|LUMEX_LCD_RS_Pin, GPIO_PIN_RESET);

  /*Configure GPIO pin Output Level */
  HAL_GPIO_WritePin(GPIOH, ILI_SPI2_TOUCH_CS_Pin|ILI_SPI2_SD_CS_Pin, GPIO_PIN_RESET);

  /*Configure GPIO pin Output Level */
  HAL_GPIO_WritePin(GPIOD, ILI_LCD_DC_Pin|ILI_LCD_RST_Pin, GPIO_PIN_RESET);

  /*Configure GPIO pin Output Level */
  HAL_GPIO_WritePin(ILI_SPI1_LCD_CS_GPIO_Port, ILI_SPI1_LCD_CS_Pin, GPIO_PIN_RESET);

  /*Configure GPIO pin Output Level */
  HAL_GPIO_WritePin(GPIOI, LED_BACK_Pin|LED_SELECT_Pin|LED_BRAKE_Pin, GPIO_PIN_SET);

  /*Configure GPIO pins : ROT_EN_A_Pin ROT_EN_SW_Pin */
  GPIO_InitStruct.Pin = ROT_EN_A_Pin|ROT_EN_SW_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_IT_FALLING;
  GPIO_InitStruct.Pull = GPIO_PULLUP;
  HAL_GPIO_Init(GPIOE, &GPIO_InitStruct);

  /*Configure GPIO pins : BTN_SELECT_Pin BTN_BACK_Pin */
  GPIO_InitStruct.Pin = BTN_SELECT_Pin|BTN_BACK_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_IT_RISING_FALLING;
  GPIO_InitStruct.Pull = GPIO_PULLUP;
  HAL_GPIO_Init(GPIOE, &GPIO_InitStruct);

  /*Configure GPIO pin : ROT_EN_B_Pin */
  GPIO_InitStruct.Pin = ROT_EN_B_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_INPUT;
  GPIO_InitStruct.Pull = GPIO_NOPULL;
  HAL_GPIO_Init(ROT_EN_B_GPIO_Port, &GPIO_InitStruct);

  /*Configure GPIO pin : ADS1115_ALERT_Pin */
  GPIO_InitStruct.Pin = ADS1115_ALERT_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_IT_FALLING;
  GPIO_InitStruct.Pull = GPIO_PULLUP;
  HAL_GPIO_Init(ADS1115_ALERT_GPIO_Port, &GPIO_InitStruct);

  /*Configure GPIO pin : OP_IN_Pin */
  GPIO_InitStruct.Pin = OP_IN_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_IT_RISING;
  GPIO_InitStruct.Pull = GPIO_NOPULL;
  HAL_GPIO_Init(OP_IN_GPIO_Port, &GPIO_InitStruct);

  /*Configure GPIO pins : LUMEX_LCD_D0_Pin LUMEX_LCD_D1_Pin LUMEX_LCD_D2_Pin LUMEX_LCD_D3_Pin
                           LUMEX_LCD_D4_Pin LUMEX_LCD_D5_Pin LUMEX_LCD_D6_Pin LUMEX_LCD_D7_Pin */
  GPIO_InitStruct.Pin = LUMEX_LCD_D0_Pin|LUMEX_LCD_D1_Pin|LUMEX_LCD_D2_Pin|LUMEX_LCD_D3_Pin
                          |LUMEX_LCD_D4_Pin|LUMEX_LCD_D5_Pin|LUMEX_LCD_D6_Pin|LUMEX_LCD_D7_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_OUTPUT_PP;
  GPIO_InitStruct.Pull = GPIO_PULLUP;
  GPIO_InitStruct.Speed = GPIO_SPEED_FREQ_LOW;
  HAL_GPIO_Init(GPIOA, &GPIO_InitStruct);

  /*Configure GPIO pins : LUMEX_LCD_EN_Pin LUMEX_LCD_RS_Pin */
  GPIO_InitStruct.Pin = LUMEX_LCD_EN_Pin|LUMEX_LCD_RS_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_OUTPUT_PP;
  GPIO_InitStruct.Pull = GPIO_PULLUP;
  GPIO_InitStruct.Speed = GPIO_SPEED_FREQ_LOW;
  HAL_GPIO_Init(GPIOC, &GPIO_InitStruct);

  /*Configure GPIO pin : ILI_SPI2_TOUCH_CS_Pin */
  GPIO_InitStruct.Pin = ILI_SPI2_TOUCH_CS_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_OUTPUT_PP;
  GPIO_InitStruct.Pull = GPIO_NOPULL;
  GPIO_InitStruct.Speed = GPIO_SPEED_FREQ_MEDIUM;
  HAL_GPIO_Init(ILI_SPI2_TOUCH_CS_GPIO_Port, &GPIO_InitStruct);

  /*Configure GPIO pin : ILI_SPI2_SD_CS_Pin */
  GPIO_InitStruct.Pin = ILI_SPI2_SD_CS_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_OUTPUT_PP;
  GPIO_InitStruct.Pull = GPIO_NOPULL;
  GPIO_InitStruct.Speed = GPIO_SPEED_FREQ_LOW;
  HAL_GPIO_Init(ILI_SPI2_SD_CS_GPIO_Port, &GPIO_InitStruct);

  /*Configure GPIO pins : ILI_LCD_DC_Pin ILI_LCD_RST_Pin */
  GPIO_InitStruct.Pin = ILI_LCD_DC_Pin|ILI_LCD_RST_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_OUTPUT_PP;
  GPIO_InitStruct.Pull = GPIO_NOPULL;
  GPIO_InitStruct.Speed = GPIO_SPEED_FREQ_LOW;
  HAL_GPIO_Init(GPIOD, &GPIO_InitStruct);

  /*Configure GPIO pin : ILI_SPI1_LCD_CS_Pin */
  GPIO_InitStruct.Pin = ILI_SPI1_LCD_CS_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_OUTPUT_PP;
  GPIO_InitStruct.Pull = GPIO_NOPULL;
  GPIO_InitStruct.Speed = GPIO_SPEED_FREQ_MEDIUM;
  HAL_GPIO_Init(ILI_SPI1_LCD_CS_GPIO_Port, &GPIO_InitStruct);

  /*Configure GPIO pin : ILI_TOUCH_IRQ_Pin */
  GPIO_InitStruct.Pin = ILI_TOUCH_IRQ_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_IT_FALLING;
  GPIO_InitStruct.Pull = GPIO_PULLUP;
  HAL_GPIO_Init(ILI_TOUCH_IRQ_GPIO_Port, &GPIO_InitStruct);

  /*Configure GPIO pins : LED_BACK_Pin LED_SELECT_Pin LED_BRAKE_Pin */
  GPIO_InitStruct.Pin = LED_BACK_Pin|LED_SELECT_Pin|LED_BRAKE_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_OUTPUT_PP;
  GPIO_InitStruct.Pull = GPIO_NOPULL;
  GPIO_InitStruct.Speed = GPIO_SPEED_FREQ_LOW;
  HAL_GPIO_Init(GPIOI, &GPIO_InitStruct);

  /*Configure GPIO pin : BTN_BRAKE_Pin */
  GPIO_InitStruct.Pin = BTN_BRAKE_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_IT_RISING_FALLING;
  GPIO_InitStruct.Pull = GPIO_PULLDOWN;
  HAL_GPIO_Init(BTN_BRAKE_GPIO_Port, &GPIO_InitStruct);

  /*AnalogSwitch Config */
  HAL_SYSCFG_AnalogSwitchConfig(SYSCFG_SWITCH_PA0, SYSCFG_SWITCH_PA0_CLOSE);

  /*AnalogSwitch Config */
  HAL_SYSCFG_AnalogSwitchConfig(SYSCFG_SWITCH_PA1, SYSCFG_SWITCH_PA1_CLOSE);

  /* EXTI interrupt init*/
  HAL_NVIC_SetPriority(ROT_EN_A_EXTI_IRQn, 5, 0);
  HAL_NVIC_EnableIRQ(ROT_EN_A_EXTI_IRQn);

  HAL_NVIC_SetPriority(ROT_EN_SW_EXTI_IRQn, 5, 0);
  HAL_NVIC_EnableIRQ(ROT_EN_SW_EXTI_IRQn);

  HAL_NVIC_SetPriority(BTN_SELECT_EXTI_IRQn, 5, 0);
  HAL_NVIC_EnableIRQ(BTN_SELECT_EXTI_IRQn);

  HAL_NVIC_SetPriority(ADS1115_ALERT_EXTI_IRQn, 5, 0);
  HAL_NVIC_EnableIRQ(ADS1115_ALERT_EXTI_IRQn);

  /* USER CODE BEGIN MX_GPIO_Init_2 */

  /* The optical encoder input (OP_IN, PF9) sits on EXTI line 9, so its interrupt is EXTI9_5 --
     the same one BTN_SELECT (PF5) needs. CubeMX therefore enables it above for the button, and
     the encoder only counts as a side effect of that: delete or move BTN_SELECT and the encoder
     stops counting silently, with the task still running and reporting a plausible zero.
     Claim the line explicitly so the encoder's own requirement is recorded and enforced.
     Enabling an already-enabled IRQ is idempotent, and the priority matches what is set above
     (5 == configLIBRARY_MAX_SYSCALL_INTERRUPT_PRIORITY, so taskENTER_CRITICAL masks it and the
     pulse count/timestamp pair can be read atomically). */
  HAL_NVIC_SetPriority(EXTI9_5_IRQn, 5, 0);
  HAL_NVIC_EnableIRQ(EXTI9_5_IRQn);

  /* USER CODE END MX_GPIO_Init_2 */
}

/* USER CODE BEGIN 4 */
void HAL_ADC_ConvCpltCallback(ADC_HandleTypeDef* hadc)
{
  #if FORCE_SENSOR_ADC_TASK_ENABLE == 1  
    if (hadc->Instance == ADC2)
    {
      forcesensor_adc_interrupt();
    }
  #endif
}

void HAL_GPIO_EXTI_Callback(uint16_t GPIO_Pin)
{
	switch(GPIO_Pin)
  {
    #if FORCE_SENSOR_ADS1115_TASK_ENABLE == 1
    case ADS1115_ALERT_Pin:
      forcesensor_ads1115_gpio_alert_interrupt();
      break;
    #endif
    case ROT_EN_A_Pin:
      register_rotary_encoder_input();
      break;
    // Should not ever be triggered, ROT_EN_B should be set up as a basic GPIO Input Pin
    case ROT_EN_B_Pin:
      break;
    case ROT_EN_SW_Pin:
      register_rotary_encoder_sw_input();
      break;
    case BTN_BACK_Pin:
      register_button_back_input();
      break;
    case BTN_SELECT_Pin:
      register_button_select_input();
      break;
    case BTN_BRAKE_Pin:
      register_button_brake_input();
      break;
    case ILI_TOUCH_IRQ_Pin:
      break;
    case OP_IN_Pin:
      opticalsensor_input_interrupt();
      break;
    default:
      break;
  }
}

void forceSensorTaskEntryFunction(void *argument)
{
  #if (!defined(FORCE_SENSOR_ADS1115_TASK_ENABLE) || !defined(FORCE_SENSOR_ADC_TASK_ENABLE))
    #error "FORCE_SENSOR_ADS1115_TASK_ENABLE or FORCE_SENSOR_ADC_TASK_ENABLE is not defined. Please define it as 0 or 1 in the configuration header."
  // Ensure both ADS1115 and ADC tasks can't be enabled at once. Has to be one or the other
  #elif (FORCE_SENSOR_ADS1115_TASK_ENABLE == 1) && (FORCE_SENSOR_ADC_TASK_ENABLE == 1)
    #error "Cannot enable both ADS1115 and ADC Force Sensor modules at the same time!"
  #elif (FORCE_SENSOR_ADS1115_TASK_ENABLE == 0) && (FORCE_SENSOR_ADC_TASK_ENABLE == 0)
     osThreadSuspend(osThreadGetId());
  #elif (FORCE_SENSOR_ADS1115_TASK_ENABLE == 1)
    forcesensor_ads1115_main(sessionControllerToForceSensorHandle, usbToForceSensorCommandHandle, taskToUsbControllerResponseHandle);
  #else
    forcesensor_adc_main(sessionControllerToForceSensorHandle);
  #endif
}

void bpmTaskEntryFunction(void *argument)
{
  #if (!defined(BPM_CONTROLLER_TASK_ENABLE))
  #error "BPM_CONTROLLER_TASK_ENABLE is not defined. Please define it as 0 or 1 in the configuration header."
  #elif BPM_CONTROLLER_TASK_ENABLE == 0
    osThreadSuspend(osThreadGetId());
  #else
    bpm_main(sessionControllerToBpmHandle, pidControllerToBpmHandle);
  #endif

}

void pidControllerTaskEntryFunction(void *argument)
{
  #if (!defined(PID_CONTROLLER_TASK_ENABLE))
  #error "PID_CONTROLLER_TASK_ENABLE is not defined. Please define it as 0 or 1 in the configuration header." 
  #elif PID_CONTROLLER_TASK_ENABLE == 0
    osThreadSuspend(osThreadGetId());
  #else
    pid_main(sessionControllerToPidControllerHandle, pidControllerToSessionControllerAckHandle, pidControllerToBpmHandle, PID_INITIAL_STATUS);
  #endif
}

void sessionControllerTaskEntryFunction(void* argument)
{
  #if (!defined(SESSION_CONTROLLER_TASK_ENABLE) || !defined(LUMEX_LCD_TASK_ENABLE))
  #error "SESSION_CONTROLLER_TASK_ENABLE or LUMEX_LCD_TASK_ENABLE is not defined. Please define it as 0 or 1 in the configuration header."
  #elif SESSION_CONTROLLER_TASK_ENABLE == 0
    osThreadSuspend(osThreadGetId());
  #elif LUMEX_LCD_TASK_ENABLE == 0
    #error "Lumex LCD is a hard dependency of the Session Controller task. Please enable LUMEX_LCD_TASK_ENABLE."
  #else
        session_controller_os_task_queues tasks = {
            .usb_controller = sessionControllertoUsbControllerHandle,
            .sd_controller = NULL,
            .force_sensor = sessionControllerToForceSensorHandle,
            .optical_sensor = sessionControllerToOpticalSensorHandle,
            .bpm_controller = sessionControllerToBpmHandle,
            .pid_controller = sessionControllerToPidControllerHandle,
            .pid_controller_ack = pidControllerToSessionControllerAckHandle,
            .lumex_lcd = sessionControllerToLumexLcdHandle
        };
        sessioncontroller_main(&tasks, usart1MutexHandle);
  #endif
}

void opticalSensorTaskEntryFunction(void *argument)
{
  #if (!defined(OPTICAL_ENCODER_TASK_ENABLE))
  #error "OPTICAL_ENCODER_TASK_ENABLE is not defined. Please define it as 0 or 1 in the configuration header."
  #elif OPTICAL_ENCODER_TASK_ENABLE == 0
     osThreadSuspend(osThreadGetId());
  #else
    opticalsensor_main(sessionControllerToOpticalSensorHandle);
  #endif

}

void lcdDisplayTaskEntryFunction(void *argument)
{
  #if (!defined(LUMEX_LCD_TASK_ENABLE))
  #error "LUMEX_LCD_TASK_ENABLE is not defined. Please define it as 0 or 1 in the configuration header."
  #elif LUMEX_LCD_TASK_ENABLE == 0
     osThreadSuspend(osThreadGetId());
  #else
    lumex_lcd_main(sessionControllerToLumexLcdHandle);
  #endif
}


void ledBlinkTaskEntryFunction(void *argument)
{
	#if (!defined(LED_BLINK_TASK_ENABLE))
  #error "LED_BLINK_TASK_ENABLE is not defined. Please define it as 0 or 1 in the configuration header."
	#elif LED_BLINK_TASK_ENABLE == 0
     osThreadSuspend(osThreadGetId());
  #else 
  for(;;)
  {
    // just to check if timestamp is working. Can only be checked using the debugger
    const uint32_t timestamp_for_testing = get_timestamp();
    (void)timestamp_for_testing;
    HAL_GPIO_TogglePin(ILI_SPI2_SD_CS_GPIO_Port, ILI_SPI2_SD_CS_Pin);
    osDelay(sysconfig_get_u32(SYSCFG_LED_TASK_OSDELAY));
  }
  #endif 

}

void taskMonitorEntryFunction(void *argument)
{
#if (!defined(TASK_MONITOR_TASK_ENABLE))
  #error "TASK_MONITOR_TASK_ENABLE is not defined. Please define it as 0 or 1 in the configuration header."
#elif TASK_MONITOR_TASK_ENABLE == 0
	 osThreadSuspend(osThreadGetId());
#else
  taskmonitor_osthreadids osthreadids =
  {
      .session_controller = sessionControllerTaskHandle,
      .optical_sensor = opticalSensorTaskHandle,
      .usb_controller = usbTaskHandle,
      .sd_controller = NULL,
      .force_sensor = forceSensorTaskHandle,
      .optical_sensor = opticalSensorTaskHandle,
      .bpm_controller = bpmTaskHandle,
      .pid_controller = pidTaskHandle,
      .pid_controller_ack = pidControllerToSessionControllerAckHandle,
      .lumex_lcd = lcdDisplayTaskHandle
  } ;
  taskmonitor_main(&osthreadids, taskMonitorToUsbControllerHandle);
#endif
}
/* USER CODE END 4 */

/* USER CODE BEGIN Header_usbTaskEntryFunction */
/**
  * @brief  Function implementing the usbTask thread.
  * @param  argument: Not used
  * @retval None
  */
/* USER CODE END Header_usbTaskEntryFunction */
__weak void usbTaskEntryFunction(void *argument)
{
  /* init code for USB_DEVICE */
  MX_USB_DEVICE_Init();
  /* USER CODE BEGIN 5 */
  #if (!defined(USB_CONTROLLER_TASK_ENABLE))
  #error "USB_CONTROLLER_TASK_ENABLE is not defined. Please define it as 0 or 1 in the configuration header."
  #elif USB_CONTROLLER_TASK_ENABLE == 0
     osThreadSuspend(osThreadGetId());
  #else
    usbcontroller_main(sessionControllertoUsbControllerHandle, taskMonitorToUsbControllerHandle, usbToForceSensorCommandHandle, taskToUsbControllerResponseHandle);
  #endif
  /* USER CODE END 5 */
}

 /* MPU Configuration */

void MPU_Config(void)
{
  MPU_Region_InitTypeDef MPU_InitStruct = {0};

  /* Disables the MPU */
  HAL_MPU_Disable();

  /** Initializes and configures the Region and the memory to be protected
  */
  MPU_InitStruct.Enable = MPU_REGION_ENABLE;
  MPU_InitStruct.Number = MPU_REGION_NUMBER0;
  MPU_InitStruct.BaseAddress = 0x0;
  MPU_InitStruct.Size = MPU_REGION_SIZE_4GB;
  MPU_InitStruct.SubRegionDisable = 0x87;
  MPU_InitStruct.TypeExtField = MPU_TEX_LEVEL0;
  MPU_InitStruct.AccessPermission = MPU_REGION_NO_ACCESS;
  MPU_InitStruct.DisableExec = MPU_INSTRUCTION_ACCESS_DISABLE;
  MPU_InitStruct.IsShareable = MPU_ACCESS_SHAREABLE;
  MPU_InitStruct.IsCacheable = MPU_ACCESS_NOT_CACHEABLE;
  MPU_InitStruct.IsBufferable = MPU_ACCESS_NOT_BUFFERABLE;

  HAL_MPU_ConfigRegion(&MPU_InitStruct);
  /* Enables the MPU */
  HAL_MPU_Enable(MPU_PRIVILEGED_DEFAULT);

}

/**
  * @brief  Period elapsed callback in non blocking mode
  * @note   This function is called  when TIM17 interrupt took place, inside
  * HAL_TIM_IRQHandler(). It makes a direct call to HAL_IncTick() to increment
  * a global variable "uwTick" used as application time base.
  * @param  htim : TIM handle
  * @retval None
  */
void HAL_TIM_PeriodElapsedCallback(TIM_HandleTypeDef *htim)
{
  /* USER CODE BEGIN Callback 0 */

  /* USER CODE END Callback 0 */
  if (htim->Instance == TIM17)
  {
    HAL_IncTick();
  }
  /* USER CODE BEGIN Callback 1 */
  else if (htim->Instance == lumexLcdTimInstance)
  {
	  lumex_lcd_timer_interrupt(htim);
  }

  /* USER CODE END Callback 1 */
}

/**
  * @brief  This function is executed in case of error occurrence.
  * @retval None
  */
void Error_Handler(void)
{
  /* USER CODE BEGIN Error_Handler_Debug */
  /* User can add his own implementation to report the HAL error return state */
  __disable_irq();
  while (1)
  {
  }
  /* USER CODE END Error_Handler_Debug */
}
#ifdef USE_FULL_ASSERT
/**
  * @brief  Reports the name of the source file and the source line number
  *         where the assert_param error has occurred.
  * @param  file: pointer to the source file name
  * @param  line: assert_param error line source number
  * @retval None
  */
void assert_failed(uint8_t *file, uint32_t line)
{
  /* USER CODE BEGIN 6 */
  /* User can add his own implementation to report the file name and line number,
     ex: printf("Wrong parameters value: file %s on line %d\r\n", file, line) */
  /* USER CODE END 6 */
}
#endif /* USE_FULL_ASSERT */
