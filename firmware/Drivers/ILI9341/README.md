---
module: ILI9341 driver
summary: SPI driver for the ILI9341 240x320 TFT, used by the ILI9341 display task.
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

## Why this is not the Adafruit library

The obvious move is to submodule `Adafruit_ILI9341`. That would actually be **three**
submodules — it depends on `Adafruit-GFX-Library` (which ships `Adafruit_SPITFT`), which
depends on `Adafruit_BusIO` — and none of them would work here:

- **Vtables.** The chain is `Adafruit_ILI9341 : Adafruit_SPITFT : Adafruit_GFX : Print`.
  18 `virtual` in `Adafruit_GFX.h`, 2 more in `Adafruit_SPITFT.h`, plus Arduino's `Print`
  base. No build configuration removes them, and the firmware builds `-fno-rtti
  -fno-exceptions` precisely to avoid paying for that.
- **No STM32 branch to switch on.** `Adafruit_SPITFT.cpp` is 2621 lines of per-MCU
  `#ifdef` — 24 `__AVR` sites, 21 `digitalPinToPort`, 19 `digitalWrite`, 13
  `portOutputRegister`, `SPIClass`/`SPISettings`. Porting means adding a whole new
  architecture arm to someone else's dispatch tree, which upstream will never merge, so
  the fork is permanent.
- **The payload is tiny.** What is genuinely ILI9341-specific is the init table, the
  address-window command, and the MADCTL rotation values — about 30 lines.

So: vendor the constants and the data, write the transport. Exactly what
`Drivers/ADS1115/README.md` describes for the force sensor's ADC.

**Vendored, with Adafruit's BSD notice kept** (`ILI9341_main.h`):
- the init/power/gamma command table (`ILI9341_INIT_COMMANDS` in `ILI9341.cpp`) — tuned
  values, not derivations, and the one thing worth taking;
- the command codes, MADCTL bits and RGB565 colour constants;
- `ILI9341_font.c`, the classic 5x7 GFX font: 256 glyphs x 5 column-bytes = 1280 bytes of
  pure data. Its only Arduino dependency was a `PROGMEM` attribute that is defined away on
  every non-AVR target.

## Wiring
SPI1 is the display's own bus. `ILI_SPI1_MOSI` (PD7), `_MISO` (PG9), `_SCK` (PG11),
`ILI_SPI1_LCD_CS` (PG10), `ILI_LCD_DC` (PD5), `ILI_LCD_RST` (PD6) — all in `main.h`, all
owned by the `.ioc`.

SPI1 runs 8-bit at `SPI_BAUDRATEPRESCALER_16`: SPI123 is clocked at 200 MHz, so that is a
12.5 MHz SCK. The datasheet allows about 10 MHz for writes and real modules take ~40 MHz,
so prescaler 8 (25 MHz) is the next thing to try once the panel is proven.

## Key methods
- `Init(rotation)` — reset pulse, walk the init table, apply the rotation. Defaults to
  landscape (320x240).
- `FillRect` / `FillScreen`, `DrawChar`, `DrawString`, `SetRotation`, `InvertDisplay`.
- `Width()` / `Height()` follow the active rotation.

## Performance notes
- **Blocking `HAL_SPI_Transmit`, not DMA.** The display task is `osPriorityBelowNormal`, so
  a polling wait is preempted by anything that matters and costs only idle time. DMA would
  also be real work here rather than a flag: DMA1/DMA2 cannot reach DTCM on the STM32H7, and
  `STM32H743XX_FLASH.ld` puts `.data`, `.bss`, the FreeRTOS heap and every task stack there —
  so a transfer would need a scratch buffer in a new linker section in AXI SRAM. Worth doing
  if a framebuffer or live graph ever streams full frames; not before.
- **Rectangles, never pixels.** A full frame is 320x240x16bpp = 153,600 bytes, ~98 ms at
  12.5 MHz — far too slow per sensor sample, which is why the display task repaints only
  changed fields. `DrawChar` sets one address window per cell and streams the rows into the
  open `RAMWR`; drawn pixel by pixel the same cell would be hundreds of command sequences.
- Pixel scratch lives in `.bss`, not on the caller's stack: the display task's stack is
  1 KB and there is exactly one panel.

## Bring-up order
1. Reset pulse + `SLPOUT` + `FillScreen(WHITE)` → panel and backlight alive.
2. Fill red / green / blue → SPI, D/C and colour order. A blank screen is usually CS or
   reset polarity; a correct image in wrong colours is the `MADCTL` BGR bit.
3. `DrawString` of a literal → font path.
4. Landscape rotation → 320x240 origin and orientation.

## Related
[[Display]] · [[Config]] · upstream: https://github.com/adafruit/Adafruit_ILI9341
