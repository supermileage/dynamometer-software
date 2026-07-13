#ifndef INC_TASKS_FORCESENSOR_ADC_FORCESENSOR_ADC_MAIN_H_
#define INC_TASKS_FORCESENSOR_ADC_FORCESENSOR_ADC_MAIN_H_

#include "main.h"
#include "cmsis_os2.h"

#include "MessagePassing/osqueue_helpers.h"

#ifdef __cplusplus
extern "C" {
#endif

void forcesensor_adc_interrupt();
void forcesensor_adc_main(osMessageQueueId_t sessionControllerToForceSensorADCHandle);

#ifdef __cplusplus
}
#endif



#endif /* INC_TASKS_FORCESENSOR_ADC_FORCESENSOR_ADC_MAIN_H_ */
