---
module: LumexLCD
summary: Drives the Lumex character LCD; lays the SessionController's screen state onto a 2x16 grid.
code:
  - Core/Src/Tasks/LCD/LumexLCD.cpp
  - Core/Src/Tasks/LCD/lumex_layout.c
  - Core/Inc/Tasks/LCD/LumexLCD.hpp
  - Core/Inc/Tasks/LCD/lumex_layout.h
  - Core/Inc/Tasks/LCD/lumexlcd_main.h
entry_point: lumex_lcd_main()
task_offset: TASK_OFFSET_LUMEX_LCD
consumes: [session_controller_to_display (SessionController)]
produces: [task_error_circular_buffer]
related: [SessionController, MessagePassing]
---

# LumexLCD — character display task

Bit-bangs a Lumex parallel LCD over GPIO and renders the screen state the
[[SessionController]] FSM sends.

## The display seam

The FSM sends **what it is showing**, not how to draw it: `session_controller_to_display`
carries a `display_screen_id` plus every value any screen displays. Turning that into
characters is this task's job.

That split exists because a 16x2 character LCD and a 320x240 TFT have no useful common
drawing API — the intersection caps the TFT at 16x2, the union is meaningless here — so the
seam sits at what the values *mean* instead. `AddToLumexLCDMessageQueue(op, row, column,
string)` was the old protocol; it also left a driver unable to tell *which* quantity had
changed, since all it received was `("  1234", row 0, col 3)`.

## Flow
1. `lumex_lcd_main()` → construct, `Init()`, `Run()`.
2. `Init()`: 8-bit / 2-line / 5x8 font, display on (no cursor/blink), clear.
3. `Run()` blocks on the display queue, **drains to the newest message**, then `Render()`s it.
   Intermediate messages are skipped: each one is the whole screen state, so the ones behind
   the newest are already stale. Then delays `SYSCFG_LCD_TASK_OSDELAY`.

## Rendering
- `lumex_render()` (`lumex_layout.c`) is pure: screen state in, a full 2x16 `lumex_frame` out,
  every cell written. No HAL, no RTOS, no driver state — so `tests/lumex_layout_tests.cpp`
  pins all six screens cell-for-cell on the host.
- `Render()` diffs that frame against `_lastFrame` and writes only the runs that differ. The
  common in-session update moves one field: five cells out of thirty-two.
- A change of `screen` forces a physical `ClearDisplay()`. That reproduces the old behaviour
  exactly — every `Show*Screen` used to clear, and the one redraw that deliberately did not
  (a tick inside the RPM editor) is also the one that does not change screen id.

## Internals
- `SendByte` toggles the data GPIO lines; enable-pin timing is gated by a hardware timer
  (`StartTimer`).
- `WriteData` / `WriteCommand` / `SetCursor` / `DisplayChar` / `DisplayString` / `ToggleBlink`.

## Known display artifact
The session screen's force field is six characters at columns 2-7, but the label literal
carries `0.00` at columns 6-9 — so columns 8-9 keep a stale `00` and 12.34 N reads as
`12.3400`. Pre-existing; pinned by `SessionScreenForceFieldLeavesStaleDigits` rather than
blessed. Fixing it means shortening the literal in `lumex_layout.c`.

## Errors
- `ERROR_LUMEX_LCD_TIMER_START_FAILURE` → `task_error_circular_buffer`.

## Key constants
- `SYSCFG_LCD_TASK_OSDELAY` (sysconfig), `LUMEX_LCD_ROWS` / `LUMEX_LCD_COLUMNS` (config.h)

## Related
[[SessionController]] · [[MessagePassing]]
