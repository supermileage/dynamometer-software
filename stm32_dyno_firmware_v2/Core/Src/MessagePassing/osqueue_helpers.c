#include "MessagePassing/osqueue_helpers.h"


/**
 * @brief Empty all messages from a CMSIS-RTOS2 message queue.
 * @param qHandle: handle of the queue
 * @param itemSize: size of one queue item (in bytes)
 */
void EmptyQueue(osMessageQueueId_t qHandle, size_t itemSize)
{
    osStatus_t status;
    uint8_t temp[itemSize];  // temporary buffer on the stack

    // Keep reading until queue is empty
    while ((status = osMessageQueueGet(qHandle, temp, NULL, 0)) == osOK)
    {
        // do nothing, just discard
    }

    // If status is not osOK, queue is either empty or an error occurred (can ignore)
}


/**
 * @brief Get the latest item from a message queue (blocking or non-blocking)
 * @param queueHandle: handle of the message queue
 * @param latestData: pointer to memory where the latest item will be stored
 * @param itemSize: size of one queue item (in bytes)
 * @param timeout: osWaitForever for blocking, 0 for non-blocking, or a timeout in ticks
 * @retval true if at least one item was retrieved, false if queue was empty or timed out
 */
bool GetLatestFromQueue(osMessageQueueId_t queueHandle, void* latestData, size_t itemSize, uint32_t timeout)
{
    osStatus_t status;
    uint8_t temp[itemSize];  // temporary buffer for each dequeue
    bool gotData = false;

    // First get: either blocking or non-blocking depending on timeout
    status = osMessageQueueGet(queueHandle, temp, NULL, timeout);
    if (status != osOK) return false;

    // Copy the first item to latestData
    for (size_t i = 0; i < itemSize; i++)
    {
        ((uint8_t*)latestData)[i] = temp[i];
    }
    gotData = true;

    // Drain any remaining items (non-blocking) to get the latest
    while ((status = osMessageQueueGet(queueHandle, temp, NULL, 0)) == osOK)
    {
        for (size_t i = 0; i < itemSize; i++)
        {
            ((uint8_t*)latestData)[i] = temp[i];
        }
    }

    return gotData;
}
