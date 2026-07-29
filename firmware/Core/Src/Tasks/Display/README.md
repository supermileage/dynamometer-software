---
module: Display
summary: The display task — how a measured value becomes lit pixels, and why the seam sits where it does.
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
related: [SessionController, MessagePassing, ILI9341 driver]
---

# Display — from a measured value to lit pixels

Two panels are supported and at most one is compiled in: a Lumex 16x2 character LCD and an
ILI9341 320x240 TFT. Both read the same queue and the same message.

---

## The whole path, end to end

Follow a force reading from the sensor to the glass. Every stage discards work the next one
does not need, and that is the point of having five of them.

```
 ForceSensor task
      |  forcesensor_output_data -> circular buffer
      v
 SessionController::UpdateMeasurementDisplay()          [1] only on change
      |  _fsm.DisplayForce(12.34f)
      v
 FSM::PostDisplayState()                                [2] meaning, not pixels
      |  session_controller_to_display  { screen, rpm, force, ... }
      |  osMessageQueuePut(..., timeout 0)   <- never blocks the SessionController
      v
 RunDisplayTask()  (DisplayDriver.hpp)                  [3] drain to newest
      |  display.Render(state)
      v
 ili9341_layout()  /  lumex_render()                    [4] state -> positions + text
      |  ili9341_frame { fields[], count }   (pure function, host-tested)
      v
 ILI9341Display::Render()  /  LumexLCD::Render()        [5] diff, then paint the movers
      |  _panel.DrawString(x, y, " 12.34", 6, WHITE, BLACK, 5)
      v
 ILI9341 driver                                         [6] pixels on the wire
         CASET / PASET / RAMWR + 14,400 bytes of RGB565
```

### [1] The SessionController posts only what moved

`UpdateMeasurementDisplay()` compares against `_prevForce` / `_prevAngularVelocity` and calls
the FSM only when a reading actually changes. First filter.

### [2] The FSM sends meaning, never pixels

`PostDisplayState()` fills a `session_controller_to_display` — a `display_screen_id` plus
**every value any screen shows** — and posts it:

```c
osMessageQueuePut(_toDisplayHandle, &msg, 0, 0);
```

Two deliberate choices:

- **Whole state every time**, not deltas. A driver that misses a message is still correct on
  the next one; there is no incremental state to get out of step.
- **Timeout 0.** A full queue drops the message rather than blocking. The display is the
  least important thing on this board and must never stall the task that drives the brake.

The FSM formats nothing. It used to: the layout lived here as
`WriteText(row, column, "n:     0 rpm    ")` with hand-counted padding, which is a 16x2
character grid baked into a state machine.

### [3] The task loop takes only the newest message

`RunDisplayTask` (`DisplayDriver.hpp`) blocks on the queue, then **drains to the newest**
before drawing anything:

```cpp
while (osMessageQueueGet(queue, &state, 0, 0) == osOK);
```

Each message is the whole screen state, so anything behind the newest is already stale.
Rendering them in turn would paint values nobody will ever see — and on a panel where a
field costs ~18 ms, that is the difference between keeping up and falling behind.

It is `[[noreturn]]`, and that is load-bearing: **a FreeRTOS task function that returns lands
in `prvTaskExitError()`, which fails a `configASSERT`, disables interrupts and spins.** The
whole rig dies — buttons, brake and all — with no LED and no fault report. An early version
did `if (!Render(state)) return;`, and one failed SPI write took the entire dynamometer down.
A failed render is now recorded in the error buffer and the loop carries on.

### [4] Layout: screen state in, positioned text out

Each panel has its own layout function, and they are **pure**: no HAL, no RTOS, no driver
state, so `tests/ili9341_layout_tests.cpp` and `tests/lumex_layout_tests.cpp` check every
screen on the build machine.

- `lumex_render()` → a full 2x16 `lumex_frame`, every cell written, blanks as spaces.
- `ili9341_layout()` → up to `ILI9341_MAX_FIELDS` `ili9341_field`s, each `{x, y, colour,
  size, length, text}`.

**Nothing measures available space.** Every coordinate is a number typed into the layout:

```c
add_field(out, 12, 40, SIZE_VALUE, COLOUR_VALUE, scratch);   // x=12, y=40, size 5
```

`centred()` does the arithmetic for centred rows, and that is the extent of it. There is no
reflow and no auto-fit, because the driver below clips rather than shrinks.

Two properties the driver **depends on**, asserted by tests rather than assumed:

- **Positionally stable** — for a given screen, the same field count, order, positions and
  widths whatever the values are. `AScreensFieldListIsPositionallyStable` renders every
  screen with zeroed and with extreme values and compares.
- **Fixed width, space-padded** — `"ENABLED "` is padded to eight so it covers `"DISABLED"`
  exactly, and the detail readouts **clamp** (`A 99999`, `P999.99`, `T9999s`) so a big
  reading cannot outgrow its slot and shove its neighbours.

Both exist for the same reason: there is no read-modify-write on either panel, so a field is
erased only by being repainted, background and all. A field that changed width would leave
the tail of the old one on screen forever.

### [5] Diff, then paint only what moved

`Render()` compares field *i* against field *i* of the last frame and redraws only the
movers. A change of `screen` clears and repaints in full.

```cpp
const bool screenChanged = !_hasRendered || state.screen != _lastScreen;
if (screenChanged && !Clear()) { _hasRendered = false; return false; }

for (i...) {
    if (!screenChanged && ili9341_field_equal(&_frame.fields[i], &_lastFrame.fields[i]))
        continue;
    DrawField(_frame.fields[i]);
}
```

This is not an optimisation. A full ILI9341 frame is 153,600 bytes, ~197 ms at 6.25 MHz — a
repaint per sensor sample is impossible. One field is ~18 ms.

Details that matter:

- **Field equality includes colour.** The drive-mode field keeps its width but changes
  green↔red; a text-only comparison would leave it the wrong colour.
- **On failure, `_hasRendered = false`.** The panel no longer matches the shadow copy, so the
  diff would skip cells that were never actually painted. Forcing a full clear and repaint on
  the next pass is what makes a glitch self-correcting.
- The Lumex diffs **runs of changed cells** rather than fields, for the same reason in a
  different shape: the common in-session update moves five cells out of thirty-two.

### [6] The driver puts pixels on the wire

`DrawString` → `DrawChar` per cell → one address window + streamed RGB565. The panel knows
nothing about text; every glyph pixel is computed here. See [[ILI9341 driver]] for the wire
format, the glyph bitmaps and the timing.

---

## The contract between the two halves

`session_controller_to_display` carries a `display_screen_id` plus every value any screen
shows. **The FSM says what it is displaying; each driver decides how.**

There is deliberately **no common drawing API**, and that is the central design decision:

- an *intersection* API (`WriteText(row, column, string)`) caps the TFT at 16x2 — a 320x240
  panel pretending to be a character LCD;
- a *union* API (`DrawRect`, `SetFont`, `DrawBitmap`) is meaningless on the Lumex, which
  would no-op most of it.

Putting the seam at what the values *mean* leaves each panel free. Everything the TFT can do
that the Lumex cannot lives inside its `Render()` and never surfaces in the contract.

### `DisplayDriver` — a concept, not a base class

```cpp
template <typename T>
concept DisplayDriver = requires(T d, const session_controller_to_display& s) {
    { d.Init()    } -> std::same_as<bool>;
    { d.Clear()   } -> std::same_as<bool>;
    { d.Render(s) } -> std::same_as<bool>;

    // Extended session detail: only the TFT has room for these.
    { d.ShowAngularAcceleration(float{}) } -> std::same_as<bool>;
    { d.ShowPeakForce(float{})           } -> std::same_as<bool>;
    { d.ShowSessionElapsed(uint32_t{})   } -> std::same_as<bool>;
};
```

The panel is fixed at link time, so virtual dispatch would buy nothing and cost a vtable
pointer plus an indirect call per draw. The concept checks the same contract at compile time
and inlines through it. Each driver carries `static_assert(DisplayDriver<...>)`, so a
signature mismatch is an error **at the driver**, not a link failure later.

The three `Show*` methods are the one deliberate asymmetry. They are extra in-session
readouts that need room a 2x16 grid does not have, so `LumexLCD` implements them as one-line
no-ops that discard the argument:

```cpp
bool ShowPeakForce(float newtons) { (void)newtons; return true; }
```

Inline and empty — they emit no code at all in the Lumex build; they do not even appear in
its `-fstack-usage` output. They sit on the concept rather than only on `ILI9341Display` so
the shared task loop can call them without knowing which panel it drives, and so a driver
that quietly stopped implementing one is a compile error. Adding a fourth readout is one real
implementation and one `(void)` line.

---

## Choosing a panel

`Core/Inc/Config/debug.h`, **at most one** enabled:

```c
#define LUMEX_LCD_TASK_ENABLE   0
#define ILI9341_LCD_TASK_ENABLE 1
```

`DISPLAY_TASK_ENABLE` is derived from the pair and is what code outside the two drivers
should test. Both drivers are always compiled; `--gc-sections` drops the unused one.

**Neither enabled is legal**, and useful: the display task parks and nothing drives SPI1,
which is how the panel gets ruled in or out of a fault elsewhere on the board. It is not the
same as switching to the Lumex, which would change two variables at once. Verify the
isolation with `arm-none-eabi-nm` — with no panel, `HAL_SPI_Transmit` is not linked in at
all.

---

## Layout reference

```
Tasks/Display/
  DisplayDriver.hpp     the concept every panel satisfies + the shared task loop
  display_common.{h,c}  helpers neither panel owns
  Lumex/                the 16x2 character driver and its layout
  ILI9341/              the 320x240 TFT driver and its layout
```

Neither panel subdirectory includes the other. The only shared code is the two files at this
level:

- `display_rpm_digit_increment()` — the cursor position the message carries means the same
  step size on any panel.
- `display_format_fixed2()` — two-decimal formatting **without** `snprintf("%f")`. That call
  drags in newlib's floating-point formatter, several hundred bytes of stack in a task that
  has a kilobyte, and it is the one call in the path whose cost cannot be read off
  `-fstack-usage` output.

---

## Lumex rendering (`Lumex/`)

- `lumex_lcd_main()` → construct, `Init()` (8-bit / 2-line / 5x8 font, display on, clear),
  then `RunDisplayTask`.
- `lumex_render()` writes every one of the 32 cells; unset cells are spaces.
- `Render()` writes only the runs that differ from `_lastFrame`.
- A change of `screen` forces a physical `ClearDisplay()`. That reproduces the old behaviour
  exactly — every `Show*Screen` used to clear, and the one redraw that deliberately did not
  (a tick inside the RPM editor) is also the one that does not change screen id.
- `SendByte` toggles the data GPIO lines; enable-pin timing is gated by a hardware timer
  (`StartTimer`), microsecond-scale. Millisecond waits use `osDelay`, never `HAL_Delay` —
  this runs in a task, and spinning there burns CPU other tasks want.
- Known artifact: none. The force field used to strand two digits of its own label; fixed by
  making the row literal labels and units only.
- `ERROR_LUMEX_LCD_TIMER_START_FAILURE` → `task_error_circular_buffer`.

## ILI9341 rendering (`ILI9341/`)

- `ili9341_lcd_main()` constructs the driver as a **function-local static**: the display task
  runs on 1 KB and this object carries two frames of layout state, so it belongs in `.bss`.
  `-fno-threadsafe-statics` is set and the function runs once, so there is no guard variable.
- Session screen field order, which the index-wise diff depends on:

  | # | field | position | size |
  |---|---|---|---|
  | 0 | `SPEED` label | (12, 18) | 2 |
  | 1 | RPM value | (12, 40) | 5 |
  | 2 | `rpm` unit | (172, 64) | 2 |
  | 3 | `FORCE` label | (12, 100) | 2 |
  | 4 | force value | (12, 122) | 5 |
  | 5 | `N` unit | (200, 146) | 2 |
  | 6 | `A` angular acceleration | (12, 168) | 2 |
  | 7 | `P` peak force | (108, 168) | 2 |
  | 8 | `T` session elapsed | (216, 168) | 2 |
  | 9 | drive mode | (12, 196) | 3 |

- Everything is painted on `ILI9341_BLACK`; each field carries its own foreground.
- `ILI9341_DISPLAY_ROTATION` (`config.h`) says which way up the panel is fitted — a property
  of the enclosure, not of the driver.
- `ERROR_DISPLAY_INIT_FAILURE`, `ERROR_DISPLAY_SPI_TRANSMIT_FAILURE` →
  `task_error_circular_buffer`.

---

## Units

`session_controller_to_display.rpm` is **RPM**. The optical encoder measures rad/s and the
FSM converts once on the way in via `encoder_rpm()` ([[OpticalSensor]]), so a driver renders
the number it is given and no panel repeats the conversion. This was a real bug: the readout
was labelled "rpm" while showing rad/s, so 3000 RPM displayed as 314.

`angular_acceleration` is rad/s², `force` and `peak_force` are newtons, `bpm_duty_cycle` is a
0–1 fraction, `session_seconds` is seconds.

## Key constants

`SYSCFG_LCD_TASK_OSDELAY` (sysconfig) · `LUMEX_LCD_ROWS` / `LUMEX_LCD_COLUMNS` /
`ILI9341_DISPLAY_ROTATION` (config.h) · `ILI9341_MAX_FIELDS` / `ILI9341_FIELD_TEXT_MAX`
(ili9341_layout.h) · `ILI9341_MAX_TEXT_SIZE` (ILI9341_main.h)

## Related
[[ILI9341 driver]] · [[SessionController]] · [[OpticalSensor]] · [[MessagePassing]]
