---
module: Display
summary: The display task — how a measured value reaches a panel, and why the seam sits where it does. Panel specifics live in the subdirectories.
code:
  - Core/Inc/Tasks/Display/DisplayDriver.hpp
  - Core/Inc/Tasks/Display/display_common.h
  - Core/Src/Tasks/Display/display_common.c
entry_point: lumex_lcd_main() / ili9341_lcd_main()
task_offset: TASK_OFFSET_DISPLAY
consumes: [session_controller_to_display (SessionController)]
produces: [task_error_circular_buffer]
related: [Lumex display, ILI9341 display, SessionController, MessagePassing]
---

# Display — the panel-independent half

Two panels are supported and at most one is compiled in: a Lumex 16x2 character LCD and an
ILI9341 320x240 TFT. Both read the same queue and the same message.

**This file is the part that is true of both.** How each one turns that message into something
on glass is in its own directory:

| | rendering | panel protocol |
|---|---|---|
| Lumex | [[Lumex display]] (`Lumex/README.md`) | [[Lumex panel driver]] (`Drivers/Lumex`) |
| ILI9341 | [[ILI9341 display]] (`ILI9341/README.md`) | [[ILI9341 driver]] (`Drivers/ILI9341`) |

---

## The path, end to end

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
 <panel> layout                                         [4] state -> what goes where
      |  a full frame, computed from the state alone
      v
 <panel> Render()                                       [5] diff, then paint what moved
      |
      v
 <panel> driver                                         [6] bytes on the wire
```

Stages [1] to [3] are shared and described below. [4] to [6] are per-panel — follow the
table above.

### [1] The SessionController posts only what moved

`UpdateMeasurementDisplay()` compares against `_prevForce` / `_prevAngularVelocity` and calls
the FSM only when a reading actually changes. First filter.

### [2] The FSM sends meaning, never drawing

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

The FSM formats nothing. It used to: the layout lived there as
`WriteText(row, column, "n:     0 rpm    ")` with hand-counted padding — a 16x2 character grid
baked into a state machine, which is exactly what made a second panel impossible.

### [3] The task loop takes only the newest message

`RunDisplayTask` (`DisplayDriver.hpp`) blocks on the queue, then **drains to the newest**
before drawing anything:

```cpp
while (osMessageQueueGet(queue, &state, 0, 0) == osOK);
```

Each message is the whole screen state, so anything behind the newest is already stale.
Rendering them in turn would paint values nobody will ever see.

It is `[[noreturn]]`, and that is load-bearing: **a FreeRTOS task function that returns lands
in `prvTaskExitError()`, which fails a `configASSERT`, disables interrupts and spins.** The
whole rig dies — buttons, brake and all — with no LED and no fault report. An early version
did `if (!Render(state)) return;`, and one failed SPI write took the entire dynamometer down.
A failed render is now recorded in the error buffer and the loop carries on.

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

The three `Show*` methods are the one deliberate asymmetry — extra in-session readouts that
need room a 2x16 grid does not have. They sit on the concept rather than only on
`ILI9341Display` so the shared task loop can call them without knowing which panel it drives,
and so a driver that quietly stopped implementing one is a compile error. Adding a fourth
readout is one real implementation and one `(void)` line. What each panel does with them is in
its own README.

### The rule both panels obey

**Neither panel supports read-modify-write.** Nothing can ask either one "what is currently
at this position", so a field is erased only by being *repainted*, background and all.

Every layout rule in both subdirectories descends from that one fact: fixed-width fields,
space padding, clamped values, stable positions. They are the same requirement expressed in a
character grid and in pixels.

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
isolation with `arm-none-eabi-nm` — with no panel, `HAL_SPI_Transmit` is not linked in at all.

---

## Layout reference

```
Tasks/Display/
  DisplayDriver.hpp     the concept every panel satisfies + the shared task loop
  display_common.{h,c}  helpers neither panel owns
  Lumex/                the 16x2 character panel's rendering    -> Lumex/README.md
  ILI9341/              the 320x240 TFT's rendering             -> ILI9341/README.md
```

with the panel protocols one level out, in `Drivers/Lumex` and `Drivers/ILI9341`. Each
subdirectory renders; each driver talks to hardware. Neither panel subdirectory includes the
other.

### The two shared helpers

- `display_rpm_digit_increment()` — the cursor position the message carries means the same
  step size on any panel.
- `display_format_fixed2()` — two-decimal formatting **without** `snprintf("%f")`. That call
  drags in newlib's floating-point formatter, several hundred bytes of stack in a task that
  has a kilobyte, and it is the one call in the path whose cost cannot be read off
  `-fstack-usage` output.

---

## Units

`session_controller_to_display.rpm` is **RPM**. The optical encoder measures rad/s and the FSM
converts once on the way in via `encoder_rpm()` ([[OpticalSensor]]), so a driver renders the
number it is given and no panel repeats the conversion. This was a real bug: the readout was
labelled "rpm" while showing rad/s, so 3000 RPM displayed as 314.

`angular_acceleration` is rad/s², `force` and `peak_force` are newtons, `bpm_duty_cycle` is a
0–1 fraction, `session_seconds` is seconds.

## Key constants

`SYSCFG_LCD_TASK_OSDELAY` (sysconfig) — the delay at the end of each pass of the task loop.
Panel-specific constants are listed in the panel READMEs.

## Related
[[Lumex display]] · [[ILI9341 display]] · [[Lumex panel driver]] · [[ILI9341 driver]] ·
[[SessionController]] · [[OpticalSensor]] · [[MessagePassing]]
