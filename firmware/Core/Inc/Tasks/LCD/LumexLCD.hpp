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

#include "TimeKeeping/timestamps.h"

#ifdef __cplusplus
extern "C" {
#endif

class LumexLCD
{
	public:
		LumexLCD(osMessageQueueId_t lumexLcdToSessionControllerqHandle);
		~LumexLCD() = default;

		bool Init();
		void Run();


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

		osMessageQueueId_t _fromSCqHandle;
};


#ifdef __cplusplus
}
#endif



#endif /* INC_TASKS_LCD_LUMEXLCD_HPP_ */
