---
module: ILI9341 display
summary: Rendering for the ILI9341 320x240 TFT — the field model, the diff the panel's speed forces, and the session-detail readouts.
code:
  - Core/Inc/Tasks/Display/ILI9341/ILI9341Display.hpp
  - Core/Src/Tasks/Display/ILI9341/ILI9341Display.cpp
  - Core/Inc/Tasks/Display/ILI9341/ili9341_layout.h
  - Core/Src/Tasks/Display/ILI9341/ili9341_layout.c
  - Core/Inc/Tasks/Display/ILI9341/ili9341_main.h
entry_point: ili9341_lcd_main()
related: [Display, ILI9341 driver]
---

# ILI9341 display — rendering on a 320x240 TFT

Stages [4] to [6] of the path in [[Display]], for the TFT. The wire protocol underneath —
`CASET`/`PASET`/`RAMWR`, RGB565, glyph bitmaps — is [[ILI9341 driver]] in `Drivers/ILI9341`;
this file is what goes on the screen and when.

Enabled with `ILI9341_LCD_TASK_ENABLE 1` in `Config/debug.h`. Landscape, 320x240; which way up
is `ILI9341_DISPLAY_ROTATION` in `config.h`, a property of the enclosure rather than of the
driver.

---

## [4] Layout — `ili9341_layout.c`

`ili9341_layout(state, detail, frame)` is **pure**: no HAL, no RTOS, no driver state. Screen
state in, up to `ILI9341_MAX_FIELDS` positioned runs of text out:

```c
typedef struct {
    uint16_t x, y;
    uint16_t colour;
    uint8_t  size;      // font scale; the cell is 6*size by 8*size pixels
    uint8_t  length;
    char     text[ILI9341_FIELD_TEXT_MAX];
} ili9341_field;
```

`tests/ili9341_layout_tests.cpp` checks it on the build machine.

**Nothing measures available space.** Every coordinate is a number typed into the layout:

```c
add_field(out, 12, 40, SIZE_VALUE, COLOUR_VALUE, scratch);   // x=12, y=40, size 5
```

`centred()` does the arithmetic for centred rows and that is the extent of it. There is no
reflow and no auto-fit, because the driver below **clips rather than shrinks** — text that
does not fit is lost, not resized. Fitting is this file's job, done up front.

### Two properties the driver depends on

Asserted by tests rather than assumed, because both are invisible until they break:

- **Positionally stable.** For a given screen: the same field count, order, positions and
  widths whatever the values are. `AScreensFieldListIsPositionallyStable` lays out every screen
  with zeroed and with extreme values and compares. This is what makes the index-wise diff in
  [5] valid rather than accidental.
- **Fixed width, space-padded.** `"ENABLED "` is padded to eight so it covers `"DISABLED"`
  exactly, and the detail readouts **clamp** (`A 99999`, `P999.99`, `T9999s`) so a large
  reading cannot outgrow its slot and shift its neighbours.

Both descend from the same fact as the character panel's rules: no read-modify-write, so a
field is erased only by being repainted, background and all. See [[Display]].

### Session screen field order

The index-wise diff depends on this order, so it is written down:

| # | field | position | size | colour |
|---|---|---|---|---|
| 0 | `SPEED` label | (12, 18) | 2 | grey |
| 1 | RPM value | (12, 40) | 5 | white |
| 2 | `rpm` unit | (172, 64) | 2 | grey |
| 3 | `FORCE` label | (12, 100) | 2 | grey |
| 4 | force value | (12, 122) | 5 | white |
| 5 | `N` unit | (200, 146) | 2 | grey |
| 6 | `A` angular acceleration | (12, 168) | 2 | grey |
| 7 | `P` peak force | (108, 168) | 2 | grey |
| 8 | `T` session elapsed | (216, 168) | 2 | grey |
| 9 | drive mode | (12, 196) | 3 | green / red / yellow |

Fields 6–8 are the detail row — the readouts the character panel has no room for. The drive
mode is `PID ARMED` / `PID OFF` when the menu allows arming it, otherwise `BRAKE nnn%`; both
are ten characters so one paints over the other.

Everything is drawn on `ILI9341_BLACK`; each field carries its own foreground.

---

## [5] Render — `ILI9341Display::Render`

```cpp
const bool screenChanged = !_hasRendered || state.screen != _lastScreen;
if (screenChanged && !Clear()) { _hasRendered = false; return false; }

for (i...) {
    if (!screenChanged && ili9341_field_equal(&_frame.fields[i], &_lastFrame.fields[i]))
        continue;
    DrawField(_frame.fields[i]);
}
```

**This is not an optimisation.** A full frame is 320x240x16bpp = 153,600 bytes, ~197 ms at
6.25 MHz — a repaint per sensor sample is impossible. One field is ~18 ms. Diffing is what
makes the panel usable at all.

A change of `screen` clears and repaints in full: a different screen has a different set of
fields in different places, so there is nothing meaningful to diff against.

Details that matter:

- **Field equality includes colour.** The drive-mode field keeps its width but changes
  green↔red; a text-only comparison would leave it the wrong colour.
- **On failure, `_hasRendered = false`.** The panel no longer matches the shadow copy, so the
  diff would skip fields that were never actually painted. Forcing a full clear and repaint on
  the next pass is what makes a glitch self-correcting.

### The session detail readouts

`ShowAngularAcceleration`, `ShowPeakForce` and `ShowSessionElapsed` **only record** into
`_detail`; drawing happens in `Render`. That is deliberate — everything on screen goes through
one layout pass and one diff, and these must not paint behind its back.

The character panel discards the same three calls as no-ops. See [[Display]] for why they are
on the concept at all.

---

## [6] The panel — the task's side of it

`ili9341_lcd_main()` constructs the driver as a **function-local static**, not a local:

```cpp
static ILI9341Display display;
```

The display task runs on 1 KB of stack and this object carries **two** `ili9341_frame`s — the
frame being built and the last one painted — which at `ILI9341_MAX_FIELDS` entries is several
hundred bytes. It belongs in `.bss`. `-fno-threadsafe-statics` is set and the function runs
exactly once, so there is no guard variable and no initialisation race.

The class supplies the panel driver with the board wiring (`hspi1`, CS/DC/RST) and a
`DisplayDelayMs` that is `osDelay` — the driver takes the delay as a callback so it stays free
of `cmsis_os2.h`, and under an RTOS the right answer is to yield rather than spin through
`Init()`'s ~325 ms of waits.

---

## Timing

At 6.25 MHz, 16 bits per pixel:

| operation | pixels | bytes | time |
|---|---|---|---|
| full screen | 76,800 | 153,600 | ~197 ms |
| one size-5 field, 6 chars | 7,200 | 14,400 | ~18 ms |
| one size-3 character | 432 | 864 | ~1.1 ms |

Two orders of magnitude slower to repaint than the character panel, for 2,400 times as many
addressable dots.

## Errors

`ERROR_DISPLAY_INIT_FAILURE`, `ERROR_DISPLAY_SPI_TRANSMIT_FAILURE` →
`task_error_circular_buffer`, and from there to the host over USB. A transmit failure is
reported and survived, never fatal — see [[Display]].

## Key constants

`ILI9341_MAX_FIELDS` / `ILI9341_FIELD_TEXT_MAX` / `ILI9341_LAYOUT_WIDTH` /
`ILI9341_LAYOUT_HEIGHT` (`ili9341_layout.h`) · `ILI9341_DISPLAY_ROTATION` (`config.h`) ·
`ILI9341_MAX_TEXT_SIZE` (`Drivers/ILI9341/ILI9341_main.h`)

## Related
[[Display]] · [[ILI9341 driver]] · [[SessionController]]
