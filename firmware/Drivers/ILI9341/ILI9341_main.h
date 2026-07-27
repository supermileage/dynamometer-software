// ILI9341 register/command codes and 16-bit colour constants.
//
// Transcribed from the Adafruit_ILI9341 Arduino library (Adafruit_ILI9341.h), which is the
// clearest published statement of this controller's command set and of the power/gamma
// sequence a real panel needs. Only the constants and the init table came across; the
// library's own transport is Arduino's -- see this directory's README for why none of it is
// vendored, submoduled, or forked.
//
/*
Software License Agreement (BSD License)

Copyright (c) 2012 Adafruit Industries.  All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:
1. Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.
3. Neither the name of the copyright holders nor the
   names of its contributors may be used to endorse or promote products
   derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS ''AS IS'' AND ANY
EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

#ifndef DRIVERS_ILI9341_ILI9341_MAIN_H_
#define DRIVERS_ILI9341_ILI9341_MAIN_H_

// Panel geometry in its native portrait orientation. Landscape swaps them -- see
// ILI9341::Width()/Height(), which follow the active rotation.
#define ILI9341_TFTWIDTH  240
#define ILI9341_TFTHEIGHT 320

// --- Commands
#define ILI9341_NOP        0x00
#define ILI9341_SWRESET    0x01
#define ILI9341_RDDID      0x04
#define ILI9341_RDDST      0x09

#define ILI9341_SLPIN      0x10
#define ILI9341_SLPOUT     0x11
#define ILI9341_PTLON      0x12
#define ILI9341_NORON      0x13

#define ILI9341_RDMODE     0x0A
#define ILI9341_RDMADCTL   0x0B
#define ILI9341_RDPIXFMT   0x0C
#define ILI9341_RDIMGFMT   0x0D
#define ILI9341_RDSELFDIAG 0x0F

#define ILI9341_INVOFF     0x20
#define ILI9341_INVON      0x21
#define ILI9341_GAMMASET   0x26
#define ILI9341_DISPOFF    0x28
#define ILI9341_DISPON     0x29

#define ILI9341_CASET      0x2A   // Column address set
#define ILI9341_PASET      0x2B   // Page address set
#define ILI9341_RAMWR      0x2C   // Memory write
#define ILI9341_RAMRD      0x2E

#define ILI9341_PTLAR      0x30
#define ILI9341_VSCRDEF    0x33
#define ILI9341_MADCTL     0x36   // Memory access control -- rotation and colour order
#define ILI9341_VSCRSADD   0x37
#define ILI9341_PIXFMT     0x3A   // COLMOD

#define ILI9341_FRMCTR1    0xB1
#define ILI9341_FRMCTR2    0xB2
#define ILI9341_FRMCTR3    0xB3
#define ILI9341_INVCTR     0xB4
#define ILI9341_DFUNCTR    0xB6

#define ILI9341_PWCTR1     0xC0
#define ILI9341_PWCTR2     0xC1
#define ILI9341_PWCTR3     0xC2
#define ILI9341_PWCTR4     0xC3
#define ILI9341_PWCTR5     0xC4
#define ILI9341_VMCTR1     0xC5
#define ILI9341_VMCTR2     0xC7

#define ILI9341_RDID1      0xDA
#define ILI9341_RDID2      0xDB
#define ILI9341_RDID3      0xDC
#define ILI9341_RDID4      0xDD

#define ILI9341_GMCTRP1    0xE0
#define ILI9341_GMCTRN1    0xE1

// --- MADCTL bits. A blank screen is usually CS or reset polarity; wrong *colours* with a
// correct image is almost always the BGR bit.
#define ILI9341_MADCTL_MY  0x80   // Row address order: bottom to top
#define ILI9341_MADCTL_MX  0x40   // Column address order: right to left
#define ILI9341_MADCTL_MV  0x20   // Row/column exchange -- this is what makes it landscape
#define ILI9341_MADCTL_ML  0x10
#define ILI9341_MADCTL_RGB 0x00
#define ILI9341_MADCTL_BGR 0x08
#define ILI9341_MADCTL_MH  0x04

// --- Rotations, as indices into the MADCTL values above.
#define ILI9341_ROTATION_PORTRAIT       0   // 240x320
#define ILI9341_ROTATION_LANDSCAPE      1   // 320x240
#define ILI9341_ROTATION_PORTRAIT_FLIP  2
#define ILI9341_ROTATION_LANDSCAPE_FLIP 3

// --- Colours, RGB565.
#define ILI9341_BLACK       0x0000
#define ILI9341_NAVY        0x000F
#define ILI9341_DARKGREEN   0x03E0
#define ILI9341_DARKCYAN    0x03EF
#define ILI9341_MAROON      0x7800
#define ILI9341_PURPLE      0x780F
#define ILI9341_OLIVE       0x7BE0
#define ILI9341_LIGHTGREY   0xC618
#define ILI9341_DARKGREY    0x7BEF
#define ILI9341_BLUE        0x001F
#define ILI9341_GREEN       0x07E0
#define ILI9341_CYAN        0x07FF
#define ILI9341_RED         0xF800
#define ILI9341_MAGENTA     0xF81F
#define ILI9341_YELLOW      0xFFE0
#define ILI9341_WHITE       0xFFFF
#define ILI9341_ORANGE      0xFD20
#define ILI9341_GREENYELLOW 0xAFE5
#define ILI9341_PINK        0xFC18

// --- Largest text scale the driver will draw. Lives here rather than beside the driver class
// so the layout code -- which must not exceed it -- can see it without pulling in the HAL.
#define ILI9341_MAX_TEXT_SIZE 8

// --- The classic 5x7 glyphs, one column per byte, drawn in a 6x8 cell (the sixth column and
// eighth row are the inter-character gap). 1280 bytes of pure data lifted from Adafruit-GFX's
// glcdfont.c; the only Arduino-ism there was a PROGMEM attribute that is a no-op off AVR.
#define ILI9341_FONT_GLYPH_WIDTH  5
#define ILI9341_FONT_GLYPH_HEIGHT 7
#define ILI9341_FONT_CELL_WIDTH   6
#define ILI9341_FONT_CELL_HEIGHT  8

#endif /* DRIVERS_ILI9341_ILI9341_MAIN_H_ */
