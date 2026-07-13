#ifndef INC_TIMEKEEPING_TIMESTAMPS_H_
#define INC_TIMEKEEPING_TIMESTAMPS_H_

#include <stdint.h>

#include "main.h"

#include "stm32h7xx.h"

#ifdef __cplusplus
extern "C" {
#endif

extern TIM_HandleTypeDef* timestampTimer;

inline uint32_t get_timestamp()
{
    return __HAL_TIM_GET_COUNTER(timestampTimer);
}

inline HAL_StatusTypeDef start_timestamp_timer()
{
	return HAL_TIM_Base_Start(timestampTimer);
}

uint32_t get_timestamp_scale(void);
uint32_t get_apb1_timer_clock(void);
uint32_t get_apb2_timer_clock(void);
uint32_t get_timer_clock(TIM_TypeDef* TIMx);

#ifdef __cplusplus
}
#endif

#endif // INC_TIMEKEEPING_TIMESTAMPS_H_
