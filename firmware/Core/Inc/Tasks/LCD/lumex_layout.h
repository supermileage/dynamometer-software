#ifndef INC_TASKS_LCD_LUMEX_LAYOUT_H_
#define INC_TASKS_LCD_LUMEX_LAYOUT_H_

// The Lumex panel's share of the display split: screen state in, a 2x16 character grid out.
//
// This is deliberately free of HAL, RTOS and driver state so the host tests can pin every
// screen against the literals the FSM used to write directly. It used to live in
// FiniteStateMachine.cpp as runs of WriteText(row, column, "...") with hand-counted padding
// and column offsets -- layout, not state-machine logic, and the wrong side of the seam once
// a second panel exists.

#include <stdint.h>

#include "Config/config.h"
#include "MessagePassing/messages_private.h"

#ifdef __cplusplus
extern "C" {
#endif

// A whole panel's worth of characters. Not NUL-terminated: every cell is a character to be
// written, and blank cells are spaces, so the grid is always exactly full.
typedef struct
{
    char cells[LUMEX_LCD_ROWS][LUMEX_LCD_COLUMNS];
} lumex_frame;

// Renders one screen. Every cell is written on every call -- unset cells become spaces -- so
// the result depends only on `state` and never on what was on screen before. The driver is
// what turns two of these into the minimal set of writes.
void lumex_render(const session_controller_to_display *state, lumex_frame *out);

// The step size the encoder applies at a given cursor position: 10000 down to 1.
uint32_t lumex_rpm_digit_increment(display_rpm_digit digit);

#ifdef __cplusplus
}
#endif

#endif /* INC_TASKS_LCD_LUMEX_LAYOUT_H_ */
