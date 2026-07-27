#ifndef INC_TASKS_DISPLAY_DISPLAYDRIVER_HPP_
#define INC_TASKS_DISPLAY_DISPLAYDRIVER_HPP_

// What every display driver has to be, and the task loop they share.
//
// A concept rather than a base class. Virtual dispatch would cost a vtable pointer per object
// and an indirect call per draw for a choice that is fixed at link time -- exactly one driver
// is compiled in -- so this checks the same contract at compile time and inlines through it.
//
// It also deliberately exposes no drawing primitives. A 16x2 character LCD and a 320x240 TFT
// have wildly different capabilities, and any common *drawing* API would either cap the TFT or
// be meaningless on the LCD. Render() takes the whole screen state and each driver does
// whatever its panel can with it, so a richer panel needs nothing added here.

#include <concepts>
#include <cstring>

#include "cmsis_os2.h"

#include "Config/sysconfig.h"
#include "MessagePassing/messages_private.h"

template <typename T>
concept DisplayDriver = requires(T driver, const session_controller_to_display& state)
{
    // Brings the panel up. False means the task suspends rather than spinning on dead hardware.
    { driver.Init() } -> std::same_as<bool>;

    // Blanks the panel and forgets what was on it, so the next Render repaints in full.
    { driver.Clear() } -> std::same_as<bool>;

    // Paints one screen state. Called on every message; drivers are expected to diff against
    // what they last drew and repaint only what moved.
    { driver.Render(state) } -> std::same_as<bool>;
};

// The queue-drain loop, identical for every panel.
//
// Drains to the newest message before drawing: each one is the whole of what should be on
// screen, so the ones behind it are already stale and rendering them in turn would only paint
// values the user is never going to see. That matters more the slower the panel is.
template <DisplayDriver Display>
void RunDisplayTask(Display& display, osMessageQueueId_t queue)
{
    session_controller_to_display state;
    memset(&state, 0, sizeof(state));

    while (1)
    {
        if (osMessageQueueGet(queue, &state, 0, osWaitForever) == osOK)
        {
            while (osMessageQueueGet(queue, &state, 0, 0) == osOK);

            if (!display.Render(state))
            {
                return;
            }
        }

        osDelay(sysconfig_get_u32(SYSCFG_LCD_TASK_OSDELAY));
    }
}

#endif /* INC_TASKS_DISPLAY_DISPLAYDRIVER_HPP_ */
