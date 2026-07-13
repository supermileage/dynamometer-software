---
module: LumexLCD
summary: Drives the Lumex character LCD; renders strings the SessionController FSM sends.
code:
  - Core/Src/Tasks/LCD/LumexLCD.cpp
  - Core/Inc/Tasks/LCD/LumexLCD.hpp
  - Core/Inc/Tasks/LCD/lumexlcd_main.h
entry_point: lumex_lcd_main()
task_offset: TASK_OFFSET_LUMEX_LCD
consumes: [session_controller_to_lumex_lcd (SessionController)]
produces: [task_error_circular_buffer]
related: [SessionController, MessagePassing]
---

# LumexLCD — character display task

Bit-bangs a Lumex parallel LCD over GPIO and renders what the [[SessionController]] FSM sends.

## Flow
1. `lumex_lcd_main()` → construct, `Init()`, `Run()`.
2. `Init()`: 8-bit / 2-line / 5×8 font, display on (no cursor/blink), clear.
3. `Run()` blocks on `session_controller_to_lumex_lcd`; opcodes:
   - `CLEAR_DISPLAY` — clear screen.
   - `WRITE_TO_DISPLAY` — write `display_string` at `(row, column)`.
   Drains the queue each wake, then delays `LCD_TASK_OSDELAY`.

## Internals
- `SendByte` toggles the data GPIO lines; enable-pin timing is gated by a hardware timer (`StartTimer`).
- `WriteData` / `WriteCommand` / `SetCursor` / `DisplayChar` / `DisplayString` / `ToggleBlink`.

## Errors
- `ERROR_LUMEX_LCD_TIMER_START_FAILURE` → `task_error_circular_buffer`.

## Key constants (config.h)
- `LCD_TASK_OSDELAY`, `SESSION_CONTROLLER_TO_LUMEX_LCD_MSG_STRING_SIZE`

## Related
[[SessionController]] · [[MessagePassing]]
