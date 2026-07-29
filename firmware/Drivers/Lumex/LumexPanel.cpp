#include "LumexPanel.hpp"

#include "Config/config.h"


LumexPanel::LumexPanel(const Pins& pins, DelayUs delayUs, DelayMs delayMs) :
    _pins(pins),
    _delayUs(delayUs),
    _delayMs(delayMs)
{}


// ---------------------------------------------------------------------------- transport

void LumexPanel::Write(const Pin& pin, GPIO_PinState state)
{
    HAL_GPIO_WritePin(pin.port, pin.pin, state);
}

// The whole wire protocol, in one function.
//
//   1. put the byte on D0..D7
//   2. raise E
//   3. hold it for LUMEX_ENABLE_PULSE_US
//   4. drop E -- the panel latches on this edge
//
// RS is set by the caller and must already be stable, which it is: WriteCommand and WriteData
// set it before calling here, and the eight GPIO writes below take longer than the controller's
// setup time on their own.
//
// The hold doubles as the instruction-execution wait, which is why the next byte can follow
// immediately with no further delay.
bool LumexPanel::SendByte(uint8_t byte)
{
    for (uint8_t bit = 0; bit < 8; bit++)
    {
        Write(_pins.data[bit], static_cast<GPIO_PinState>((byte >> bit) & 0x01u));
    }

    Write(_pins.en, GPIO_PIN_SET);
    _delayUs(LUMEX_ENABLE_PULSE_US);
    Write(_pins.en, GPIO_PIN_RESET);

    return true;
}

bool LumexPanel::WriteCommand(uint8_t command)
{
    Write(_pins.rs, GPIO_PIN_RESET);   // RS low: this byte is an instruction

    return SendByte(command);
}

bool LumexPanel::WriteData(uint8_t data)
{
    Write(_pins.rs, GPIO_PIN_SET);     // RS high: this byte is a character

    return SendByte(data);
}


// ---------------------------------------------------------------------------- setup

bool LumexPanel::Init()
{
    Write(_pins.en, GPIO_PIN_RESET);

    // The controller ignores everything until its internal power-on reset finishes.
    _delayMs(LUMEX_POWER_ON_DELAY_MS);

    // Function set, three times. This is the documented way out of an unknown state: the
    // controller may come up in 4-bit mode -- after a warm reset that did not cycle its power,
    // say -- where a single 8-bit function set is read as half of a 4-bit pair. Repeating it
    // lands the part in 8-bit mode from any starting state.
    const uint8_t functionSet =
        LUMEX_CMD_FUNCTION_SET | LUMEX_FUNCTION_8BIT | LUMEX_FUNCTION_2LINE;

    for (uint8_t attempt = 0; attempt < 3; attempt++)
    {
        if (!WriteCommand(functionSet))
        {
            return false;
        }

        _delayMs(LUMEX_SETTLE_DELAY_MS);
    }

    if (!WriteCommand(LUMEX_CMD_DISPLAY_CONTROL | LUMEX_DISPLAY_ON))
    {
        return false;
    }

    _delayMs(LUMEX_SETTLE_DELAY_MS);

    return ClearDisplay();
}


// ---------------------------------------------------------------------------- drawing

bool LumexPanel::ClearDisplay()
{
    if (!WriteCommand(LUMEX_CMD_CLEAR))
    {
        return false;
    }

    // One of the two instructions the enable pulse's 40 us does not cover.
    _delayMs(LUMEX_CLEAR_DELAY_MS);

    return true;
}

// Moves the cursor. The two rows are not contiguous in DDRAM -- row 1 starts at 0x40 -- so the
// address is a base plus the column, not a linear offset.
bool LumexPanel::SetCursor(uint8_t row, uint8_t column)
{
    const uint8_t base = (row == 0) ? LUMEX_DDRAM_ROW0_BASE : LUMEX_DDRAM_ROW1_BASE;

    return WriteCommand(LUMEX_CMD_SET_DDRAM_ADDR | (uint8_t)(base + column));
}

bool LumexPanel::DisplayChar(uint8_t row, uint8_t column, uint8_t character)
{
    return SetCursor(row, column) && WriteData(character);
}

bool LumexPanel::DisplayString(uint8_t row, uint8_t column, const char* string, size_t size)
{
    assert_param(row < LUMEX_LCD_ROWS);

    for (size_t i = 0; i < size; i++)
    {
        // Clamp instead of wrapping: drop any characters past the last column so an overflow
        // fails visibly in one cell rather than corrupting the other row.
        if (column >= LUMEX_LCD_COLUMNS)
        {
            break;
        }

        // The cursor auto-increments, so a run could be written with one SetCursor and then
        // characters. It is set per character anyway: this costs one extra byte per cell and
        // removes any dependence on the entry mode the controller happens to be in.
        if (!DisplayChar(row, column, (uint8_t)string[i]))
        {
            return false;
        }

        column++;
    }

    return true;
}

bool LumexPanel::ToggleBlink(bool enable)
{
    const uint8_t control = LUMEX_CMD_DISPLAY_CONTROL | LUMEX_DISPLAY_ON
                            | (enable ? (LUMEX_CURSOR_ON | LUMEX_BLINK_ON) : 0u);

    return WriteCommand(control);
}
