#ifndef INC_TASKS_TASKMONITOR_TASKMONITOR_MAIN_H_
#define INC_TASKS_TASKMONITOR_TASKMONITOR_MAIN_H_

#ifdef __cplusplus
extern "C" {
#endif

#include "cmsis_os2.h"

typedef struct 
{
    osThreadId_t session_controller;
    osThreadId_t usb_controller;
    osThreadId_t sd_controller;
    osThreadId_t force_sensor;
    osThreadId_t optical_sensor;
    osThreadId_t bpm_controller;
    osThreadId_t pid_controller;
    osThreadId_t pid_controller_ack;
    osThreadId_t lumex_lcd;
} taskmonitor_osthreadids;


void taskmonitor_main(taskmonitor_osthreadids* osthreadid_ptrs, osMessageQueueId_t taskMonitorToUsbControllerHandle);

#ifdef __cplusplus
}
#endif

#endif /* INC_TASKS_TASKMONITOR_TASKMONITOR_MAIN_H_ */