#ifndef INC_TASKS_DISPLAY_LUMEX_LUMEXLCD_HPP_
#define INC_TASKS_DISPLAY_LUMEX_LUMEXLCD_HPP_

#include "main.h"

#include "cmsis_os2.h"

#include "string.h"

#include "Config/config.h"

#include "CircularBufferWriter.hpp"

#include "LumexPanel.hpp"

#include "MessagePassing/messages_private.h"
#include "MessagePassing/messages_public.h"
#include "MessagePassing/osqueue_helpers.h"

#include "Tasks/Display/Lumex/lumex_layout.h"

#include "TimeKeeping/timestamps.h"

// The Lumex panel's side of the display split: screen state in, changed cells out.
//
// Owns a LumexPanel (Drivers/Lumex) and adds everything the panel itself has no business
// knowing -- what the screens look like, which cells moved since the last frame, and the
// FreeRTOS task around it. The same division as ILI9341Display over ILI9341, and the reason
// this class no longer contains a line of HD44780 protocol.
//
// Satisfies the DisplayDriver concept (Tasks/Display/DisplayDriver.hpp) without inheriting
// anything: the panel choice is fixed at link time, so the contract is checked at compile time
// and there is no vtable. See Core/Src/Tasks/Display/README.md for the display split.
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

		// --- Extended session detail: not shown here.
		//
		// Thirty-two character cells are fully spoken for by speed, force and drive mode, so
		// there is nowhere to put these. They are accepted and discarded rather than left off
		// the class, because DisplayDriver requires them of every panel and the display task
		// calls them without knowing which one it is driving.
		//
		// Inline and empty, so each costs nothing: the calls vanish at -O0 as well as -Os.
		bool ShowAngularAcceleration(float radiansPerSecondSquared)
		{
			(void)radiansPerSecondSquared;
			return true;
		}

		bool ShowPeakForce(float newtons)
		{
			(void)newtons;
			return true;
		}

		bool ShowSessionElapsed(uint32_t seconds)
		{
			(void)seconds;
			return true;
		}


	private:
		LumexPanel _panel;

		CircularBufferWriter<task_error_data> _task_error_buffer_writer;

		// What is currently on the panel, and which screen put it there. A change of screen
		// forces a physical clear -- the old code cleared inside every Show*Screen, and this
		// reproduces exactly that, including not clearing on a redraw of the same screen.
		lumex_frame _lastFrame;
		display_screen_id _lastScreen;
		bool _hasRendered;
};

#endif /* INC_TASKS_DISPLAY_LUMEX_LUMEXLCD_HPP_ */
