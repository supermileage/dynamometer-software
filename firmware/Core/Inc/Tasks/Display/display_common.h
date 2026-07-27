#ifndef INC_TASKS_DISPLAY_DISPLAY_COMMON_H_
#define INC_TASKS_DISPLAY_DISPLAY_COMMON_H_

// The parts of reading a display message that are the message's business rather than any one
// panel's. Both layouts include this; neither includes the other.

#include <stddef.h>
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

// Formats `value` to two decimal places, right-aligned in `width` columns -- what "%*.2f"
// would produce, without the float.
//
// snprintf("%f") drags in newlib's floating-point formatter, which needs several hundred
// bytes of stack and is the one call in this path whose cost cannot be read off the
// -fstack-usage output. A display task runs on a kilobyte, so it stays out. Rendering a force
// reading was the only float conversion in the firmware, and it overflowed the stack: the
// overflow hook disables interrupts and spins, which looks exactly like a dead board.
//
// Rounds half away from zero, like printf. Values wider than `width` are not truncated, again
// matching printf.
void display_format_fixed2(char *out, size_t out_size, float value, int width);

#ifdef __cplusplus
}
#endif

#endif /* INC_TASKS_DISPLAY_DISPLAY_COMMON_H_ */
