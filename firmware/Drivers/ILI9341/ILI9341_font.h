#ifndef DRIVERS_ILI9341_ILI9341_FONT_H_
#define DRIVERS_ILI9341_ILI9341_FONT_H_

#include <stdbool.h>
#include <stdint.h>

#include "ILI9341_main.h"

#ifdef __cplusplus
extern "C" {
#endif

#define ILI9341_FONT_GLYPH_COUNT 256

// One column per byte, LSB at the top of the glyph. Glyph `c` starts at
// `c * ILI9341_FONT_GLYPH_WIDTH`; there is an entry for every value a char can take, so no
// bounds check is needed beyond masking to 8 bits.
extern const uint8_t ili9341_font[ILI9341_FONT_GLYPH_COUNT * ILI9341_FONT_GLYPH_WIDTH];

// Whether the pixel at (column, row) within a glyph's 5x7 box is set. Split out from the
// drawing code so the host tests can check glyph extraction without a panel.
static inline bool ili9341_font_pixel(char c, uint8_t column, uint8_t row)
{
    if (column >= ILI9341_FONT_GLYPH_WIDTH || row >= ILI9341_FONT_GLYPH_HEIGHT + 1)
    {
        return false;
    }

    const uint8_t bits = ili9341_font[(uint8_t)c * ILI9341_FONT_GLYPH_WIDTH + column];

    return (bits >> row) & 0x01;
}

#ifdef __cplusplus
}
#endif

#endif /* DRIVERS_ILI9341_ILI9341_FONT_H_ */
