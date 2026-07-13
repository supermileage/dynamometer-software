#include <Tasks/LCD/LumexLCD.hpp>
#include <Tasks/LCD/lumexlcd_main.h>

extern TIM_HandleTypeDef* lumexLcdTimer;

extern size_t task_error_circular_buffer_index_writer;
extern task_error_data task_error_circular_buffer[TASK_ERROR_CIRCULAR_BUFFER_SIZE];

static volatile bool timerCallbackFlag = false;

LumexLCD::LumexLCD(osMessageQueueId_t sessionControllerToLumexLcdHandle) :
		_task_error_buffer_writer(task_error_circular_buffer, &task_error_circular_buffer_index_writer, TASK_ERROR_CIRCULAR_BUFFER_SIZE),
		_fromSCqHandle(sessionControllerToLumexLcdHandle)
{}

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

 void LumexLCD::Run(void)
 {

	session_controller_to_lumex_lcd msg;
     memset(&msg, 0, sizeof(msg));

     while (1)
     {
         // Block until a message arrives
         if (osMessageQueueGet(_fromSCqHandle, &msg, 0, osWaitForever) == osOK)
         {
             // Drain any remaining messages to ensure we process all pending commands
             do
             {
                 switch (msg.op)
                 {
                     case CLEAR_DISPLAY:
                         ClearDisplay();
                         break;

                     case WRITE_TO_DISPLAY:
                         if (!DisplayString(msg.row, msg.column, (const char*) msg.display_string, msg.size))
						 {
							return;
						 }
                         break;

                     default:
                         break;
                 }
             }
             while (osMessageQueueGet(_fromSCqHandle, &msg, 0, 0) == osOK);
         }

		 osDelay(LCD_TASK_OSDELAY);
     }
 }

//void LumexLCD::Run()
//{
//	while(1)
//	{
//		DisplayString(0, 0, "hi");
//	}
//}


bool LumexLCD::StartTimer(uint8_t microseconds)
{
	__HAL_TIM_SET_COUNTER(lumexLcdTimer, 0);
	__HAL_TIM_SET_AUTORELOAD(lumexLcdTimer, microseconds);
	if (HAL_TIM_Base_Start_IT(lumexLcdTimer) != HAL_OK)
	{
		task_error_data error_data = PopulateTaskErrorDataStruct(
			get_timestamp(),
			TASK_OFFSET_LUMEX_LCD,
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
	assert_param(size < SESSION_CONTROLLER_TO_LUMEX_LCD_MSG_STRING_SIZE);
	
	for (uint8_t i = 0; i < size; i++)
	{
		if (!SetCursor(row, column))
		{
			return false;
		}

		if (!WriteData(string[i]))
		{
			return false;
		}

		column++;

		// Clamp instead of wrapping: drop any chars past the last column so an
		// overflow fails visibly in one cell rather than corrupting another row.
		if (column >= 16)
		{
			break;
		}
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

extern "C" void lumex_lcd_main(osMessageQueueId_t sessionControllerToLumexLcdHandle)
{
	LumexLCD lcd = LumexLCD(sessionControllerToLumexLcdHandle);

	if (!lcd.Init())
	{
		 osThreadSuspend(osThreadGetId());;
	}


	lcd.Run();
}






