#ifndef INC_TASKS_DISPLAY_ILI9341DISPLAY_HPP_
#define INC_TASKS_DISPLAY_ILI9341DISPLAY_HPP_

#include "main.h"

#include "CircularBufferWriter.hpp"

#include "ILI9341.hpp"

#include "MessagePassing/messages_private.h"
#include "MessagePassing/messages_public.h"

#include "Tasks/Display/ili9341_layout.h"

// The ILI9341's side of the display split: turns screen state into painted pixels.
//
// The counterpart to LumexLCD, and satisfies the same DisplayDriver concept without sharing a
// base class with it. Everything this panel can do that the character LCD cannot -- colour,
// several text sizes, arbitrary positioning -- lives inside Render() and never surfaces in the
// contract, which is the whole reason the contract is screen state rather than draw calls.
class ILI9341Display
{
public:
    ILI9341Display();
    ~ILI9341Display() = default;

    bool Init();
    bool Clear();
    bool Render(const session_controller_to_display& state);

private:
    // Paints one field over its own background, which is also how the previous value is erased:
    // fields are fixed-width per screen, so a redraw covers every pixel the old one touched.
    bool DrawField(const ili9341_field& field);

    ILI9341 _panel;

    CircularBufferWriter<task_error_data> _task_error_buffer_writer;

    // What is currently painted, and which screen put it there. A change of screen repaints
    // from a cleared panel; within a screen the layout is positionally stable, so field i can
    // be compared against field i and only the movers redrawn.
    ili9341_frame _lastFrame;

    // Scratch for the frame being rendered. A member rather than a local in Render() because
    // it is ~256 bytes and this object is a static in ili9341_lcd_main(), so it lands in .bss
    // instead of on the display task's kilobyte of stack.
    ili9341_frame _frame;
    display_screen_id _lastScreen;
    bool _hasRendered;
};

#endif /* INC_TASKS_DISPLAY_ILI9341DISPLAY_HPP_ */
