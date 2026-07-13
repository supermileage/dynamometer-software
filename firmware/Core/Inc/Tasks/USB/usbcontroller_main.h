#ifndef INC_TASKS_USB_USBCONTROLLER_MAIN_H_
#define INC_TASKS_USB_USBCONTROLLER_MAIN_H_

#include <stdint.h>
#include "cmsis_os2.h"

#include "main.h"

#ifdef __cplusplus
extern "C" {
#endif

void usbcontroller_main(osMessageQueueId_t sessionControllerToUsbController,
                        osMessageQueueId_t taskMonitorToUsbControllerHandle,
                        osMessageQueueId_t forceSensorCommandQueue,
                        osMessageQueueId_t taskCompletionQueue);

#ifdef __cplusplus
}
#endif



#endif /* INC_TASKS_USB_USBCONTROLLER_MAIN_H_ */
