#ifndef INC_TASKS_USB_USB_RX_RING_H_
#define INC_TASKS_USB_USB_RX_RING_H_

#include <stdint.h>
#include <stddef.h>

/*
 * Host -> device RX byte ring.
 *
 * Single-producer / single-consumer: the producer is CDC_Receive_FS (USB ISR context,
 * usbd_cdc_if.c) calling usb_rx_push(); the consumer is the USB task reading through the
 * functions below. No lock is required: the ISR only advances the write index and the task
 * only advances the read index, and aligned 32-bit index reads/writes are atomic on
 * Cortex-M. One slot is left empty so a full ring is distinguishable from an empty one.
 *
 * This module is deliberately free of HAL/RTOS includes so it can also be compiled and
 * exercised on a desktop host by the unit tests in firmware/tests/, where the tests play
 * the ISR by calling usb_rx_push() directly.
 */

#ifdef __cplusplus
extern "C" {
#endif

/* Ring capacity in bytes of storage; usable capacity is one less (see above). */
#define USB_CONTROLLER_RX_BUFFER_SIZE 512

/* Producer side (USB ISR context): append bytes to the ring. If the ring would lap
   the consumer, drop the remainder and raise the overflow flag so the frame parser
   can resync at the next start-of-frame marker. */
void   usb_rx_push(const uint8_t *data, uint32_t len);

size_t usb_rx_available(void);              /* bytes ready to read */
size_t usb_rx_peek(uint8_t *dst, size_t n); /* copy up to n bytes without consuming */
size_t usb_rx_read(uint8_t *dst, size_t n); /* copy and consume up to n bytes */
void   usb_rx_skip(size_t n);               /* discard up to n bytes */
int    usb_rx_overflowed(void);             /* read-and-clear the overflow flag */
void   usb_rx_flush(void);                  /* discard all buffered bytes (resync after overflow) */

#ifdef __cplusplus
}
#endif

#endif /* INC_TASKS_USB_USB_RX_RING_H_ */
