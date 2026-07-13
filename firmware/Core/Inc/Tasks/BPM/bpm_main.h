#ifndef INC_TASKS_BPM_BPM_MAIN_H_
#define INC_TASKS_BPM_BPM_MAIN_H_

#include "main.h"
#include "cmsis_os2.h"


#ifdef __cplusplus
extern "C" {
#endif

void bpm_main(osMessageQueueId_t sessionControllerToBpmHandle, osMessageQueueId_t pidToBpmHandle);

#ifdef __cplusplus
}
#endif

#endif /* INC_TASKS_BPM_BPM_MAIN_H_ */
