#ifndef INC_TASKS_USB_USB_FRAMER_H_
#define INC_TASKS_USB_USB_FRAMER_H_

#include <stdint.h>
#include <stddef.h>

#include "MessagePassing/messages_public.h"

/*
 * Inbound (host -> device) frame parser over the usb_rx_ring byte stream.
 *
 * Pulls one complete, CRC-validated frame ([SOF][usb_msg_header_t][payload][crc16], all
 * little-endian) out of the RX ring per call. Garbage, spurious SOF markers and corrupt
 * frames are skipped byte-wise so the stream resyncs after noise or an overflow; a frame is
 * consumed from the ring only after it validates in full, so a partial arrival is simply
 * retried on the next poll.
 *
 * Free-standing (no HAL/RTOS includes) so the unit tests in firmware/tests/ can compile it
 * for the desktop host and drive it by pushing bytes into the ring; on the board it is the
 * body of USBController::TryReadFrame.
 */

/* Returns true and fills header/payload/payloadLen when a frame is ready; false when no
   complete frame is available yet (non-blocking). A frame whose payload exceeds
   payloadCapacity (or USB_RX_MAX_PAYLOAD) is treated as a spurious SOF and resynced past. */
bool usb_framer_try_read_frame(
    usb_msg_header_t& header, uint8_t* payload, size_t payloadCapacity, size_t& payloadLen);

#endif /* INC_TASKS_USB_USB_FRAMER_H_ */
