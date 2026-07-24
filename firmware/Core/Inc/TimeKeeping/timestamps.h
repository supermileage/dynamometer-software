#ifndef INC_TIMEKEEPING_TIMESTAMPS_H_
#define INC_TIMEKEEPING_TIMESTAMPS_H_

#include <stdint.h>

#include "main.h"

#include "stm32h7xx.h"

#ifdef __cplusplus
extern "C" {
#endif

extern TIM_HandleTypeDef* timestampTimer;

// The timestamp source is TIM2, free-running over its full 32-bit range.
//
//   HSE 25 MHz -> PLL (M=2, N=64, P=2) -> SYSCLK 400 MHz -> HCLK 200 MHz -> PCLK1 100 MHz,
//   doubled to a 200 MHz APB1 timer clock, divided by the TIM2 prescaler (200) = 1 MHz.
//
// So one tick is 1 us, and the counter wraps every 2^32 us ~= 71.6 minutes. Anything measuring
// an interval across that boundary must unwrap it; unsigned subtraction of two raw timestamps
// already does the right thing for a single wrap, which is why the deltas below are safe.
// Derive the rate from get_timestamp_scale() rather than assuming 1 MHz here -- it is computed
// from the live clock tree, so it survives a CubeMX clock or prescaler change that this comment
// would not.

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
