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

// static inline, not plain inline, so this is callable from C and C++ alike at any optimisation
// level. A bare `inline` in C provides only an inline definition: the compiler need not emit an
// external one, so at -O0 a C caller links against a symbol nothing defines. It went unnoticed
// while every caller was C++ -- where inline has vague linkage and the definition is emitted --
// and surfaced the moment main.c called one of these in a Debug build.
static inline uint32_t get_timestamp(void)
{
    return __HAL_TIM_GET_COUNTER(timestampTimer);
}

// Called once from main(), before the scheduler starts: this counter is shared by every task that
// stamps a sample or times a wait, and no task owns it. Tasks should assume it is already running.
//
// Idempotent anyway, which HAL_TIM_Base_Start underneath it is not -- that returns HAL_ERROR
// whenever the handle is not in READY state, and a timer someone has already started is BUSY, so
// it answers "already running" and "failed to start" with the same value. Asking the hardware
// whether the counter is running answers the question callers are actually asking. A handle that
// was never initialised still fails honestly: CEN stays clear, and HAL_ERROR comes back.
static inline HAL_StatusTypeDef start_timestamp_timer(void)
{
	if ((timestampTimer->Instance->CR1 & TIM_CR1_CEN) == 0U)
	{
		(void)HAL_TIM_Base_Start(timestampTimer);
	}

	return ((timestampTimer->Instance->CR1 & TIM_CR1_CEN) != 0U) ? HAL_OK : HAL_ERROR;
}

uint32_t get_timestamp_scale(void);
uint32_t get_apb1_timer_clock(void);
uint32_t get_apb2_timer_clock(void);
uint32_t get_timer_clock(TIM_TypeDef* TIMx);

#ifdef __cplusplus
}
#endif

#endif // INC_TIMEKEEPING_TIMESTAMPS_H_
