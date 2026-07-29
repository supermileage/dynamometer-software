#include <Tasks/Display/Lumex/LumexLCD.hpp>
#include <Tasks/Display/Lumex/lumexlcd_main.h>
#include <Config/sysconfig.h>

#include "FreeRTOS.h"   // configTICK_RATE_HZ, for the static_assert below

#include "Tasks/Display/DisplayDriver.hpp"

extern size_t task_error_circular_buffer_index_writer;
extern task_error_data task_error_circular_buffer[TASK_ERROR_CIRCULAR_BUFFER_SIZE];


// --- The two waits the panel driver needs, supplied here so the driver itself stays free of
//     both the RTOS and any particular timer. Same arrangement as ILI9341::DelayMs.

// Microseconds, for the ~40 us enable pulse. Busy-waits on the free-running timestamp counter
// -- the one every sample is stamped from -- rather than a timer of its own.
//
// This used to be TIM13: a whole peripheral, an NVIC line, an ISR and a volatile flag, whose
// only job was to drop E and set the flag that this task was *already spinning on*. It cost a
// timer and saved no CPU, because the spin was there either way. Spinning 40 us directly is the
// same behaviour with none of the machinery, and TIM13 is now free.
//
// Note that TIM13 counted at 500 kHz, so the pulse it produced was ~82 us rather than the 40 us
// its argument implied. See LUMEX_ENABLE_PULSE_US before adjusting this.
//
// osDelay cannot do this job: at a 1 kHz tick its floor is 1 ms, which would stretch every byte
// 25x and a full 32-cell repaint from ~2.6 ms to ~64 ms.
//
// The loop is bounded as well as timed. get_timestamp() reads a counter that SessionController
// starts, and SESSION_CONTROLLER_TASK_ENABLE 0 is a legal configuration -- with the counter
// frozen the elapsed time would never advance and this would hang the display task forever.
// LumexLCD::Init starts the counter itself for that reason; the bound is what makes a failure
// there produce a mistimed panel rather than a wedged task.
static void PanelDelayUs(uint32_t microseconds)
{
    const uint32_t start = get_timestamp();

    // Counts iterations, not microseconds -- it only exists for the case where the counter is
    // frozen and the timed condition can never come true. Each pass is a volatile read of CNT
    // and a compare, so a 40 us wait needs a couple of thousand of these and the limit is far
    // enough above that to never end a healthy wait early.
    uint32_t guard = 0;
    const uint32_t guardLimit = 100000u;

    while ((uint32_t)(get_timestamp() - start) < microseconds && ++guard < guardLimit)
    {
        // Spin. Nothing else can usefully happen in 40 us.
    }
}

// Milliseconds, for power-on and the clear instruction. Long enough to be worth yielding for,
// so this is osDelay -- never HAL_Delay, which spins and burns CPU other tasks want.
static_assert(configTICK_RATE_HZ == 1000,
              "osDelay is being called with milliseconds; that only holds at a 1 kHz tick.");

static void PanelDelayMs(uint32_t milliseconds)
{
    osDelay(milliseconds);
}

// Board wiring. The driver takes this rather than reaching for the LUMEX_LCD_* macros, so it
// depends on nothing but the pins it is handed.
static const LumexPanel::Pins LUMEX_PINS = {
    {
        { LUMEX_LCD_D0_GPIO_Port, LUMEX_LCD_D0_Pin },
        { LUMEX_LCD_D1_GPIO_Port, LUMEX_LCD_D1_Pin },
        { LUMEX_LCD_D2_GPIO_Port, LUMEX_LCD_D2_Pin },
        { LUMEX_LCD_D3_GPIO_Port, LUMEX_LCD_D3_Pin },
        { LUMEX_LCD_D4_GPIO_Port, LUMEX_LCD_D4_Pin },
        { LUMEX_LCD_D5_GPIO_Port, LUMEX_LCD_D5_Pin },
        { LUMEX_LCD_D6_GPIO_Port, LUMEX_LCD_D6_Pin },
        { LUMEX_LCD_D7_GPIO_Port, LUMEX_LCD_D7_Pin },
    },
    { LUMEX_LCD_RS_GPIO_Port, LUMEX_LCD_RS_Pin },
    { LUMEX_LCD_EN_GPIO_Port, LUMEX_LCD_EN_Pin },
};


LumexLCD::LumexLCD() :
		_panel(LUMEX_PINS, PanelDelayUs, PanelDelayMs),
		_task_error_buffer_writer(task_error_circular_buffer, &task_error_circular_buffer_index_writer, TASK_ERROR_CIRCULAR_BUFFER_SIZE),
		_lastScreen(DISPLAY_SCREEN_IDLE),
		_hasRendered(false)
{
	memset(_lastFrame.cells, ' ', sizeof(_lastFrame.cells));
}

bool LumexLCD::Init()
{
	// PanelDelayUs measures against this counter, so it has to be running before the panel is
	// touched. SessionController starts it too, and here it always gets there first -- it runs
	// at osPriorityHigh against this task's osPriorityBelowNormal. Doing it here as well is what
	// keeps this task working when the session controller is compiled out.
	//
	// So this call is normally the *second* one, which is exactly what start_timestamp_timer was
	// changed to tolerate: HAL_TIM_Base_Start underneath it reports an already-running timer as
	// HAL_ERROR, and taking that at face value suspended this task and blanked the panel.
	if (start_timestamp_timer() != HAL_OK)
	{
		task_error_data error_data = PopulateTaskErrorDataStruct(
			get_timestamp(),
			TASK_OFFSET_DISPLAY,
			static_cast<uint32_t>(ERROR_DISPLAY_INIT_FAILURE)
		);

		_task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
		return false;
	}

	return _panel.Init();
}

bool LumexLCD::Clear()
{
	if (!_panel.ClearDisplay())
	{
		return false;
	}

	memset(_lastFrame.cells, ' ', sizeof(_lastFrame.cells));

	return true;
}

bool LumexLCD::Render(const session_controller_to_display& state)
{
	lumex_frame frame;
	lumex_render(&state, &frame);

	// Every Show*Screen used to open with a ClearDisplay, and the one redraw that deliberately
	// did not -- a tick inside the RPM editor -- is also the one that does not change screen.
	// So "clear when the screen id moves" is the same rule, derived rather than passed along.
	if (!_hasRendered || state.screen != _lastScreen)
	{
		if (!Clear())
		{
			_hasRendered = false;
			return false;
		}
	}

	// Write each run of changed cells in one go. Runs rather than whole rows because the
	// common case in a session is one field moving: five cells out of thirty-two.
	for (uint8_t row = 0; row < LUMEX_LCD_ROWS; row++)
	{
		uint8_t column = 0;

		while (column < LUMEX_LCD_COLUMNS)
		{
			if (frame.cells[row][column] == _lastFrame.cells[row][column])
			{
				column++;
				continue;
			}

			const uint8_t start = column;
			while (column < LUMEX_LCD_COLUMNS
			       && frame.cells[row][column] != _lastFrame.cells[row][column])
			{
				column++;
			}

			if (!_panel.DisplayString(row, start, &frame.cells[row][start], column - start))
			{
				// The panel no longer matches _lastFrame, so the diff would skip cells that
				// were never written. Force a full clear and repaint next pass.
				_hasRendered = false;
				return false;
			}
		}
	}

	_lastFrame = frame;
	_lastScreen = state.screen;
	_hasRendered = true;

	return true;
}

static_assert(DisplayDriver<LumexLCD>,
              "LumexLCD must satisfy DisplayDriver -- see Tasks/Display/DisplayDriver.hpp");

extern "C" void lumex_lcd_main(osMessageQueueId_t sessionControllerToDisplayHandle)
{
	LumexLCD lcd;

	if (!lcd.Init())
	{
		// Suspend rather than return: returning from a task function disables interrupts and
		// spins (prvTaskExitError), taking the whole board down over a display fault.
		osThreadSuspend(osThreadGetId());
	}

	RunDisplayTask(lcd, sessionControllerToDisplayHandle);

	osThreadSuspend(osThreadGetId());
}
