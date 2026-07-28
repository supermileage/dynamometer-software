#include "ILI9341.hpp"

#include <string.h>

// The controller's power-on sequence, transcribed from Adafruit_ILI9341.cpp's initcmd[].
// Format: command, argument count, arguments... A count with the high bit set means "and then
// delay 150 ms", which is what SLPOUT and DISPON need. Terminated by a zero command.
//
// This table is the one genuinely panel-specific thing the Adafruit library knows that a
// datasheet reading would take a long time to reproduce -- the gamma curves in particular are
// tuned values, not derivations.
static const uint8_t ILI9341_INIT_COMMANDS[] = {
    0xEF, 3, 0x03, 0x80, 0x02,
    0xCF, 3, 0x00, 0xC1, 0x30,
    0xED, 4, 0x64, 0x03, 0x12, 0x81,
    0xE8, 3, 0x85, 0x00, 0x78,
    0xCB, 5, 0x39, 0x2C, 0x00, 0x34, 0x02,
    0xF7, 1, 0x20,
    0xEA, 2, 0x00, 0x00,
    ILI9341_PWCTR1,   1, 0x23,               // Power control VRH[5:0]
    ILI9341_PWCTR2,   1, 0x10,               // Power control SAP[2:0];BT[3:0]
    ILI9341_VMCTR1,   2, 0x3E, 0x28,         // VCOM control
    ILI9341_VMCTR2,   1, 0x86,               // VCOM control 2
    ILI9341_MADCTL,   1, 0x48,               // Memory access control (SetRotation overrides)
    ILI9341_VSCRSADD, 1, 0x00,               // Vertical scroll zero
    ILI9341_PIXFMT,   1, 0x55,               // 16 bits/pixel, RGB565
    ILI9341_FRMCTR1,  2, 0x00, 0x18,
    ILI9341_DFUNCTR,  3, 0x08, 0x82, 0x27,   // Display function control
    0xF2, 1, 0x00,                           // 3Gamma function disable
    ILI9341_GAMMASET, 1, 0x01,               // Gamma curve selected
    ILI9341_GMCTRP1, 15, 0x0F, 0x31, 0x2B, 0x0C, 0x0E, 0x08,
                         0x4E, 0xF1, 0x37, 0x07, 0x10, 0x03, 0x0E, 0x09, 0x00,
    ILI9341_GMCTRN1, 15, 0x00, 0x0E, 0x14, 0x03, 0x11, 0x07,
                         0x31, 0xC1, 0x48, 0x08, 0x0F, 0x0C, 0x31, 0x36, 0x0F,
    ILI9341_SLPOUT, 0x80,                    // Exit sleep, then wait
    ILI9341_DISPON, 0x80,                    // Display on, then wait
    0x00                                     // End of list
};

// MADCTL value per rotation index. MV is the row/column exchange that makes it landscape; BGR
// is set because these modules wire the panel that way -- a correct image in wrong colours is
// this bit.
static const uint8_t ILI9341_ROTATION_MADCTL[4] = {
    ILI9341_MADCTL_MX | ILI9341_MADCTL_BGR,
    ILI9341_MADCTL_MV | ILI9341_MADCTL_BGR,
    ILI9341_MADCTL_MY | ILI9341_MADCTL_BGR,
    ILI9341_MADCTL_MX | ILI9341_MADCTL_MY | ILI9341_MADCTL_MV | ILI9341_MADCTL_BGR,
};

// Pixel scratch, deliberately in .bss rather than on the caller's stack: the display task runs
// on a small FreeRTOS stack and there is exactly one panel, so a shared buffer is both cheaper
// and safer than a local. Sized to one glyph row at the largest text scale, which is also a
// convenient chunk for filling rectangles.
#define ILI9341_SCRATCH_PIXELS (ILI9341_FONT_CELL_WIDTH * ILI9341_MAX_TEXT_SIZE)
static uint8_t ili9341_scratch[ILI9341_SCRATCH_PIXELS * 2];

// How long HAL_SPI_Transmit may block.
//
// Deliberately far longer than any transfer here needs -- the largest is 96 bytes, ~61 us at
// 12.5 MHz. The timeout is wall-clock, and it keeps counting while the caller is preempted:
// this runs in the lowest-priority task on the board, so a burst of sensor, PID and USB work
// during session start can stall it for a long time between HAL's polls. A timeout tuned to
// the transfer would fire on scheduling latency rather than on a real bus fault, which is a
// display glitch reported as hardware failure.
#define ILI9341_SPI_TIMEOUT_MS 1000


ILI9341::ILI9341(SPI_HandleTypeDef* spi,
                 GPIO_TypeDef* csPort,  uint16_t csPin,
                 GPIO_TypeDef* dcPort,  uint16_t dcPin,
                 GPIO_TypeDef* rstPort, uint16_t rstPin,
                 DelayMs delay) :
    _spi(spi),
    _delay(delay != nullptr ? delay : HAL_Delay),
    _csPort(csPort), _dcPort(dcPort), _rstPort(rstPort),
    _csPin(csPin), _dcPin(dcPin), _rstPin(rstPin),
    _rotation(ILI9341_ROTATION_LANDSCAPE)
{}


// ---------------------------------------------------------------------------- transport

void ILI9341::Select()
{
    HAL_GPIO_WritePin(_csPort, _csPin, GPIO_PIN_RESET);
}

void ILI9341::Deselect()
{
    HAL_GPIO_WritePin(_csPort, _csPin, GPIO_PIN_SET);
}

bool ILI9341::WriteCommand(uint8_t command)
{
    HAL_GPIO_WritePin(_dcPort, _dcPin, GPIO_PIN_RESET);

    return HAL_SPI_Transmit(_spi, &command, 1, ILI9341_SPI_TIMEOUT_MS) == HAL_OK;
}

bool ILI9341::WriteData(const uint8_t* data, size_t length)
{
    if (length == 0)
    {
        return true;
    }

    HAL_GPIO_WritePin(_dcPort, _dcPin, GPIO_PIN_SET);

    // HAL_SPI_Transmit takes a uint16_t count, so anything longer goes in chunks. Callers here
    // never exceed it, but a future full-frame blit would.
    while (length > 0)
    {
        const uint16_t chunk = (length > UINT16_MAX) ? UINT16_MAX : (uint16_t)length;

        if (HAL_SPI_Transmit(_spi, (uint8_t*)data, chunk, ILI9341_SPI_TIMEOUT_MS) != HAL_OK)
        {
            return false;
        }

        data += chunk;
        length -= chunk;
    }

    return true;
}

bool ILI9341::SendCommand(uint8_t command, const uint8_t* data, size_t length)
{
    Select();

    const bool ok = WriteCommand(command) && WriteData(data, length);

    Deselect();

    return ok;
}


// ---------------------------------------------------------------------------- setup

bool ILI9341::Init(uint8_t rotation)
{
    Deselect();

    // Reset is active low and must be held well past the controller's 10 us minimum; the panel
    // then needs time before it will accept commands.
    HAL_GPIO_WritePin(_rstPort, _rstPin, GPIO_PIN_SET);
    _delay(5);
    HAL_GPIO_WritePin(_rstPort, _rstPin, GPIO_PIN_RESET);
    _delay(20);
    HAL_GPIO_WritePin(_rstPort, _rstPin, GPIO_PIN_SET);
    _delay(150);

    const uint8_t* command = ILI9341_INIT_COMMANDS;

    while (*command)
    {
        const uint8_t opcode = *command++;
        uint8_t count = *command++;
        const bool delayAfter = (count & 0x80) != 0;

        count &= 0x7F;

        if (!SendCommand(opcode, command, count))
        {
            return false;
        }

        command += count;

        if (delayAfter)
        {
            _delay(150);
        }
    }

    return SetRotation(rotation);
}

bool ILI9341::SetRotation(uint8_t rotation)
{
    _rotation = rotation & 0x03;

    const uint8_t madctl = ILI9341_ROTATION_MADCTL[_rotation];

    return SendCommand(ILI9341_MADCTL, &madctl, 1);
}

bool ILI9341::InvertDisplay(bool invert)
{
    return SendCommand(invert ? ILI9341_INVON : ILI9341_INVOFF, NULL, 0);
}

uint16_t ILI9341::Width() const
{
    return (_rotation == ILI9341_ROTATION_LANDSCAPE
            || _rotation == ILI9341_ROTATION_LANDSCAPE_FLIP)
           ? ILI9341_TFTHEIGHT : ILI9341_TFTWIDTH;
}

uint16_t ILI9341::Height() const
{
    return (_rotation == ILI9341_ROTATION_LANDSCAPE
            || _rotation == ILI9341_ROTATION_LANDSCAPE_FLIP)
           ? ILI9341_TFTWIDTH : ILI9341_TFTHEIGHT;
}


// ---------------------------------------------------------------------------- drawing

bool ILI9341::SetAddrWindow(uint16_t x, uint16_t y, uint16_t w, uint16_t h)
{
    const uint16_t panelWidth = Width();
    const uint16_t panelHeight = Height();

    if (x >= panelWidth || y >= panelHeight || w == 0 || h == 0)
    {
        return false;
    }

    // Clip rather than reject: a field drawn near the edge should lose its overhang, not
    // vanish, and an unclipped window makes the controller wrap the write onto the next row.
    if (x + w > panelWidth)  w = panelWidth - x;
    if (y + h > panelHeight) h = panelHeight - y;

    const uint16_t x1 = x + w - 1;
    const uint16_t y1 = y + h - 1;

    const uint8_t columns[4] = { (uint8_t)(x >> 8), (uint8_t)x, (uint8_t)(x1 >> 8), (uint8_t)x1 };
    const uint8_t pages[4]   = { (uint8_t)(y >> 8), (uint8_t)y, (uint8_t)(y1 >> 8), (uint8_t)y1 };

    Select();

    const bool ok = WriteCommand(ILI9341_CASET) && WriteData(columns, sizeof(columns))
                 && WriteCommand(ILI9341_PASET) && WriteData(pages, sizeof(pages))
                 && WriteCommand(ILI9341_RAMWR);

    // Left selected on purpose: the caller streams pixel data straight into the open RAMWR.
    if (!ok)
    {
        Deselect();
    }

    return ok;
}

bool ILI9341::FillRect(uint16_t x, uint16_t y, uint16_t w, uint16_t h, uint16_t colour)
{
    const uint16_t panelWidth = Width();
    const uint16_t panelHeight = Height();

    if (x >= panelWidth || y >= panelHeight || w == 0 || h == 0)
    {
        return true;   // entirely off-panel: nothing to do, and not an error
    }

    if (x + w > panelWidth)  w = panelWidth - x;
    if (y + h > panelHeight) h = panelHeight - y;

    if (!SetAddrWindow(x, y, w, h))
    {
        return false;
    }

    for (size_t i = 0; i < ILI9341_SCRATCH_PIXELS; i++)
    {
        ili9341_scratch[i * 2]     = (uint8_t)(colour >> 8);
        ili9341_scratch[i * 2 + 1] = (uint8_t)colour;
    }

    size_t remaining = (size_t)w * (size_t)h;
    bool ok = true;

    while (remaining > 0 && ok)
    {
        const size_t pixels = (remaining > ILI9341_SCRATCH_PIXELS)
                              ? ILI9341_SCRATCH_PIXELS : remaining;

        ok = WriteData(ili9341_scratch, pixels * 2);
        remaining -= pixels;
    }

    Deselect();

    return ok;
}

bool ILI9341::FillScreen(uint16_t colour)
{
    return FillRect(0, 0, Width(), Height(), colour);
}

bool ILI9341::DrawChar(uint16_t x, uint16_t y, char c, uint16_t fg, uint16_t bg, uint8_t size)
{
    if (size == 0 || size > ILI9341_MAX_TEXT_SIZE)
    {
        return false;
    }

    const uint16_t cellWidth = ILI9341_FONT_CELL_WIDTH * size;
    const uint16_t cellHeight = ILI9341_FONT_CELL_HEIGHT * size;

    // One window for the whole cell, then every row streamed into the open RAMWR. Writing the
    // cell as a single run rather than pixel by pixel is the difference between one command
    // sequence and several hundred.
    if (!SetAddrWindow(x, y, cellWidth, cellHeight))
    {
        return false;
    }

    bool ok = true;

    for (uint8_t glyphRow = 0; glyphRow < ILI9341_FONT_CELL_HEIGHT && ok; glyphRow++)
    {
        // Build one glyph row at scale, then repeat it `size` times down the panel.
        for (uint8_t column = 0; column < ILI9341_FONT_CELL_WIDTH; column++)
        {
            const bool lit = ili9341_font_pixel(c, column, glyphRow);
            const uint16_t pixel = lit ? fg : bg;

            for (uint8_t repeat = 0; repeat < size; repeat++)
            {
                const size_t at = ((size_t)column * size + repeat) * 2;

                ili9341_scratch[at]     = (uint8_t)(pixel >> 8);
                ili9341_scratch[at + 1] = (uint8_t)pixel;
            }
        }

        for (uint8_t repeat = 0; repeat < size && ok; repeat++)
        {
            ok = WriteData(ili9341_scratch, (size_t)cellWidth * 2);
        }
    }

    Deselect();

    return ok;
}

bool ILI9341::DrawString(uint16_t x, uint16_t y, const char* text, size_t length,
                         uint16_t fg, uint16_t bg, uint8_t size)
{
    const uint16_t advance = ILI9341_FONT_CELL_WIDTH * size;

    for (size_t i = 0; i < length; i++)
    {
        const uint16_t at = x + (uint16_t)(i * advance);

        if (at >= Width())
        {
            break;   // clip rather than wrap onto the row below
        }

        if (!DrawChar(at, y, text[i], fg, bg, size))
        {
            return false;
        }
    }

    return true;
}
