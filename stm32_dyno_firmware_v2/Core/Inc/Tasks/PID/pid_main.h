#ifndef INC_TASKS_PID_PID_MAIN_H_
#define INC_TASKS_PID_PID_MAIN_H_

#include "main.h"

#include "cmsis_os2.h"

#include <stdbool.h>


#ifdef __cplusplus
extern "C" {
#endif

void pid_main(osMessageQueueId_t sessionControllerToPidControllerHandle, osMessageQueueId_t pidControllerToSessionControllerAckHandle, osMessageQueueId_t pidToBpmHandle, bool initialState);

#ifdef __cplusplus
}
#endif

#endif /* INC_TASKS_PID_PID_MAIN_H_ */
