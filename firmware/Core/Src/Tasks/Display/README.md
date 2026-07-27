---
module: Display
summary: The display seam — screen state in, whichever panel is fitted out. Holds the ILI9341 driver.
code:
  - Core/Inc/Tasks/Display/DisplayDriver.hpp
  - Core/Inc/Tasks/Display/display_common.h
  - Core/Src/Tasks/Display/display_common.c
  - Core/Inc/Tasks/Display/ILI9341Display.hpp
  - Core/Src/Tasks/Display/ILI9341Display.cpp
  - Core/Inc/Tasks/Display/ili9341_layout.h
  - Core/Src/Tasks/Display/ili9341_layout.c
  - Core/Inc/Tasks/Display/ili9341_main.h
entry_point: ili9341_lcd_main()
task_offset: TASK_OFFSET_DISPLAY
consumes: [session_controller_to_display (SessionController)]
produces: [task_error_circular_buffer]
related: [LumexLCD, SessionController, MessagePassing]
---

# Display — the panel-independent seam

Two panels are supported and exactly one is compiled in: the Lumex 16x2 character LCD
([[LumexLCD]]) and an ILI9341 320x240 TFT. Both read the same queue and the same message.

## The contract

`session_controller_to_display` carries a `display_screen_id` plus every value any screen
shows. The FSM says **what it is displaying**; each driver decides **how**.

There is deliberately no common *drawing* API. The intersection of a character grid and a
320x240 TFT (`WriteText(row, column, string)`) caps the TFT at 16x2; the union
(`DrawRect`, `SetFont`, `DrawBitmap`) is meaningless on the LCD. Putting the seam at what
the values *mean* leaves each panel free: everything the TFT can do that the LCD cannot
lives inside its `Render()` and never appears in the contract.

## DisplayDriver — a concept, not a base class

```cpp
template <typename T>
concept DisplayDriver = requires(T d, const session_controller_to_display& s) {
    { d.Init()     } -> std::same_as<bool>;
    { d.Clear()    } -> std::same_as<bool>;
    { d.Render(s)  } -> std::same_as<bool>;
};
```

Virtual dispatch would cost a vtable pointer and an indirect call per draw for a choice
fixed at link time. The concept checks the same contract at compile time and inlines
through it. Each driver's `.cpp` carries `static_assert(DisplayDriver<...>)`, so a
signature mismatch is an error at the driver rather than at the call site.

`RunDisplayTask<Display>` is the shared queue-drain loop. It **drains to the newest
message** before drawing: each one is the whole screen state, so the ones behind it are
already stale.

## Choosing a panel

`Core/Inc/Config/debug.h`, exactly one set to 1:

```c
#define LUMEX_LCD_TASK_ENABLE   1
#define ILI9341_LCD_TASK_ENABLE 0
```

A `#error` catches both or neither. `lcdDisplayTaskEntryFunction` in `main.c` dispatches to
`lumex_lcd_main()` or `ili9341_lcd_main()`. Both drivers are always compiled;
`--gc-sections` drops the unused one.

## ILI9341 rendering

- `ili9341_layout()` is pure: screen state in, up to `ILI9341_MAX_FIELDS` positioned text
  fields out. No HAL, no RTOS — `tests/ili9341_layout_tests.cpp` checks it host-side.
- For a given screen the field list is **positionally stable**: same count, order,
  positions and widths whatever the values. That is what makes the driver's index-wise diff
  valid, and it is asserted in the tests rather than assumed.
- Fields are fixed-width and space-padded. Drawing paints foreground *and* background, so a
  redraw erases the previous value — there is no read-modify-write on this bus.
- `Render()` repaints only fields whose text or colour moved. A change of `screen` clears
  and repaints in full. This is not an optimisation: a full frame is ~98 ms at 12.5 MHz,
  against ~1-2 ms for one field.

## Errors
`ERROR_DISPLAY_INIT_FAILURE`, `ERROR_DISPLAY_SPI_TRANSMIT_FAILURE` →
`task_error_circular_buffer`.

## Related
[[LumexLCD]] · [[ILI9341 driver]] · [[SessionController]] · [[MessagePassing]]
