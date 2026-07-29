#ifndef DRIVERS_LUMEX_LUMEXPANEL_HPP_
#define DRIVERS_LUMEX_LUMEXPANEL_HPP_

// Lumex 16x2 character LCD (HD44780 controller), bit-banged over eight data lines plus RS and
// E. The counterpart to Drivers/ILI9341: the panel's own protocol and nothing above it. What
// to put on the screen, and which cells changed since last time, belong to
// Tasks/Display/Lumex/LumexLCD.
//
// Write-only. This board wires no R/W pin, so the busy flag can never be read and every wait
// is a fixed delay -- see LumexPanel_main.h for which and why.
//
// Same shape as the ILI9341 driver: a plain class, no base, no virtuals, board wiring and
// timing handed in rather than reached for.

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "LumexPanel_main.h"

#include "main.h"

class LumexPanel
{
public:
    // One GPIO. The eight data lines are not required to share a port, and on this board they
    // happen to but nothing here relies on it.
    struct Pin
    {
        GPIO_TypeDef* port;
        uint16_t      pin;
    };

    // Board wiring, D0 first. Passing this as one struct keeps a ten-argument constructor from
    // existing.
    struct Pins
    {
        Pin data[8];
        Pin rs;   // low = instruction, high = character data
        Pin en;   // the strobe; the panel latches on its falling edge
    };

    // Two waits, because they are two different problems and one mechanism cannot do both.
    //
    // DelayUs is the ~40 us between bytes. osDelay cannot express it -- at a 1 kHz tick its
    // floor is 1 ms, which would stretch every byte 25x and a full repaint from ~2.6 ms to
    // ~64 ms -- so this one busy-waits, and 40 us of spinning is not worth an RTOS call.
    //
    // DelayMs is the millisecond-scale waits: power-on and the clear instruction. Those are
    // long enough to be worth yielding for, so the task passes osDelay, exactly as it passes
    // osDelay to the ILI9341 driver.
    using DelayUs = void (*)(uint32_t microseconds);
    using DelayMs = void (*)(uint32_t milliseconds);

    LumexPanel(const Pins& pins, DelayUs delayUs, DelayMs delayMs);
    ~LumexPanel() = default;

    // Power-on reset sequence: 8-bit / 2-line / 5x8, display on, cursor and blink off, clear.
    bool Init();

    bool ClearDisplay();
    bool SetCursor(uint8_t row, uint8_t column);

    // Writes `size` characters from `column` along `row`, clipping at the last column rather
    // than wrapping onto the other row -- an overlong field then fails visibly in its own cells
    // instead of corrupting its neighbour.
    bool DisplayString(uint8_t row, uint8_t column, const char* string, size_t size);
    bool DisplayChar(uint8_t row, uint8_t column, uint8_t character);

    bool ToggleBlink(bool enable);

    // Raw instruction / character writes, public because the instruction set is the driver's
    // whole surface and a caller may legitimately want one this class does not wrap.
    bool WriteCommand(uint8_t command);
    bool WriteData(uint8_t data);

private:
    // Puts a byte on the data lines and strobes E. Everything above goes through here.
    bool SendByte(uint8_t byte);

    void Write(const Pin& pin, GPIO_PinState state);

    Pins    _pins;
    DelayUs _delayUs;
    DelayMs _delayMs;
};

#endif /* DRIVERS_LUMEX_LUMEXPANEL_HPP_ */
