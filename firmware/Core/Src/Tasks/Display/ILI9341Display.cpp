#include "Tasks/Display/ILI9341Display.hpp"

#include <string.h>

#include "Config/config.h"
#include "Config/sysconfig.h"

#include "Tasks/Display/DisplayDriver.hpp"
#include "Tasks/Display/ili9341_main.h"

#include "TimeKeeping/timestamps.h"

extern SPI_HandleTypeDef hspi1;

extern size_t task_error_circular_buffer_index_writer;
extern task_error_data task_error_circular_buffer[TASK_ERROR_CIRCULAR_BUFFER_SIZE];

// The panel is painted on black; every field carries its own foreground.
#define ILI9341_DISPLAY_BACKGROUND ILI9341_BLACK


ILI9341Display::ILI9341Display() :
    _panel(&hspi1,
           ILI_SPI1_LCD_CS_GPIO_Port, ILI_SPI1_LCD_CS_Pin,
           ILI_LCD_DC_GPIO_Port,      ILI_LCD_DC_Pin,
           ILI_LCD_RST_GPIO_Port,     ILI_LCD_RST_Pin),
    _task_error_buffer_writer(task_error_circular_buffer,
                              &task_error_circular_buffer_index_writer,
                              TASK_ERROR_CIRCULAR_BUFFER_SIZE),
    _lastScreen(DISPLAY_SCREEN_IDLE),
    _hasRendered(false)
{
    memset(&_lastFrame, 0, sizeof(_lastFrame));
}

bool ILI9341Display::Init()
{
    if (!_panel.Init(ILI9341_DISPLAY_ROTATION))
    {
        task_error_data error_data = PopulateTaskErrorDataStruct(
            get_timestamp(),
            TASK_OFFSET_DISPLAY,
            static_cast<uint32_t>(ERROR_DISPLAY_INIT_FAILURE)
        );

        _task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
        return false;
    }

    return Clear();
}

bool ILI9341Display::Clear()
{
    if (!_panel.FillScreen(ILI9341_DISPLAY_BACKGROUND))
    {
        return false;
    }

    memset(&_lastFrame, 0, sizeof(_lastFrame));

    return true;
}

bool ILI9341Display::DrawField(const ili9341_field& field)
{
    return _panel.DrawString(field.x, field.y, field.text, field.length,
                             field.colour, ILI9341_DISPLAY_BACKGROUND, field.size);
}

bool ILI9341Display::Render(const session_controller_to_display& state)
{
    ili9341_frame frame;
    ili9341_layout(&state, &frame);

    // A new screen has a different set of fields in different places, so there is nothing to
    // diff against -- blank the panel and paint all of it. Within a screen the field list is
    // positionally stable, which is what makes the index-wise comparison below valid.
    const bool screenChanged = !_hasRendered || state.screen != _lastScreen;

    if (screenChanged && !Clear())
    {
        return false;
    }

    for (uint8_t i = 0; i < frame.count; i++)
    {
        if (!screenChanged
            && i < _lastFrame.count
            && ili9341_field_equal(&frame.fields[i], &_lastFrame.fields[i]))
        {
            continue;
        }

        if (!DrawField(frame.fields[i]))
        {
            task_error_data error_data = PopulateTaskErrorDataStruct(
                get_timestamp(),
                TASK_OFFSET_DISPLAY,
                static_cast<uint32_t>(ERROR_DISPLAY_SPI_TRANSMIT_FAILURE)
            );

            _task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
            return false;
        }
    }

    _lastFrame = frame;
    _lastScreen = state.screen;
    _hasRendered = true;

    return true;
}

static_assert(DisplayDriver<ILI9341Display>,
              "ILI9341Display must satisfy DisplayDriver -- see Tasks/Display/DisplayDriver.hpp");

extern "C" void ili9341_lcd_main(osMessageQueueId_t sessionControllerToDisplayHandle)
{
    // Static rather than a local: the display task runs on a small FreeRTOS stack and this
    // object carries a frame of layout state. -fno-threadsafe-statics is set and this function
    // runs exactly once, so there is no guard variable and no initialisation race.
    static ILI9341Display display;

    if (!display.Init())
    {
        osThreadSuspend(osThreadGetId());
    }

    RunDisplayTask(display, sessionControllerToDisplayHandle);
}
