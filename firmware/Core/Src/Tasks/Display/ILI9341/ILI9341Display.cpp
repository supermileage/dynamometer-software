#include "Tasks/Display/ILI9341/ILI9341Display.hpp"

#include <string.h>

#include "FreeRTOS.h"   // configTICK_RATE_HZ, for the static_assert below

#include "Config/config.h"
#include "Config/sysconfig.h"

#include "Tasks/Display/DisplayDriver.hpp"
#include "Tasks/Display/ILI9341/ili9341_main.h"

#include "TimeKeeping/timestamps.h"

extern SPI_HandleTypeDef hspi1;

extern size_t task_error_circular_buffer_index_writer;
extern task_error_data task_error_circular_buffer[TASK_ERROR_CIRCULAR_BUFFER_SIZE];

// The panel is painted on black; every field carries its own foreground.
#define ILI9341_DISPLAY_BACKGROUND ILI9341_BLACK

// The wait the panel driver uses for its reset and power-on timings, ~325 ms of them. Handed
// in so the driver itself stays free of the RTOS (see ILI9341::DelayMs): here, inside a task,
// the right answer is to yield rather than spin, which HAL_Delay would do.
//
// osDelay counts ticks and returns a status; at configTICK_RATE_HZ one tick is one
// millisecond, so the only adaptation is discarding the status. The static_assert is what
// makes a change of tick rate a build error instead of a panel that misses its timings.
static_assert(configTICK_RATE_HZ == 1000,
              "osDelay is being called with milliseconds; that only holds at a 1 kHz tick.");

static void DisplayDelayMs(uint32_t milliseconds)
{
    osDelay(milliseconds);
}


ILI9341Display::ILI9341Display() :
    _panel(&hspi1,
           ILI_SPI1_LCD_CS_GPIO_Port, ILI_SPI1_LCD_CS_Pin,
           ILI_LCD_DC_GPIO_Port,      ILI_LCD_DC_Pin,
           ILI_LCD_RST_GPIO_Port,     ILI_LCD_RST_Pin,
           DisplayDelayMs),
    _task_error_buffer_writer(task_error_circular_buffer,
                              &task_error_circular_buffer_index_writer,
                              TASK_ERROR_CIRCULAR_BUFFER_SIZE),
    _lastScreen(DISPLAY_SCREEN_IDLE),
    _hasRendered(false)
{
    memset(&_lastFrame, 0, sizeof(_lastFrame));
    memset(&_frame, 0, sizeof(_frame));
    memset(&_detail, 0, sizeof(_detail));
}

// The Show* methods only record. Drawing happens in Render, so that everything on screen still
// goes through one layout pass and one diff -- these must not paint behind its back.
bool ILI9341Display::ShowAngularAcceleration(float radiansPerSecondSquared)
{
    _detail.angular_acceleration = radiansPerSecondSquared;
    return true;
}

bool ILI9341Display::ShowPeakForce(float newtons)
{
    _detail.peak_force = newtons;
    return true;
}

bool ILI9341Display::ShowSessionElapsed(uint32_t seconds)
{
    _detail.session_seconds = seconds;
    return true;
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
    ili9341_layout(&state, &_detail, &_frame);

    // A new screen has a different set of fields in different places, so there is nothing to
    // diff against -- blank the panel and paint all of it. Within a screen the field list is
    // positionally stable, which is what makes the index-wise comparison below valid.
    const bool screenChanged = !_hasRendered || state.screen != _lastScreen;

    if (screenChanged && !Clear())
    {
        _hasRendered = false;
        return false;
    }

    for (uint8_t i = 0; i < _frame.count; i++)
    {
        if (!screenChanged
            && i < _lastFrame.count
            && ili9341_field_equal(&_frame.fields[i], &_lastFrame.fields[i]))
        {
            continue;
        }

        if (!DrawField(_frame.fields[i]))
        {
            task_error_data error_data = PopulateTaskErrorDataStruct(
                get_timestamp(),
                TASK_OFFSET_DISPLAY,
                static_cast<uint32_t>(ERROR_DISPLAY_SPI_TRANSMIT_FAILURE)
            );

            _task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);

            // What is on the panel no longer matches _lastFrame, so the field-by-field diff
            // would skip cells that were never actually painted. Force the next pass to clear
            // and repaint everything.
            _hasRendered = false;
            return false;
        }
    }

    _lastFrame = _frame;
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
        // Suspend rather than return: returning from a task function disables interrupts and
        // spins (prvTaskExitError), which would take the rest of the board down over a display
        // that would not start.
        osThreadSuspend(osThreadGetId());
    }

    RunDisplayTask(display, sessionControllerToDisplayHandle);

    // RunDisplayTask does not return; this is here so that a future edit which lets it return
    // parks this task instead of killing the scheduler.
    osThreadSuspend(osThreadGetId());
}
