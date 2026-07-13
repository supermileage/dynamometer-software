#ifndef INC_TASKS_FORCESENSOR_ADS1115_FORCESENSOR_ADS1115_MAIN_H_
#define INC_TASKS_FORCESENSOR_ADS1115_FORCESENSOR_ADS1115_MAIN_H_

#include "main.h"
#include "cmsis_os2.h"

#include "Config/config.h"

#include "MessagePassing/osqueue_helpers.h"

#ifdef __cplusplus
extern "C" {
#endif

void forcesensor_ads1115_gpio_alert_interrupt(void);
void forcesensor_ads1115_main(osMessageQueueId_t sessionControllerToForcesensorADS1115Handle,
                              osMessageQueueId_t usbToForceSensorCommandHandle,
                              osMessageQueueId_t taskCompletionHandle);

#ifdef __cplusplus
}
#endif



#endif /* INC_TASKS_FORCESENSOR_ADS1115_FORCESENSOR_ADS1115_MAIN_H_ */
