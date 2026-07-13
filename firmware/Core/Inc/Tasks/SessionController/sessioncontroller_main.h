#ifndef INC_TASKS_SESSION_CONTROLLER_SESSION_CONTROLLER_MAIN_H_
#define INC_TASKS_SESSION_CONTROLLER_SESSION_CONTROLLER_MAIN_H_

#include "main.h"
#include "cmsis_os.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct 
{
    osMessageQueueId_t usb_controller;
    osMessageQueueId_t sd_controller;
    osMessageQueueId_t force_sensor;
    osMessageQueueId_t optical_sensor;
    osMessageQueueId_t bpm_controller;
    osMessageQueueId_t pid_controller;
    osMessageQueueId_t pid_controller_ack;
    osMessageQueueId_t lumex_lcd;
} session_controller_os_task_queues;


void sessioncontroller_main(session_controller_os_task_queues* task_queues, osMutexId_t usart1Mutex);

#ifdef __cplusplus
}
#endif

#endif /* INC_TASKS_SESSION_CONTROLLER_SESSION_CONTROLLER_MAIN_H_ */
