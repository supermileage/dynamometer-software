#ifndef INC_TASKS_LCD_LUMEXLCD_HPP_
#define INC_TASKS_LCD_LUMEXLCD_HPP_

#include "main.h"

#include "cmsis_os2.h"

#include "string.h"

#include "Config/config.h"

#include "CircularBufferWriter.hpp"

#include "MessagePassing/messages_private.h"
#include "MessagePassing/messages_public.h"
#include "MessagePassing/osqueue_helpers.h"

#include "Tasks/LCD/lumex_layout.h"

#include "TimeKeeping/timestamps.h"

// Lumex 16x2 character LCD, bit-banged over GPIO.
//
// Satisfies the DisplayDriver concept (Tasks/Display/DisplayDriver.hpp) without inheriting
// anything: the panel choice is fixed at link time, so the contract is checked at compile time
// and there is no vtable. See Core/Src/Tasks/LCD/README.md for the display split.
class LumexLCD
{
	public:
		LumexLCD();
		~LumexLCD() = default;

		bool Init();

		// Blanks the panel and forgets what was on it, so the next Render redraws in full.
		bool Clear();

		// Lays the screen state out on the 2x16 grid and writes only the cells that differ
		// from what is already up there. Reposts are frequent -- the FSM sends the whole
		// state whenever any part of it moves -- and this panel is slow, so the diff is what
		// keeps a changed RPM reading to the five cells it occupies.
		bool Render(const session_controller_to_display& state);


	private:
		bool StartTimer(uint8_t microseconds);
		bool SendByte(uint8_t byte);
		bool WriteData(uint8_t data);
		bool WriteCommand(uint8_t command);
		bool ClearDisplay();
		bool SetCursor(uint8_t row, uint8_t column);
		bool DisplayChar(uint8_t row, uint8_t column, uint8_t character);
		bool DisplayString(uint8_t row, uint8_t column, const char* string, size_t size);
		bool ToggleBlink(bool enable);

		CircularBufferWriter<task_error_data> _task_error_buffer_writer;

		// What is currently on the panel, and which screen put it there. A change of screen
		// forces a physical clear -- the old code cleared inside every Show*Screen, and this
		// reproduces exactly that, including not clearing on a redraw of the same screen.
		lumex_frame _lastFrame;
		display_screen_id _lastScreen;
		bool _hasRendered;
};

#endif /* INC_TASKS_LCD_LUMEXLCD_HPP_ */
