#ifndef INC_TASKS_DISPLAY_DISPLAY_COMMON_H_
#define INC_TASKS_DISPLAY_DISPLAY_COMMON_H_

// The parts of reading a display message that are the message's business rather than any one
// panel's. Both layouts include this; neither includes the other.

#include <stdint.h>

#include "MessagePassing/messages_private.h"

#ifdef __cplusplus
extern "C" {
#endif

// The step one encoder tick applies at a given cursor position: 10000 down to 1.
//
// The message carries the cursor position rather than the step it implies, so that a panel with
// room can mark the digit itself instead of only printing a number. Panels that just print the
// number use this.
uint32_t display_rpm_digit_increment(display_rpm_digit digit);

#ifdef __cplusplus
}
#endif

#endif /* INC_TASKS_DISPLAY_DISPLAY_COMMON_H_ */
