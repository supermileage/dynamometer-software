---
module: ILI9341 driver
summary: SPI driver for the ILI9341 240x320 TFT — the wire protocol, the drawing model, and why it is written rather than vendored.
code:
  - Drivers/ILI9341/ILI9341.hpp
  - Drivers/ILI9341/ILI9341.cpp
  - Drivers/ILI9341/ILI9341_main.h
  - Drivers/ILI9341/ILI9341_font.h
  - Drivers/ILI9341/ILI9341_font.c
used_by: Display (ILI9341 variant)
related: [Display, Config]
---

# ILI9341 — SPI TFT driver

C++ driver for the ILI9341, written against the STM32 HAL. Same shape as the
[[ADS1115 driver]]: a plain class, no base class, no virtuals, HAL handles passed in.

**The one thing to understand before anything else: the panel has no idea what text is.**
The ILI9341 is a dumb framebuffer with a cursor. It knows no fonts, no characters, no
lines, no rectangles. It knows exactly one useful trick — you give it a rectangle and then
stream raw pixels into it. Everything above that, including every glyph, is computed here
and sent as pixels.

---

## 1. The wire

### Signals

Four-wire SPI plus a fifth line the ILI9341 adds, all on SPI1, which is the panel's own bus:

| signal | pin | direction | what it does |
|---|---|---|---|
| `ILI_SPI1_SCK`    | PG11 | out | clock |
| `ILI_SPI1_MOSI`   | PD7  | out | the only line that carries anything we send |
| `ILI_SPI1_MISO`   | PG9  | in  | wired, never read — the driver never reads back |
| `ILI_SPI1_LCD_CS` | PG10 | out | chip select, **active low**, driven by GPIO not SPI_NSS |
| `ILI_LCD_DC`      | PD5  | out | **data / command**: low = this byte is a command, high = data |
| `ILI_LCD_RST`     | PD6  | out | hardware reset, **active low**, idles high |

`ILI_LCD_DC` is the part that is not ordinary SPI. There are no addresses, no registers and
no headers on this bus: **the D/C pin is the entire framing mechanism.** A byte is a command
if D/C was low when it was clocked, and an argument or a pixel if D/C was high. That is why
`WriteCommand` and `WriteData` differ only in which way they set that pin.

### Bus settings (`main.c`, generated from the `.ioc`)

```
Mode        master, 2 lines (full duplex, though MISO is unused)
DataSize    8 bit
CLKPolarity low   ) SPI mode 0: idle low, sample on the rising edge
CLKPhase    1 edge)
FirstBit    MSB
NSS         soft   -- CS is a plain GPIO, so it can stay low across several transfers
Prescaler   32 on a 200 MHz SPI123 kernel clock -> 6.25 MHz SCK
```

`NSS_SOFT` matters more than it looks. Because CS is an ordinary GPIO, the driver can hold
it low across a command *and* the megabyte of pixels that follows, which is what makes a
streamed write possible at all.

### The message format

There is no packet structure. A transaction is nothing more than a CS window with D/C
toggling inside it:

```
CS  ‾‾‾\_______________________________________________________/‾‾‾
D/C     \__ 0 __/‾‾‾‾‾‾‾‾‾‾‾ 1 ‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾
MOSI     [opcode] [arg] [arg] ... [arg]
         1 byte   0..n bytes
```

In code that is exactly `SendCommand`:

```cpp
Select();                     // CS low
WriteCommand(opcode);         // D/C low,  1 byte
WriteData(args, length);      // D/C high, n bytes
Deselect();                   // CS high
```

### Worked example 1 — `FillRect(12, 40, 30, 40, ILI9341_WHITE)`

A 30x40 white block at (12, 40). `SetAddrWindow` computes the inclusive far corner
(x1 = 12+30-1 = 41, y1 = 40+40-1 = 79) and sends three commands, then the pixels:

```
CS low
  D/C=0  2A                     CASET, "set column range"
  D/C=1  00 0C 00 29            x0 = 0x000C (12), x1 = 0x0029 (41)   -- 16-bit, big-endian
  D/C=0  2B                     PASET, "set page (row) range"
  D/C=1  00 28 00 4F            y0 = 0x0028 (40), y1 = 0x004F (79)
  D/C=0  2C                     RAMWR, "everything after this is pixels"
  D/C=1  FF FF FF FF ... FF FF  30*40 = 1200 pixels, 2400 bytes
CS high
```

Note `SetAddrWindow` deliberately **leaves CS asserted** and returns. The caller streams
straight into the open `RAMWR` and calls `Deselect()` when it is done.

### Worked example 2 — one character

`DrawChar` at size 3 opens a window over the whole 18x24 cell and then streams it row by
row, so the *entire glyph is a single command sequence*:

```
CS low
  D/C=0  2A ; D/C=1  <x range, 18 wide>
  D/C=0  2B ; D/C=1  <y range, 24 tall>
  D/C=0  2C
  D/C=1  <36 bytes>   glyph row 0, repeated 3x down     ) 24 writes
  D/C=1  <36 bytes>                                     ) of 18 pixels
  ...                                                   ) = 864 bytes
CS high
```

### Pixel format

`PIXFMT` (0x3A) is set to `0x55` in the init table: **16 bits per pixel, RGB565**, sent
**high byte first**.

```
bit  15 14 13 12 11 10  9  8  7  6  5  4  3  2  1  0
      R  R  R  R  R  G  G  G  G  G  G  B  B  B  B  B
```

So `ILI9341_WHITE` = `0xFFFF` → `FF FF`, `ILI9341_RED` = `0xF800` → `F8 00`. The byte order
is why the scratch fill is written the way it is:

```cpp
ili9341_scratch[i * 2]     = (uint8_t)(colour >> 8);
ili9341_scratch[i * 2 + 1] = (uint8_t)colour;
```

The colour constants in `ILI9341_main.h` are already RGB565 literals; nothing converts from
24-bit RGB anywhere.

### The cursor, and why it is the whole design

After `RAMWR`, every pixel written advances an internal cursor left-to-right then
top-to-bottom **inside the window**, wrapping at the window's right edge, not the panel's.
That is the entire reason this driver is fast enough to use:

- set a window once, stream N pixels — **one** command sequence;
- set a window per pixel — **N** command sequences, each 11 bytes of overhead for 2 bytes of
  payload.

Hence the rule the whole driver is built on: **blit rectangles, never pixels.** There is no
`DrawPixel` in this class, on purpose.

---

## 2. Drawing model

### There is no read-modify-write

The driver never reads the panel back. MISO is wired but unused, so there is no way to ask
"what is currently at (x, y)". Everything drawn is therefore *opaque*: `DrawChar` paints the
glyph's foreground **and** its background, all 48 pixels of a 6x8 cell.

That single fact drives the layout rules one level up ([[Display]]): fields are fixed-width
and space-padded, because overwriting `"12345"` with `" 1235"` only erases the old digits if
those cells are repainted background and all.

### Glyphs

`ILI9341_font.c` is 1280 bytes of pure data: 256 glyphs x 5 bytes. **One byte per column**,
bit *n* of that byte being row *n* from the top:

```
'A' = 0x7C, 0x12, 0x11, 0x12, 0x7C

        . . # . .        bit0 of each of the 5 bytes
        . # . # .        bit1
        # . . . #        bit2
        # . . . #        bit3
        # # # # #        bit4
        # . . . #        bit5
        # . . . #        bit6
```

`ili9341_font_pixel(c, column, row)` is just `(font[c * 5 + column] >> row) & 1`.

The glyph is 5x7 drawn inside a **6x8 cell** — the sixth column and eighth row are the
inter-character and inter-line gap, and they are painted as background like everything else.

### `size` is replication, not a font

`size` scales by repeating pixels: at size 3 each font dot becomes a 3x3 block. It is *not*
a different typeface, so size 5 is not a nicer-looking font than size 1 — it is the same 35
dots, five times blockier. Cell geometry is `6 * size` by `8 * size`, and text advances by
exactly `6 * size` per character whatever the character is (a fixed advance: `'i'` and `'W'`
occupy the same width).

`ILI9341_MAX_TEXT_SIZE` (8) is the cap, and it is not arbitrary — it sizes the scratch
buffer:

```
ili9341_scratch = ILI9341_FONT_CELL_WIDTH (6) * ILI9341_MAX_TEXT_SIZE (8) * 2 bytes = 96 B
```

which is one glyph row at the largest allowed scale. `DrawChar` rejects a larger `size`
rather than overrun it.

### Clipping

Nothing here reflows, shrinks or wraps. Text that does not fit is **lost**:

- `DrawString` stops as soon as a cell would *start* past `Width()`;
- `SetAddrWindow` and `FillRect` clip a rectangle's overhang to the panel edge — clipping
  rather than rejecting, so a field near the edge loses its tail instead of vanishing, and
  an unclipped window is never handed to the controller (which would wrap it onto the next
  row).

Fitting is the layout's job, done up front with fixed positions and clamped values. See
[[Display]].

---

## 3. Bring-up sequence

`Init(rotation)` does three things in order.

**1. Hardware reset.** Active low, and generously timed — the controller needs far longer
after reset than its 10 us minimum pulse before it will accept commands:

```
RST high, 5 ms   ->   RST low, 20 ms   ->   RST high, 150 ms
```

**2. Walk the init table.** `ILI9341_INIT_COMMANDS` is a flat byte array in a
self-describing format:

```
opcode, count, arg0 .. arg(count-1),
opcode, count, ...
0x00                                  <- terminator
```

with one wrinkle: **if the high bit of `count` is set, wait 150 ms after that command.**
`count & 0x7F` is the real argument count. Only `SLPOUT` (exit sleep) and `DISPON` use it,
and both genuinely need the wait.

This table is the one part of the Adafruit library genuinely worth having. The gamma curves
(`GMCTRP1`/`GMCTRN1`, 15 bytes each) are tuned values, not derivations — you would not
reconstruct them from the datasheet in an afternoon.

**3. Apply the rotation** via `MADCTL` (0x36).

### Rotation and `MADCTL`

| index | constant | bits | panel |
|---|---|---|---|
| 0 | `ILI9341_ROTATION_PORTRAIT` | `MX \| BGR` = 0x48 | 240x320 |
| 1 | `ILI9341_ROTATION_LANDSCAPE` | `MV \| BGR` = 0x28 | 320x240 |
| 2 | `ILI9341_ROTATION_PORTRAIT_FLIP` | `MY \| BGR` = 0x88 | 240x320 |
| 3 | `ILI9341_ROTATION_LANDSCAPE_FLIP` | `MX \| MY \| MV \| BGR` = 0xE8 | 320x240 |

`MV` is the row/column exchange — that bit alone is what makes it landscape, and `Width()` /
`Height()` swap based on it. `MX`/`MY` mirror the axes, so 1 and 3 differ by exactly 0xC0: a
180-degree flip, same geometry, same colours.

`BGR` is set in all four because these modules wire the panel that way. **A correct image in
wrong colours is that bit and nothing else.**

Which rotation this board uses is `ILI9341_DISPLAY_ROTATION` in `config.h`, not a constant
here — which way up the panel is fitted is a property of the enclosure.

---

## 4. Performance

At the configured 6.25 MHz SCK, 16 bits per pixel:

| operation | pixels | bytes | time |
|---|---|---|---|
| full screen (320x240) | 76,800 | 153,600 | ~197 ms |
| one size-5 field, 6 chars (180x40) | 7,200 | 14,400 | ~18 ms |
| one size-3 character (18x24) | 432 | 864 | ~1.1 ms |

**A full repaint per sensor sample is impossible**, which is the whole reason the layer above
diffs and repaints only changed fields. That is not an optimisation; it is what makes the
panel usable.

### Blocking `HAL_SPI_Transmit`, not DMA

The display task is `osPriorityBelowNormal`, so a polling wait is preempted by anything that
matters and costs only idle time.

DMA would also be real work here rather than a flag: **DMA1/DMA2 cannot reach DTCM on the
STM32H7**, and `STM32H743XX_FLASH.ld` puts `.data`, `.bss`, the FreeRTOS heap and every task
stack there. A DMA transfer would need a scratch buffer in a new linker section in AXI SRAM
(0x24000000, currently completely unused) plus a completion semaphore to make yielding — not
spinning — the point. Worth doing if a framebuffer or a live graph ever streams full frames;
not before.

`ILI9341_SPI_TIMEOUT_MS` is 1000, far longer than any transfer here needs, because the HAL's
timeout is **wall clock** and keeps counting while the caller is preempted. A timeout tuned
to the transfer would fire on scheduling latency rather than a real bus fault.

### Where the buffers live

`ili9341_scratch` is a file-scope `static` in `.bss`, not a local. The display task runs on
1 KB of stack and there is exactly one panel, so a shared buffer is both cheaper and safer.

### `DelayMs` is injected

`Init` needs ~325 ms of waits. The constructor takes a `DelayMs` callback rather than calling
`HAL_Delay` directly, defaulting to `HAL_Delay` for bare-metal bring-up. The display task
passes `osDelay` instead, because under an RTOS `HAL_Delay` spins rather than yields — and
inside a FreeRTOS critical section it would never return at all, since HAL's tick comes from
a TIM at priority 15 which is masked there. This is also what keeps the driver free of
`cmsis_os2.h`.

---

## 5. Why this is not the Adafruit library

The obvious move is to submodule `Adafruit_ILI9341`. That would actually be **three**
submodules — it depends on `Adafruit-GFX-Library` (which ships `Adafruit_SPITFT`), which
depends on `Adafruit_BusIO` — and none of them would work here:

- **Vtables.** The chain is `Adafruit_ILI9341 : Adafruit_SPITFT : Adafruit_GFX : Print`.
  18 `virtual` in `Adafruit_GFX.h`, 2 more in `Adafruit_SPITFT.h`, plus Arduino's `Print`
  base. No build configuration removes them, and the firmware builds `-fno-rtti
  -fno-exceptions` precisely to avoid paying for that.
- **No STM32 branch to switch on.** `Adafruit_SPITFT.cpp` is 2621 lines of per-MCU `#ifdef`
  — 24 `__AVR` sites, 21 `digitalPinToPort`, 19 `digitalWrite`, 13 `portOutputRegister`,
  `SPIClass`/`SPISettings`. Porting means adding a whole new architecture arm to someone
  else's dispatch tree, which upstream will never merge, so the fork is permanent.
- **The payload is tiny.** What is genuinely ILI9341-specific is the init table, the
  address-window command and the MADCTL values — about 30 lines.

So: vendor the constants and the data, write the transport. Exactly what
`Drivers/ADS1115/README.md` describes for the force sensor's ADC.

**Vendored, with Adafruit's BSD notice kept** (`ILI9341_main.h`):
- the init/power/gamma table (`ILI9341_INIT_COMMANDS`);
- the command codes, MADCTL bits and RGB565 colour constants;
- `ILI9341_font.c` — 1280 bytes of glyph data whose only Arduino dependency was a `PROGMEM`
  attribute that is defined away on every non-AVR target.

---

## 6. API

| method | notes |
|---|---|
| `Init(rotation)` | reset, init table, rotation. Defaults to landscape |
| `SetRotation(r)` | `MADCTL` write; changes what `Width()`/`Height()` report |
| `InvertDisplay(b)` | `INVON` / `INVOFF` |
| `FillRect(x,y,w,h,c)` | clipped; off-panel is success-with-nothing-drawn, not an error |
| `FillScreen(c)` | `FillRect` over the whole panel |
| `DrawChar(x,y,c,fg,bg,size)` | one cell, opaque, one command sequence |
| `DrawString(x,y,text,len,fg,bg,size)` | fixed advance, clipped at the right edge. **Not NUL-aware** — callers pass fixed-width fields |
| `Width()` / `Height()` | follow the active rotation |

Every method returns `bool`: false means a HAL SPI call failed. Callers must not treat that
as fatal — see [[Display]] for why a failed write must never take the board down.

---

## 7. Bring-up order on new hardware

1. Reset pulse + `SLPOUT` + `FillScreen(WHITE)` → panel and backlight alive. A blank screen
   here is almost always CS or RST polarity (both idle **high**), or a backlight pin that is
   switched rather than strapped to 3V3.
2. Fill red / green / blue → SPI, D/C and colour order. Wrong colours with a correct image is
   the `MADCTL` BGR bit; garbage is usually `DataSize` not being 8-bit, or the clock too fast.
3. `DrawString` of a literal → font path.
4. Rotation → origin corner and orientation. Upside down is rotation 1 vs 3.

## Related
[[Display]] · [[Config]] · upstream: https://github.com/adafruit/Adafruit_ILI9341
