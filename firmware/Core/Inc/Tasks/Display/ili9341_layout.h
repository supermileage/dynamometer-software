#ifndef INC_TASKS_DISPLAY_ILI9341_LAYOUT_H_
#define INC_TASKS_DISPLAY_ILI9341_LAYOUT_H_

// The ILI9341 panel's share of the display split: screen state in, positioned text fields out.
//
// The counterpart to Tasks/LCD/lumex_layout.h, and deliberately a different shape. Both take
// the same session_controller_to_display and neither constrains the other -- that is the point
// of sending screen state rather than draw commands. This one lays out a 320x240 landscape
// panel with several text sizes; the Lumex one lays out a 2x16 character grid.
//
// Free of HAL, RTOS and driver state so the host tests can pin every screen's geometry.

#include <stdbool.h>
#include <stdint.h>

#include "MessagePassing/messages_private.h"

#ifdef __cplusplus
extern "C" {
#endif

// Panel geometry in the orientation this layout assumes.
#define ILI9341_LAYOUT_WIDTH  320
#define ILI9341_LAYOUT_HEIGHT 240

// The session screen is the busiest: two labelled primary readouts, three detail readouts and
// the drive mode.
#define ILI9341_MAX_FIELDS     12
#define ILI9341_FIELD_TEXT_MAX 20

// One run of text at a fixed position and scale.
//
// `text` is fixed-width per screen and space-padded, never trimmed: drawing paints both
// foreground and background, so a field redrawn with a shorter value would leave the tail of
// the longer one behind. Padding is what erases it, exactly as on the character panel.
typedef struct
{
    uint16_t x;
    uint16_t y;
    uint16_t colour;
    uint8_t  size;     // font scale; the cell is 6*size by 8*size pixels
    uint8_t  length;
    char     text[ILI9341_FIELD_TEXT_MAX];
} ili9341_field;

typedef struct
{
    ili9341_field fields[ILI9341_MAX_FIELDS];
    uint8_t count;
} ili9341_frame;

// The extra in-session readouts this panel has room for and the character panel does not.
// Kept separate from session_controller_to_display so that what is common to every panel and
// what is this panel's alone stay visibly apart.
typedef struct
{
    float    angular_acceleration;   // rad/s^2
    float    peak_force;             // N, largest magnitude this session
    uint32_t session_seconds;        // since the session started
} ili9341_session_detail;

// Lays out one screen. For a given screen id the field count, order, positions and sizes are
// fixed, so the driver can diff field i against field i of the previous frame and repaint only
// those whose text or colour moved.
// `detail` is only read on the session screen; pass a zeroed struct elsewhere.
void ili9341_layout(const session_controller_to_display *state,
                    const ili9341_session_detail *detail,
                    ili9341_frame *out);

// Whether two fields would paint the same pixels. Position and size are stable within a
// screen, so in practice this compares text and colour.
bool ili9341_field_equal(const ili9341_field *a, const ili9341_field *b);

#ifdef __cplusplus
}
#endif

#endif /* INC_TASKS_DISPLAY_ILI9341_LAYOUT_H_ */
