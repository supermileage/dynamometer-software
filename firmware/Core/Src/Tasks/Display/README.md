---
module: Display
summary: The display task — screen state in, whichever panel is fitted out. Holds both panel drivers.
code:
  - Core/Inc/Tasks/Display/DisplayDriver.hpp
  - Core/Inc/Tasks/Display/display_common.h
  - Core/Src/Tasks/Display/display_common.c
  - Core/Inc/Tasks/Display/Lumex/LumexLCD.hpp
  - Core/Src/Tasks/Display/Lumex/LumexLCD.cpp
  - Core/Inc/Tasks/Display/Lumex/lumex_layout.h
  - Core/Src/Tasks/Display/Lumex/lumex_layout.c
  - Core/Inc/Tasks/Display/Lumex/lumexlcd_main.h
  - Core/Inc/Tasks/Display/ILI9341/ILI9341Display.hpp
  - Core/Src/Tasks/Display/ILI9341/ILI9341Display.cpp
  - Core/Inc/Tasks/Display/ILI9341/ili9341_layout.h
  - Core/Src/Tasks/Display/ILI9341/ili9341_layout.c
  - Core/Inc/Tasks/Display/ILI9341/ili9341_main.h
entry_point: lumex_lcd_main() / ili9341_lcd_main()
task_offset: TASK_OFFSET_DISPLAY
consumes: [session_controller_to_display (SessionController)]
produces: [task_error_circular_buffer]
related: [SessionController, MessagePassing]
---

# Display — the panel-independent seam

Two panels are supported and exactly one is compiled in: the Lumex 16x2 character LCD and an
ILI9341 320x240 TFT. Both read the same queue and the same message.

## Layout

```
Tasks/Display/
  DisplayDriver.hpp     the concept every panel satisfies + the shared task loop
  display_common.{h,c}  helpers neither panel owns (cursor step, fixed-point formatting)
  Lumex/                the 16x2 character driver and its layout
  ILI9341/              the 320x240 TFT driver and its layout
```

Both panels used to live apart, under `Tasks/LCD/` and `Tasks/Display/`, which read as two
modules; they are one task with one `task_offset` reading one queue, so they are one
directory. Neither panel subdirectory includes the other — the only shared code is the two
files at this level.

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

## Lumex rendering (`Lumex/`)

- `lumex_lcd_main()` → construct, `Init()` (8-bit / 2-line / 5x8 font, display on, clear),
  then `RunDisplayTask`.
- `lumex_render()` is pure: screen state in, a full 2x16 `lumex_frame` out, every cell
  written. No HAL, no RTOS, no driver state — `tests/lumex_layout_tests.cpp` pins all six
  screens cell-for-cell on the host.
- `Render()` diffs that frame against `_lastFrame` and writes only the runs that differ. The
  common in-session update moves one field: five cells out of thirty-two.
- A change of `screen` forces a physical `ClearDisplay()`. That reproduces the old behaviour
  exactly — every `Show*Screen` used to clear, and the one redraw that deliberately did not
  (a tick inside the RPM editor) is also the one that does not change screen id.
- `SendByte` toggles the data GPIO lines; enable-pin timing is gated by a hardware timer
  (`StartTimer`), which is microsecond-scale. Millisecond waits use `osDelay`, never
  `HAL_Delay` — this runs in a task, and spinning there burns CPU that other tasks want.
- `ERROR_LUMEX_LCD_TIMER_START_FAILURE` → `task_error_circular_buffer`.

## ILI9341 rendering (`ILI9341/`)

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

- `ERROR_DISPLAY_INIT_FAILURE`, `ERROR_DISPLAY_SPI_TRANSMIT_FAILURE` →
  `task_error_circular_buffer`.

## Units

`session_controller_to_display.rpm` is RPM. The optical encoder measures rad/s, and the FSM
converts once on the way in via `encoder_rpm()` ([[OpticalSensor]]) — so a driver renders the
number it is given and no panel repeats the conversion.

## Key constants

`SYSCFG_LCD_TASK_OSDELAY` (sysconfig) · `LUMEX_LCD_ROWS` / `LUMEX_LCD_COLUMNS` /
`ILI9341_DISPLAY_ROTATION` (config.h)

## Related
[[ILI9341 driver]] · [[SessionController]] · [[OpticalSensor]] · [[MessagePassing]]
