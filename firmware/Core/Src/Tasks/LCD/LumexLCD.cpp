#include <Tasks/LCD/LumexLCD.hpp>
#include <Tasks/LCD/lumexlcd_main.h>
#include <Config/sysconfig.h>

#include "Tasks/Display/DisplayDriver.hpp"

extern TIM_HandleTypeDef* lumexLcdTimer;

extern size_t task_error_circular_buffer_index_writer;
extern task_error_data task_error_circular_buffer[TASK_ERROR_CIRCULAR_BUFFER_SIZE];

static volatile bool timerCallbackFlag = false;

LumexLCD::LumexLCD() :
		_task_error_buffer_writer(task_error_circular_buffer, &task_error_circular_buffer_index_writer, TASK_ERROR_CIRCULAR_BUFFER_SIZE),
		_lastScreen(DISPLAY_SCREEN_IDLE),
		_hasRendered(false)
{
	memset(_lastFrame.cells, ' ', sizeof(_lastFrame.cells));
}

bool LumexLCD::Init()
{

    // Enable to GND to tell that we are in command mode, not data mode
	HAL_GPIO_WritePin(LUMEX_LCD_EN_GPIO_Port, LUMEX_LCD_EN_Pin, GPIO_PIN_RESET);

    osDelay(40);


    // Proper 8-bit mode initialization sequence
    // Function set: 8-bit mode, 2-line, 5x8 font
    if (!WriteCommand(0x38))
    {
    	return false;
    }

    osDelay(5);


    // needs to be done twice
    if (!WriteCommand(0x38))
	{
		return false;
	}

	osDelay(5);

    // just to make sure it works
	if (!WriteCommand(0x38))
	{
		return false;
	}

	osDelay(5);

    // Display ON, Cursor OFF, Blink OFF
	if (!WriteCommand(0x0c))
	{
		return false;
	}

	osDelay(5);

    // Clear Display
    if (!ClearDisplay())
	{
    	return false;
	}

    return true;
}

bool LumexLCD::Clear()
{
	if (!ClearDisplay())
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

			if (!DisplayString(row, start, &frame.cells[row][start], column - start))
			{
				return false;
			}
		}
	}

	_lastFrame = frame;
	_lastScreen = state.screen;
	_hasRendered = true;

	return true;
}


bool LumexLCD::StartTimer(uint8_t microseconds)
{
	__HAL_TIM_SET_COUNTER(lumexLcdTimer, 0);
	__HAL_TIM_SET_AUTORELOAD(lumexLcdTimer, microseconds);
	if (HAL_TIM_Base_Start_IT(lumexLcdTimer) != HAL_OK)
	{
		task_error_data error_data = PopulateTaskErrorDataStruct(
			get_timestamp(),
			TASK_OFFSET_DISPLAY,
			static_cast<uint32_t>(ERROR_LUMEX_LCD_TIMER_START_FAILURE)
		);
		
		_task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
		return false;
	}

	return true;
}



bool LumexLCD::SendByte(uint8_t byte)
{
	// Very Inefficient Way of Toggling Pins but may decide to use registers instead in the future
	HAL_GPIO_WritePin(LUMEX_LCD_D7_GPIO_Port, LUMEX_LCD_D7_Pin, static_cast<GPIO_PinState>((byte >> 7) & 0x01));
	HAL_GPIO_WritePin(LUMEX_LCD_D6_GPIO_Port, LUMEX_LCD_D6_Pin, static_cast<GPIO_PinState>((byte >> 6) & 0x01));
	HAL_GPIO_WritePin(LUMEX_LCD_D5_GPIO_Port, LUMEX_LCD_D5_Pin, static_cast<GPIO_PinState>((byte >> 5) & 0x01));
	HAL_GPIO_WritePin(LUMEX_LCD_D4_GPIO_Port, LUMEX_LCD_D4_Pin, static_cast<GPIO_PinState>((byte >> 4) & 0x01));
	HAL_GPIO_WritePin(LUMEX_LCD_D3_GPIO_Port, LUMEX_LCD_D3_Pin, static_cast<GPIO_PinState>((byte >> 3) & 0x01));
	HAL_GPIO_WritePin(LUMEX_LCD_D2_GPIO_Port, LUMEX_LCD_D2_Pin, static_cast<GPIO_PinState>((byte >> 2) & 0x01));
	HAL_GPIO_WritePin(LUMEX_LCD_D1_GPIO_Port, LUMEX_LCD_D1_Pin, static_cast<GPIO_PinState>((byte >> 1) & 0x01));
	HAL_GPIO_WritePin(LUMEX_LCD_D0_GPIO_Port, LUMEX_LCD_D0_Pin, static_cast<GPIO_PinState>((byte >> 0) & 0x01));


	// Set EN Pin and start timer
	HAL_GPIO_WritePin(LUMEX_LCD_EN_GPIO_Port, LUMEX_LCD_EN_Pin, GPIO_PIN_SET);

	timerCallbackFlag = false;

	if (!StartTimer(40))
	{
		return false;
	}

	while(!timerCallbackFlag);

	return true;

}

bool LumexLCD::WriteData(uint8_t data)
{
	HAL_GPIO_WritePin(LUMEX_LCD_RS_GPIO_Port, LUMEX_LCD_RS_Pin, GPIO_PIN_SET);

	if (!SendByte(data))
	{
		return false;
	}
	return true;

}


bool LumexLCD::WriteCommand(uint8_t command)
{
	HAL_GPIO_WritePin(LUMEX_LCD_RS_GPIO_Port, LUMEX_LCD_RS_Pin, GPIO_PIN_RESET);

	if (!SendByte(command))
	{
		return false;
	}

	return true;
}

bool LumexLCD::ClearDisplay()
{
	if (!WriteCommand(0x01))
	{
		return false;
	}

	HAL_Delay(20);

	return true;
}


bool LumexLCD::SetCursor(uint8_t row, uint8_t column) {

	uint8_t address = (row == 0) ? 0x00 : 0x40;
	address += column;
	if (!WriteCommand(0x80 | address))
	{
		return false;
	}

	return true;

}

bool LumexLCD::DisplayChar(uint8_t row, uint8_t column, uint8_t character)
{
	if (!SetCursor(row, column))
	{
		return false;
	}

	if (!WriteData(character))
	{
		return false;
	}

	return true;
}

bool LumexLCD::DisplayString(uint8_t row, uint8_t column, const char* string, size_t size)
{
	assert_param(row < LUMEX_LCD_ROWS);

	for (uint8_t i = 0; i < size; i++)
	{
		// Clamp instead of wrapping: drop any chars past the last column so an
		// overflow fails visibly in one cell rather than corrupting another row.
		if (column >= LUMEX_LCD_COLUMNS)
		{
			break;
		}

		if (!SetCursor(row, column))
		{
			return false;
		}

		if (!WriteData(string[i]))
		{
			return false;
		}

		column++;
	}

	return true;


}

bool LumexLCD::ToggleBlink(bool enable)
{
    if (enable)
    {
        // Binary: 00001 1 1 1 = 0x0F
		// Display ON, Cursor ON, Blink ON
        if (!WriteCommand(0x0F))
        {
            return false;
        }
    }
    else
    {
        // Display ON, Cursor OFF, Blink OFF
        if (!WriteCommand(0x0C))
        {
            return false;
        }
    }

    return true;
}


extern "C" void lumex_lcd_timer_interrupt()
{
	HAL_TIM_Base_Stop_IT(lumexLcdTimer);
	HAL_GPIO_WritePin(LUMEX_LCD_EN_GPIO_Port, LUMEX_LCD_EN_Pin, GPIO_PIN_RESET);
	timerCallbackFlag = true;

}

static_assert(DisplayDriver<LumexLCD>,
              "LumexLCD must satisfy DisplayDriver -- see Tasks/Display/DisplayDriver.hpp");

extern "C" void lumex_lcd_main(osMessageQueueId_t sessionControllerToDisplayHandle)
{
	LumexLCD lcd;

	if (!lcd.Init())
	{
		 osThreadSuspend(osThreadGetId());
	}

	RunDisplayTask(lcd, sessionControllerToDisplayHandle);
}






