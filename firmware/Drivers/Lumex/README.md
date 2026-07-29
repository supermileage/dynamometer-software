---
module: Lumex panel driver
summary: Driver for the Lumex 16x2 character LCD (HD44780) — the instruction set, the bit-banged wire protocol, and the timing.
code:
  - Drivers/Lumex/LumexPanel.hpp
  - Drivers/Lumex/LumexPanel.cpp
  - Drivers/Lumex/LumexPanel_main.h
used_by: Display (Lumex variant)
related: [Display, ILI9341 driver, Config]
---

# Lumex — HD44780 character LCD driver

Driver for the Lumex 16x2 character LCD, bit-banged over GPIO. The counterpart to
[[ILI9341 driver]] and deliberately the same shape: a plain class, no base, no virtuals,
board wiring and timing handed in rather than reached for.

**The contrast with the ILI9341 is the point of having both.** That panel is a framebuffer —
you send pixels and it knows nothing about text. This one is the opposite: it contains a
character generator and 80 bytes of display RAM, so you send **`'A'`** and it draws an A.
There is no way to address a pixel at all.

| | Lumex (HD44780) | ILI9341 |
|---|---|---|
| bus | 8 parallel data lines + RS + E | SPI + D/C |
| you send | characters and instructions | raw RGB565 pixels |
| fonts | in the panel's ROM | in our flash (`ILI9341_font.c`) |
| addressable unit | a character cell (32 of them) | a pixel (76,800 of them) |
| full repaint | ~2.6 ms | ~197 ms |

---

## 1. The wire

### Signals

| signal | pins | what it does |
|---|---|---|
| `LUMEX_LCD_D0..D7` | PA0–PA7 | the byte being written |
| `LUMEX_LCD_RS` | PC5 | **register select**: low = instruction, high = character |
| `LUMEX_LCD_EN` | PC4 | **enable strobe**: the panel latches on its *falling* edge |
| R/W | *not wired* | tied low on the board — see below |

**There is no R/W pin on this board.** The panel is write-only, which means the busy flag can
never be read, which means every wait has to be a fixed delay long enough for the worst case.
That single fact explains all the timing constants in `LumexPanel_main.h`.

`RS` is the entire framing mechanism, exactly as `D/C` is on the ILI9341: a byte is an
instruction or a character purely because of what `RS` was when `E` fell.

### The write cycle

Every byte, without exception, goes through `SendByte`:

```
D0..D7  ──< byte >────────────────────────
RS      ──< 0 = instruction / 1 = data >──     (set by the caller, already stable)
E       _________/‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾\_________
                 |<-- 40 us -->|   ^
                                   latched here
```

```cpp
for (bit = 0; bit < 8; bit++) Write(data[bit], (byte >> bit) & 1);
Write(en, HIGH);
_delayUs(LUMEX_ENABLE_PULSE_US);   // 40 us
Write(en, LOW);                    // panel latches on this edge
```

Data setup time needs no explicit wait: the eight `HAL_GPIO_WritePin` calls take longer on
their own than the controller requires.

### Why the pulse is 40 µs and not 450 ns

The datasheet's minimum enable-high time is ~450 ns. The 40 µs here is not that — **it is the
instruction-execution wait wearing the pulse's clothes.** The controller needs ~37 µs to retire
an ordinary instruction, and with no busy flag to poll there is nowhere else to put that wait.
Holding `E` high for 40 µs covers both, which is why the next byte can follow immediately with
no further delay.

The two exceptions are `CLEAR` and `HOME`, which take ~1.52 ms. `ClearDisplay` waits
`LUMEX_CLEAR_DELAY_MS` (20 ms, generous) after them.

**If the panel is ever intermittent, raise this first.** Dropped or garbled characters, worse
when the board is warm, is what too short a hold looks like. When TIM13 timed this pulse it
counted at 500 kHz — 200 MHz APB1 timer clock over a prescaler of 400 — so the `StartTimer(40)`
in that code meant 41 ticks of 2 µs and the wire saw **~82 µs**. The `40` was ticks wearing the
units of microseconds. 40 µs is what the datasheet asks for and what that code always appeared
to be doing, but it is half of what the board actually ran on for years, and the ~37 µs it has
to cover moves 20–30% with the controller's internal RC oscillator. With no R/W pin there is no
busy flag to ask whether it was long enough.

---

## 2. The instruction set — what each command does

The HD44780 has eight instructions, told apart by **the position of the highest set bit**.
Everything below that bit is options, which is why `LumexPanel_main.h` defines them as pieces
to OR together rather than as magic numbers.

| code | instruction | what it does |
|---|---|---|
| `0x01` | `CLEAR` | blanks DDRAM to spaces, cursor home. **Slow (~1.5 ms)** |
| `0x02` | `HOME` | cursor to 0,0, contents untouched. Also slow |
| `0x04` | `ENTRY_MODE` | which way the cursor moves after a character, and whether the display scrolls |
| `0x08` | `DISPLAY_CONTROL` | display / cursor / blink on or off |
| `0x10` | `CURSOR_SHIFT` | nudge cursor or display without writing |
| `0x20` | `FUNCTION_SET` | bus width, line count, font |
| `0x40` | `SET_CGRAM_ADDR` | point at user-defined glyph RAM (unused here) |
| `0x80` | `SET_DDRAM_ADDR` | **point at a screen cell — this is how the cursor moves** |

### Option bits

```
ENTRY_MODE       | 0x02 INCREMENT        advance cursor after each character
                 | 0x01 SHIFT_DISPLAY    scroll the display instead of the cursor

DISPLAY_CONTROL  | 0x04 DISPLAY_ON
                 | 0x02 CURSOR_ON        the underline
                 | 0x01 BLINK_ON         the blinking block

FUNCTION_SET     | 0x10 8BIT             all eight data lines wired (this board)
                 | 0x08 2LINE
                 | 0x04 5X10_FONT        clear for the usual 5x8
```

### The four codes this driver actually sends

| built from | code | meaning |
|---|---|---|
| `FUNCTION_SET \| 8BIT \| 2LINE` | `0x38` | 8-bit bus, two lines, 5x8 font |
| `DISPLAY_CONTROL \| DISPLAY_ON` | `0x0C` | display on, cursor off, blink off |
| `DISPLAY_CONTROL \| DISPLAY_ON \| CURSOR_ON \| BLINK_ON` | `0x0F` | `ToggleBlink(true)` |
| `CLEAR` | `0x01` | blank the screen |
| `SET_DDRAM_ADDR \| base \| column` | `0x80`… | every cursor move |

### DDRAM addressing — the one real trap

**The two rows are not contiguous.** Row 0 starts at `0x00` and row 1 at `0x40`, with the
addresses in between unused on a 16-column part. So a cell is not a linear offset:

```
row 0, column 0  -> 0x80 | 0x00 | 0  = 0x80
row 0, column 15 -> 0x80 | 0x00 | 15 = 0x8F
row 1, column 0  -> 0x80 | 0x40 | 0  = 0xC0     <- not 0x90
row 1, column 15 -> 0x80 | 0x40 | 15 = 0xCF
```

That is all `SetCursor` does. Writing past column 15 does not wrap onto row 1 — it walks into
the unused gap and the characters vanish, which is why `DisplayString` clips at
`LUMEX_LCD_COLUMNS` instead.

### Worked example — putting `Hi` at row 1, column 3

```
RS=0  0xC3          SET_DDRAM_ADDR | 0x40 | 3        (E pulse, 40 us)
RS=1  0x48  'H'                                      (E pulse, 40 us)
RS=0  0xC4          SET_DDRAM_ADDR | 0x40 | 4        (E pulse, 40 us)
RS=1  0x69  'i'                                      (E pulse, 40 us)
```

Eight GPIO writes and one strobe per line above; ~160 µs for the pair.

The cursor auto-increments, so `SetCursor` could be sent once and the characters streamed
after it. `DisplayString` re-addresses every cell anyway: it costs one extra byte per
character and removes any dependence on whatever entry mode the controller happens to be in.

---

## 3. Power-on

`Init()` runs the documented reset sequence:

```
E low, wait 40 ms            controller ignores everything until its own reset finishes
FUNCTION_SET (0x38), 5 ms  ) three times
FUNCTION_SET (0x38), 5 ms  )
FUNCTION_SET (0x38), 5 ms  )
DISPLAY_CONTROL (0x0C), 5 ms
CLEAR (0x01), 20 ms
```

**Why `FUNCTION_SET` three times** — this is not superstition. The controller may come up in
4-bit mode, for instance after a warm reset that never cycled its power. In 4-bit mode a single
8-bit write is read as *half* of a 4-bit pair, so one function set cannot be trusted to land.
Repeating it reaches 8-bit mode from any starting state.

---

## 4. Timing

At 40 µs per byte, and two bytes per character cell (address + character):

| operation | bytes | time |
|---|---|---|
| one character cell | 2 | ~80 µs |
| one 5-cell field (e.g. the RPM readout) | 10 | ~400 µs |
| full 32-cell repaint | 64 | ~2.6 ms |
| `CLEAR` | 1 | ~20 ms |

Two orders of magnitude faster to repaint than the ILI9341, because there are 32 cells rather
than 76,800 pixels. The layer above still diffs and writes only changed runs — see
[[Display]] — but here that is an economy rather than a necessity.

---

## 5. The two delays, and why there is no timer

`LumexPanel` takes **two** callbacks, because the waits are two different problems:

```cpp
using DelayUs = void (*)(uint32_t microseconds);   // the 40 us enable pulse
using DelayMs = void (*)(uint32_t milliseconds);   // power-on, CLEAR
```

`DelayMs` is `osDelay` — yields, exactly as the ILI9341 driver's injected delay does.

`DelayUs` **cannot** be `osDelay`: at `configTICK_RATE_HZ` 1000 its floor is 1 ms, which would
stretch every byte 25x and a full repaint from ~2.6 ms to ~64 ms. So it busy-waits on the
free-running microsecond timestamp counter — the same one every sensor sample is stamped from.

**This used to be TIM13**, with an NVIC line, an ISR, and a `volatile bool` the task spun on:

```cpp
StartTimer(40);
while (!timerCallbackFlag);   // spun for the full 40 us anyway
```

The timer's only job was to drop `E` and set the flag the task was **already** spinning on, so
it cost a whole peripheral and saved no CPU. Spinning 40 µs directly is the same behaviour with
none of the machinery, and TIM13 is now free for something else.

The task's `PanelDelayUs` is bounded as well as timed. `get_timestamp()` reads a counter that
`SessionController::Init` starts, and `SESSION_CONTROLLER_TASK_ENABLE 0` is a legal
configuration — with the counter frozen, a purely time-based loop would never exit and would
wedge the display task. `LumexLCD::Init` starts the counter itself for that reason, and the
iteration bound is what turns a failure there into a mistimed panel rather than a hung task.

---

## 6. API

| method | notes |
|---|---|
| `Init()` | power-on reset sequence; leaves the display on and cleared |
| `ClearDisplay()` | `CLEAR` + the 20 ms wait |
| `SetCursor(row, col)` | `SET_DDRAM_ADDR` with the row base folded in |
| `DisplayChar(row, col, c)` | address then character |
| `DisplayString(row, col, s, n)` | `n` characters, clipped at the last column. **Not NUL-aware** — callers pass fixed-width fields |
| `ToggleBlink(on)` | `DISPLAY_CONTROL` with the cursor and blink bits |
| `WriteCommand(b)` / `WriteData(b)` | raw instruction / character, public because the instruction set is this driver's whole surface |

Every method returns `bool`. `SendByte` cannot currently fail — there is nothing to fail
against on a write-only bus with no busy flag — but the signatures keep the shape the ILI9341
driver has, and a caller must not treat `false` as fatal. See [[Display]] for why a failed
write must never take the board down.

## Related
[[Display]] · [[ILI9341 driver]] · [[Config]]
