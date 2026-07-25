#ifndef INC_TASKS_OPTICALSENSOR_OPTICALSENSOR_MAIN_H_
#define INC_TASKS_OPTICALSENSOR_OPTICALSENSOR_MAIN_H_

#include "main.h"
#include "cmsis_os2.h"

#include "Config/config.h"

#include "MessagePassing/osqueue_helpers.h"


#ifdef __cplusplus
extern "C" {
#endif

// TIM4, clocked by the encoder pin itself (PD12/OP_IN_CLOCK -> TI1FP1, external clock mode 1).
// Its CNT is the pulse count; nothing here ever writes it.
extern TIM_HandleTypeDef* opticalCounterTimer;

// Called from HAL_TIM_PeriodElapsedCallback when TIM4's 16-bit counter wraps.
void opticalsensor_overflow_interrupt();
void opticalsensor_main(osMessageQueueId_t sessionControllerToForceSensorADCHandle);

#ifdef __cplusplus
}
#endif

#endif // INC_TASKS_OPTICALSENSOR_OPTICALSENSOR_MAIN_H_
