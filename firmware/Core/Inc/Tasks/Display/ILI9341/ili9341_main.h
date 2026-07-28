#ifndef INC_TASKS_DISPLAY_ILI9341_MAIN_H_
#define INC_TASKS_DISPLAY_ILI9341_MAIN_H_

#include "main.h"

#include "cmsis_os2.h"

#ifdef __cplusplus
extern "C" {
#endif

// Entry point for the display task when ILI9341_LCD_TASK_ENABLE is the selected driver.
// Mirrors lumex_lcd_main(): same queue, same message, different panel.
void ili9341_lcd_main(osMessageQueueId_t sessionControllerToDisplayqHandle);

#ifdef __cplusplus
}
#endif

#endif /* INC_TASKS_DISPLAY_ILI9341_MAIN_H_ */
