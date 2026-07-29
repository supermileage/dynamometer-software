---
module: Lumex display
summary: Rendering for the Lumex 16x2 character LCD — the six screens, the cell diff, and what it does with the readouts it cannot show.
code:
  - Core/Inc/Tasks/Display/Lumex/LumexLCD.hpp
  - Core/Src/Tasks/Display/Lumex/LumexLCD.cpp
  - Core/Inc/Tasks/Display/Lumex/lumex_layout.h
  - Core/Src/Tasks/Display/Lumex/lumex_layout.c
  - Core/Inc/Tasks/Display/Lumex/lumexlcd_main.h
entry_point: lumex_lcd_main()
related: [Display, Lumex panel driver]
---

# Lumex display — rendering on a 2x16 character grid

Stages [4] to [6] of the path in [[Display]], for the character panel. The HD44780 protocol
underneath — instruction codes, the RS/E write cycle, DDRAM addressing — is
[[Lumex panel driver]] in `Drivers/Lumex`; this file is what goes on the screen and when.

Enabled with `LUMEX_LCD_TASK_ENABLE 1` in `Config/debug.h`.

---

## [4] Layout — `lumex_layout.c`

`lumex_render(state, frame)` is **pure**: no HAL, no RTOS, no driver state. Screen state in, a
full `lumex_frame` out —

```c
typedef struct { char cells[LUMEX_LCD_ROWS][LUMEX_LCD_COLUMNS]; } lumex_frame;   // 2 x 16
```

— with **every one of the 32 cells written on every call**, blanks as spaces. Nothing is left
over from a previous frame, so the result depends only on `state`. That is what makes the diff
in [5] valid, and what lets `tests/lumex_layout_tests.cpp` pin all six screens cell-for-cell on
the build machine.

### The six screens

Written as whole 16-character rows in the tests, because the bugs worth catching are
off-by-one column errors that a field-level check steps straight over.

```
IDLE                  SD_LOGGING            PID_ENABLE
      DYNO               SD LOGGING           PID LOGGING
  PRESS SELECT            DISABLED             DISABLED

DESIRED_RPM           DESIRED_RPM_EDIT      SESSION
  PID DES RPM           PID DES RPM         n:  1235 rpm
       5000              5000   100         F: 12.34 N  B 45
```

### Fixed-width fields

Every value is written at a fixed width — `%5lu` for RPM, six characters for force, four for
the drive mode. Not cosmetic: [[Display]] explains that neither panel supports
read-modify-write, so a shorter value only erases a longer one if it repaints the same cells.
`"ENABLED "` is padded to eight so it covers `"DISABLED"` exactly.

Values that could outgrow their field are clipped to it rather than allowed to shove their
neighbours.

### Two fixed layout bugs, recorded so they are not reintroduced

- **The force field used to strand two digits.** The row literal was
  `"F:    0.00 N    "`, carrying its own `0.00` at columns 6–9, while the force field wrote
  columns 2–7. Columns 8–9 were never rewritten, so 12.34 N displayed as `12.3400`. The
  literals now hold labels and units only, with each unit placed just past where its field
  ends.
- **The RPM readout showed rad/s.** See Units in [[Display]].

---

## [5] Render — `LumexLCD::Render`

Renders a frame, then writes only what differs from `_lastFrame`, in **runs of changed
cells**:

```cpp
for each row:
    walk the columns; where a cell differs, extend a run while cells keep differing;
    _panel.DisplayString(row, start, &frame.cells[row][start], runLength);
```

Runs rather than whole rows because the common in-session update moves one field — five cells
out of thirty-two.

**A change of `screen` forces a physical `ClearDisplay()`** before the repaint. That
reproduces the old FSM behaviour exactly: every `Show*Screen` used to clear, and the one
redraw that deliberately did not — a tick inside the RPM editor — is also the one that does
not change screen id. The rule is derived now rather than passed along as a flag.

**On a failed write, `_hasRendered = false`.** The panel no longer matches the shadow copy, so
the diff would skip cells that were never actually written. Forcing a full clear and repaint
next pass is what makes a glitch self-correcting.

### The three readouts it cannot show

`DisplayDriver` requires `ShowAngularAcceleration`, `ShowPeakForce` and `ShowSessionElapsed`
of every panel. Thirty-two cells are fully spoken for by speed, force and drive mode, so this
one accepts and discards them:

```cpp
bool ShowPeakForce(float newtons) { (void)newtons; return true; }
```

Inline and empty. They emit **no code at all** in this build — they do not even appear in its
`-fstack-usage` output — so the asymmetry costs nothing but the three lines. See [[Display]]
for why they sit on the concept rather than only on the TFT.

---

## [6] The panel — the task's side of it

`lumex_lcd_main()` constructs a `LumexLCD` on the task stack (it is small: one 32-byte frame
and a few flags), runs `Init()`, then hands off to `RunDisplayTask`.

This class supplies the panel driver with two things it deliberately does not reach for
itself:

- **the board wiring**, as a `LumexPanel::Pins` struct built from the `LUMEX_LCD_*` macros;
- **two delays**, because the waits are two different problems. `PanelDelayMs` is `osDelay`.
  `PanelDelayUs` busy-waits on the free-running microsecond timestamp counter, because
  `osDelay`'s floor at a 1 kHz tick is 1 ms and the enable pulse is 40 µs — rounding up would
  stretch a full repaint from ~2.6 ms to ~64 ms. [[Lumex panel driver]] explains why that 40 µs
  is not negotiable.

`Init()` starts the timestamp counter itself. `SessionController` also starts it, and starting
twice is harmless — doing it here is what keeps this task working when
`SESSION_CONTROLLER_TASK_ENABLE` is 0, which would otherwise leave `PanelDelayUs` waiting on a
frozen counter forever. The busy-wait is bounded as well as timed for the same reason.

---

## Timing

Two bytes per character cell (address + character), ~40 µs each:

| operation | time |
|---|---|
| one cell | ~80 µs |
| one 5-cell field | ~400 µs |
| full 32-cell repaint | ~2.6 ms |
| `CLEAR` (screen change) | ~20 ms |

Fast enough that the run-diff is an economy rather than a necessity — unlike the TFT, where a
full repaint is ~197 ms and diffing is what makes the panel usable at all.

## Errors

`ERROR_LUMEX_LCD_TIMER_START_FAILURE` and `ERROR_DISPLAY_INIT_FAILURE` →
`task_error_circular_buffer`, and from there to the host over USB.

## Key constants

`LUMEX_LCD_ROWS` / `LUMEX_LCD_COLUMNS` (`config.h`) — the character grid.
Timing constants are in `Drivers/Lumex/LumexPanel_main.h`.

## Related
[[Display]] · [[Lumex panel driver]] · [[SessionController]]
