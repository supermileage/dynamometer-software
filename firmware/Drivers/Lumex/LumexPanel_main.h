// HD44780 instruction set, as used by the Lumex character LCD.
//
// The controller has eight instructions, distinguished by the position of the highest set bit.
// Everything below that bit is options for that instruction, which is why the codes are built
// by OR-ing rather than listed as magic numbers:
//
//     0x01  clear
//     0x02  home
//     0x04  entry mode      | cursor direction | display shift
//     0x08  display control | display on | cursor on | blink on
//     0x10  cursor/display shift
//     0x20  function set    | bus width | line count | font
//     0x40  set CGRAM address (user-defined glyphs -- unused here)
//     0x80  set DDRAM address (this is how the cursor is moved)
//
// Sent with RS low. Anything sent with RS high is a character for the current DDRAM address.

#ifndef DRIVERS_LUMEX_LUMEXPANEL_MAIN_H_
#define DRIVERS_LUMEX_LUMEXPANEL_MAIN_H_

// --- Instructions
#define LUMEX_CMD_CLEAR             0x01u   // blanks DDRAM and homes the cursor. Slow: ~1.5 ms
#define LUMEX_CMD_HOME              0x02u   // cursor to 0,0 without clearing. Also slow
#define LUMEX_CMD_ENTRY_MODE        0x04u
#define LUMEX_CMD_DISPLAY_CONTROL   0x08u
#define LUMEX_CMD_CURSOR_SHIFT      0x10u
#define LUMEX_CMD_FUNCTION_SET      0x20u
#define LUMEX_CMD_SET_CGRAM_ADDR    0x40u
#define LUMEX_CMD_SET_DDRAM_ADDR    0x80u

// --- Options for LUMEX_CMD_ENTRY_MODE
#define LUMEX_ENTRY_INCREMENT       0x02u   // advance the cursor after each character
#define LUMEX_ENTRY_SHIFT_DISPLAY   0x01u   // scroll the whole display instead of the cursor

// --- Options for LUMEX_CMD_DISPLAY_CONTROL
#define LUMEX_DISPLAY_ON            0x04u
#define LUMEX_CURSOR_ON             0x02u   // the underline
#define LUMEX_BLINK_ON              0x01u   // the blinking block

// --- Options for LUMEX_CMD_FUNCTION_SET
#define LUMEX_FUNCTION_8BIT         0x10u   // all eight data lines wired (this board)
#define LUMEX_FUNCTION_2LINE        0x08u
#define LUMEX_FUNCTION_5X10_FONT    0x04u   // clear for the usual 5x8

// --- DDRAM layout.
//
// The two rows are NOT contiguous: row 0 starts at 0x00 and row 1 at 0x40, with the gap
// unused on a 16-column part. So moving to (row, column) is SET_DDRAM_ADDR | base | column,
// which is all SetCursor does.
#define LUMEX_DDRAM_ROW0_BASE       0x00u
#define LUMEX_DDRAM_ROW1_BASE       0x40u

// --- Timing, microseconds.
//
// This panel is write-only on this board: there is no R/W pin, so the busy flag can never be
// read and every wait has to be a fixed delay long enough for the worst case.
//
// ENABLE_PULSE_US doubles as the instruction-execution wait. The controller needs ~37 us to
// retire an ordinary instruction, far longer than the ~450 ns the enable pulse itself must be
// held. Nothing waits after E falls, so it is this hold that spaces one latch from the next and
// it has to cover the execution time on its own. 40 us covers it and lets the next byte follow
// immediately.
//
// If the panel is ever intermittent -- dropped or garbled characters, worse when warm -- raise
// this first. When TIM13 timed the pulse it counted at 500 kHz (200 MHz APB1 timer clock / 400),
// so the ARR of 40 it was handed was 41 ticks of 2 us: the wire saw ~82 us, and the 40 in that
// code was ticks wearing the units of microseconds. This is the value the datasheet asks for and
// the value the code always appeared to use, but it is half of what the board actually ran on
// for years, and the ~37 us it has to cover moves 20-30% with the controller's internal RC
// oscillator. With no R/W pin there is no busy flag to ask whether it was long enough.
#define LUMEX_ENABLE_PULSE_US       40u

// CLEAR and HOME are the two slow instructions, ~1.52 ms; 20 ms is generous and only ever
// costs on a screen change.
#define LUMEX_CLEAR_DELAY_MS        20u

// Power-on: the controller wants >40 ms after Vcc rises before it will accept anything, then
// a settle between the instructions of the reset sequence.
#define LUMEX_POWER_ON_DELAY_MS     40u
#define LUMEX_SETTLE_DELAY_MS       5u

#endif /* DRIVERS_LUMEX_LUMEXPANEL_MAIN_H_ */
