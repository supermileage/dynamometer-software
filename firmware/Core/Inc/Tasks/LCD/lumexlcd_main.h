#ifndef INC_TASKS_LCD_LUMEXLCD_MAIN_H_
#define INC_TASKS_LCD_LUMEXLCD_MAIN_H_

#include "main.h"

#include "cmsis_os2.h"


#ifdef __cplusplus
extern "C" {
#endif

void lumex_lcd_timer_interrupt();
void lumex_lcd_main(osMessageQueueId_t lumexLcdToSessionControllerqHandle);

#ifdef __cplusplus
}
#endif




#endif /* INC_TASKS_LCD_LUMEXLCD_MAIN_H_ */
