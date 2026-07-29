#ifndef DRIVERS_ILI9341_ILI9341_HPP_
#define DRIVERS_ILI9341_ILI9341_HPP_

// ILI9341 240x320 SPI TFT, driven through the STM32 HAL.
//
// Written against HAL_SPI_Transmit rather than ported from Adafruit_ILI9341. That library's
// transport lives in Adafruit_SPITFT.cpp -- 2600 lines of per-MCU #ifdef over Arduino's
// digitalWrite/SPIClass, with no STM32 branch -- and its class chain
// (Adafruit_ILI9341 : Adafruit_SPITFT : Adafruit_GFX : Print) carries twenty virtual
// functions this codebase deliberately does not pay for. What is actually panel-specific is
// the init table and the address-window command, which are vendored in ILI9341_main.h and
// below; see README.md.
//
// Same shape as Drivers/ADS1115: a plain class, no base, no virtuals, HAL handles passed in.

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "ILI9341_font.h"
#include "ILI9341_main.h"

#include "main.h"

class ILI9341
{
public:
    // How the driver waits out the panel's reset and power-on timings (5 + 20 + 150 + 150 ms).
    //
    // Injected rather than hardcoded. This is a driver, not a task, so it must not reach for
    // cmsis_os2.h -- that is what keeps it host-testable and reusable. But HAL_Delay is the
    // wrong call under an RTOS: it spins instead of yielding, so Init() burns ~325 ms of CPU
    // at the display task's priority, and it never returns at all inside a FreeRTOS critical
    // section, because HAL's tick comes from a TIM at TICK_INT_PRIORITY 15 which is masked
    // there. So the caller supplies the wait: the display task passes osDelay, and bare-metal
    // bring-up gets HAL_Delay by default.
    using DelayMs = void (*)(uint32_t milliseconds);

    ILI9341(SPI_HandleTypeDef* spi,
            GPIO_TypeDef* csPort,  uint16_t csPin,
            GPIO_TypeDef* dcPort,  uint16_t dcPin,
            GPIO_TypeDef* rstPort, uint16_t rstPin,
            DelayMs delay = HAL_Delay);
    ~ILI9341() = default;

    // Hardware reset pulse, then the vendored power/gamma sequence, then `rotation`. Leaves
    // the panel on with undefined framebuffer contents -- callers fill before showing.
    bool Init(uint8_t rotation = ILI9341_ROTATION_LANDSCAPE);

    bool SetRotation(uint8_t rotation);
    bool InvertDisplay(bool invert);

    bool FillRect(uint16_t x, uint16_t y, uint16_t w, uint16_t h, uint16_t colour);
    bool FillScreen(uint16_t colour);

    // One glyph in a (6*size x 8*size) cell, foreground on background. Both are painted, so
    // drawing over a cell erases what was there -- there is no read-modify-write on this bus.
    bool DrawChar(uint16_t x, uint16_t y, char c, uint16_t fg, uint16_t bg, uint8_t size);

    // `length` characters, advancing one cell each. Not NUL-aware: callers pass fixed-width
    // fields so that a shorter value overwrites the tail of a longer one.
    bool DrawString(uint16_t x, uint16_t y, const char* text, size_t length,
                    uint16_t fg, uint16_t bg, uint8_t size);

    // Follow the active rotation: 320x240 in landscape, 240x320 in portrait.
    uint16_t Width() const;
    uint16_t Height() const;

private:
    bool WriteCommand(uint8_t command);
    bool WriteData(const uint8_t* data, size_t length);
    bool SendCommand(uint8_t command, const uint8_t* data, size_t length);

    // Clips to the panel and returns false if nothing is left, so callers can skip the write
    // rather than send a malformed window the controller would interpret as a wrap.
    bool SetAddrWindow(uint16_t x, uint16_t y, uint16_t w, uint16_t h);

    void Select();
    void Deselect();

    SPI_HandleTypeDef* _spi;

    DelayMs _delay;

    GPIO_TypeDef* _csPort;
    GPIO_TypeDef* _dcPort;
    GPIO_TypeDef* _rstPort;

    uint16_t _csPin;
    uint16_t _dcPin;
    uint16_t _rstPin;

    uint8_t _rotation;
};

#endif /* DRIVERS_ILI9341_ILI9341_HPP_ */
