#include "TimeKeeping/timestamps.h"

uint32_t get_timestamp_scale()
{
    return get_timer_clock(timestampTimer->Instance) / (timestampTimer->Instance->PSC + 1);
}

uint32_t get_apb1_timer_clock(void) {
    uint32_t pclk1 = HAL_RCC_GetPCLK1Freq();
    uint32_t pre = (RCC->D2CFGR & RCC_D2CFGR_D2PPRE1) >> RCC_D2CFGR_D2PPRE1_Pos;
    if (pre >= 4) {  // prescaler 4..7 -> APB1 timer clock doubled
        pclk1 *= 2;
    }
    return pclk1;
}

uint32_t get_apb2_timer_clock(void) {
    uint32_t pclk2 = HAL_RCC_GetPCLK2Freq();
    uint32_t pre = (RCC->D2CFGR & RCC_D2CFGR_D2PPRE2) >> RCC_D2CFGR_D2PPRE2_Pos;
    if (pre >= 4) {  // prescaler 4..7 -> APB2 timer clock doubled
        pclk2 *= 2;
    }
    return pclk2;
}


// Specific timer clock
const uint32_t get_timer_clock(TIM_TypeDef* TIMx) {
    if (TIMx == TIM1 || TIMx == TIM8 || TIMx == TIM15 || 
        TIMx == TIM16 || TIMx == TIM17) {
        return get_apb2_timer_clock();
    } else {
        return get_apb1_timer_clock();
    }
}
