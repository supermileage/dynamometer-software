#ifndef INC_MESSAGEPASSING_OSQUEUE_HELPERS_H_
#define INC_MESSAGEPASSING_OSQUEUE_HELPERS_H_

#include <stdint.h>
#include <stdbool.h>
#include <stdlib.h>

#include "cmsis_os2.h"

#ifdef __cplusplus
extern "C" {
#endif

void EmptyQueue(osMessageQueueId_t qHandle, size_t itemSize);
bool GetLatestFromQueue(osMessageQueueId_t queueHandle, void* latestData, size_t itemSize, uint32_t timeout);

#ifdef __cplusplus
}
#endif



#endif /* INC_MESSAGEPASSING_OSQUEUE_HELPERS_H_ */
