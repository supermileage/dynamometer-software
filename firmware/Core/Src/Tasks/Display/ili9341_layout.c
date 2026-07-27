#include "Tasks/Display/ili9341_layout.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

#include "ILI9341_main.h"

#include "Tasks/Display/display_common.h"

// Palette. Values white, labels and units grey, the drive mode coloured by what it is doing --
// the one thing worth spotting across the room while the rig is running.
#define COLOUR_BACKGROUND ILI9341_BLACK
#define COLOUR_VALUE      ILI9341_WHITE
#define COLOUR_LABEL      ILI9341_LIGHTGREY
#define COLOUR_ON         ILI9341_GREEN
#define COLOUR_OFF        ILI9341_RED
#define COLOUR_BRAKE      ILI9341_YELLOW

// Text scales, in 6x8 cells.
#define SIZE_TITLE  3   // 18x24
#define SIZE_HUGE   6   // 36x48
#define SIZE_VALUE  5   // 30x40
#define SIZE_TOGGLE 4   // 24x32
#define SIZE_SMALL  2   // 12x16

#define CELL_WIDTH(size) (ILI9341_FONT_CELL_WIDTH * (size))

// Centres a `length`-character run of the given scale.
static uint16_t centred(uint8_t length, uint8_t size)
{
    const uint16_t width = (uint16_t)length * CELL_WIDTH(size);

    return (width >= ILI9341_LAYOUT_WIDTH) ? 0 : (uint16_t)((ILI9341_LAYOUT_WIDTH - width) / 2);
}

static void add_field(ili9341_frame *out, uint16_t x, uint16_t y, uint8_t size, uint16_t colour,
                      const char *text)
{
    if (out->count >= ILI9341_MAX_FIELDS)
    {
        return;
    }

    ili9341_field *field = &out->fields[out->count++];

    field->x = x;
    field->y = y;
    field->size = size;
    field->colour = colour;

    size_t length = strlen(text);
    if (length > ILI9341_FIELD_TEXT_MAX - 1)
    {
        length = ILI9341_FIELD_TEXT_MAX - 1;
    }

    memcpy(field->text, text, length);
    field->text[length] = '\0';
    field->length = (uint8_t)length;
}

// Centred horizontally, which is what every screen but the session readout wants.
static void add_centred(ili9341_frame *out, uint16_t y, uint8_t size, uint16_t colour,
                        const char *text)
{
    add_field(out, centred((uint8_t)strlen(text), size), y, size, colour, text);
}

bool ili9341_field_equal(const ili9341_field *a, const ili9341_field *b)
{
    return a->x == b->x
        && a->y == b->y
        && a->size == b->size
        && a->colour == b->colour
        && a->length == b->length
        && memcmp(a->text, b->text, a->length) == 0;
}

// The two toggle pages share a value row. Fixed at eight characters so "ENABLED " paints over
// the whole of a previous "DISABLED".
static void add_enabled_disabled(ili9341_frame *out, bool enabled)
{
    add_field(out, centred(8, SIZE_TOGGLE), 130, SIZE_TOGGLE,
              enabled ? COLOUR_ON : COLOUR_OFF,
              enabled ? "ENABLED " : "DISABLED");
}

static void layout_session(const session_controller_to_display *state, ili9341_frame *out)
{
    char scratch[ILI9341_FIELD_TEXT_MAX];

    // Speed: label, big value, unit alongside.
    add_field(out, 12, 18, SIZE_SMALL, COLOUR_LABEL, "SPEED");

    uint32_t rpm = (uint32_t)roundf(state->rpm);
    snprintf(scratch, sizeof(scratch), "%5lu", (unsigned long)rpm);
    add_field(out, 12, 40, SIZE_VALUE, COLOUR_VALUE, scratch);

    add_field(out, 172, 64, SIZE_SMALL, COLOUR_LABEL, "rpm");

    // Force, the same shape one row down.
    add_field(out, 12, 100, SIZE_SMALL, COLOUR_LABEL, "FORCE");

    float force = roundf(state->force * 100.0f) / 100.0f;
    snprintf(scratch, sizeof(scratch), "%6.2f", (double)force);
    add_field(out, 12, 122, SIZE_VALUE, COLOUR_VALUE, scratch);

    add_field(out, 200, 146, SIZE_SMALL, COLOUR_LABEL, "N");

    // Drive mode. Which of the two appears is the menu option, not the live PID state: with the
    // option off there is nothing to arm, so what the encoder actually drives is shown instead.
    // Ten characters either way so one paints over the other.
    if (state->pid_option_toggleable)
    {
        add_field(out, 12, 196, SIZE_TITLE,
                  state->pid_enabled ? COLOUR_ON : COLOUR_OFF,
                  state->pid_enabled ? "PID ARMED " : "PID OFF   ");
    }
    else
    {
        uint8_t duty = (uint8_t)roundf(state->bpm_duty_cycle * 100.0f);
        snprintf(scratch, sizeof(scratch), "BRAKE %3u%%", duty);
        add_field(out, 12, 196, SIZE_TITLE, COLOUR_BRAKE, scratch);
    }
}

void ili9341_layout(const session_controller_to_display *state, ili9341_frame *out)
{
    memset(out, 0, sizeof(*out));

    char scratch[ILI9341_FIELD_TEXT_MAX];

    switch (state->screen)
    {
        case DISPLAY_SCREEN_IDLE:
            add_centred(out, 70, SIZE_HUGE, COLOUR_VALUE, "DYNO");
            add_centred(out, 150, SIZE_SMALL, COLOUR_LABEL, "PRESS SELECT");
            break;

        case DISPLAY_SCREEN_SD_LOGGING:
            add_centred(out, 60, SIZE_TITLE, COLOUR_LABEL, "SD LOGGING");
            add_enabled_disabled(out, state->sd_logging_enabled);
            break;

        case DISPLAY_SCREEN_PID_ENABLE:
            add_centred(out, 60, SIZE_TITLE, COLOUR_LABEL, "PID LOGGING");
            add_enabled_disabled(out, state->pid_option_toggleable);
            break;

        case DISPLAY_SCREEN_DESIRED_RPM:
            add_centred(out, 60, SIZE_TITLE, COLOUR_LABEL, "PID DES RPM");

            snprintf(scratch, sizeof(scratch), "%5lu", (unsigned long)state->desired_rpm);
            add_field(out, centred(5, SIZE_VALUE), 130, SIZE_VALUE, COLOUR_VALUE, scratch);
            break;

        // The same page with the step the encoder is about to apply, so the user can see which
        // digit a tick will move.
        case DISPLAY_SCREEN_DESIRED_RPM_EDIT:
            add_centred(out, 60, SIZE_TITLE, COLOUR_LABEL, "PID DES RPM");

            snprintf(scratch, sizeof(scratch), "%5lu", (unsigned long)state->desired_rpm);
            add_field(out, centred(5, SIZE_VALUE), 120, SIZE_VALUE, COLOUR_VALUE, scratch);

            snprintf(scratch, sizeof(scratch), "STEP %5lu",
                     (unsigned long)display_rpm_digit_increment(state->cursor_digit));
            add_field(out, centred(10, SIZE_SMALL), 185, SIZE_SMALL, COLOUR_BRAKE, scratch);
            break;

        case DISPLAY_SCREEN_SESSION:
            layout_session(state, out);
            break;

        default:
            break;
    }
}
