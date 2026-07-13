#ifndef INC_TASKS_OPTICALSENSOR_OPTICALSENSOR_MAIN_H_
#define INC_TASKS_OPTICALSENSOR_OPTICALSENSOR_MAIN_H_

#include "main.h"
#include "cmsis_os2.h"

#include "Config/config.h"

#include "MessagePassing/osqueue_helpers.h"


#ifdef __cplusplus
extern "C" {
#endif

void opticalsensor_input_interrupt();
void opticalsensor_main(osMessageQueueId_t sessionControllerToForceSensorADCHandle);

#ifdef __cplusplus
}
#endif

#endif // INC_TASKS_OPTICALSENSOR_OPTICALSENSOR_MAIN_H_
