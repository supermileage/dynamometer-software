#include "Tasks/LCD/lumex_layout.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

// Writes `n` characters at (row, column), dropping anything past the last column. The old
// LumexLCD::DisplayString clamped the same way -- deliberately, so an overlong field fails
// visibly in its own cells rather than wrapping onto the other row.
//
// `n` is always the field's width, never strlen: the FSM's WriteText took its length from the
// array (`N - 1`), so a snprintf that truncated still wrote a full-width field. Keeping that
// exact is what makes a truncated reading occupy the same cells it always did.
static void put(lumex_frame *out, unsigned row, unsigned column, const char *text, size_t n)
{
    for (size_t i = 0; i < n && (column + i) < LUMEX_LCD_COLUMNS; i++)
    {
        out->cells[row][column + i] = text[i];
    }
}

#define PUT_LITERAL(out, row, column, literal) \
    put((out), (row), (column), (literal), sizeof(literal) - 1)

// Formats into a scratch buffer and writes exactly `width` characters, so an over-wide value
// occupies its field and no more -- the same clipping the old code got implicitly from
// WriteText taking its length from a just-big-enough array, but without asking snprintf to
// truncate (which -Wformat-truncation rightly flags, since there it was load-bearing).
#define SCRATCH_SIZE 32

static void put_field(lumex_frame *out, unsigned row, unsigned column, size_t width,
                      const char *scratch)
{
    put(out, row, column, scratch, width);
}

uint32_t lumex_rpm_digit_increment(display_rpm_digit digit)
{
    switch (digit)
    {
        case DISPLAY_RPM_DIGIT_TEN_THOUSAND: return 10000;
        case DISPLAY_RPM_DIGIT_THOUSAND:     return 1000;
        case DISPLAY_RPM_DIGIT_HUNDRED:      return 100;
        case DISPLAY_RPM_DIGIT_TEN:          return 10;
        case DISPLAY_RPM_DIGIT_ONE:          return 1;
        default:                             return 0;
    }
}

// The second row shared by both toggle pages.
static void render_enabled_disabled(lumex_frame *out, bool enabled)
{
    if (enabled) PUT_LITERAL(out, 1, 4, "ENABLED");
    else         PUT_LITERAL(out, 1, 4, "DISABLED");
}

static void render_session(const session_controller_to_display *state, lumex_frame *out)
{
    // Labels and units only. The cells each live field occupies are left blank and filled in
    // below, so a unit always sits just past where its value ends. The row 1 literal used to
    // carry a "0.00" of its own at cols 6-9 while the force field wrote cols 2-7, which left
    // two digits of it stranded on screen: 12.34 N read as "12.3400".
    //
    //                        col: 0123456789012345
    PUT_LITERAL(out, 0, 0, "n:       rpm    ");
    PUT_LITERAL(out, 1, 0, "F:       N      ");

    char scratch[SCRATCH_SIZE];

    uint32_t rpm = (uint32_t)roundf(state->rpm);
    snprintf(scratch, sizeof(scratch), "%5lu", (unsigned long)rpm);
    put_field(out, 0, 3, 5, scratch);

    // Six characters at cols 2-7, clear of the "F:" label and of the drive-mode field at
    // col 12 however large the reading gets.
    float force = roundf(state->force * 100.0f) / 100.0f;
    snprintf(scratch, sizeof(scratch), "%6.2f", (double)force);
    put_field(out, 1, 2, 6, scratch);

    // The drive-mode field. Which of the two appears is the menu option, not the live PID
    // state -- with the option off there is nothing to arm, so the brake command is shown.
    if (state->pid_option_toggleable)
    {
        if (state->pid_enabled) PUT_LITERAL(out, 1, 12, "PIDE");
        else                    PUT_LITERAL(out, 1, 12, "PIDD");
    }
    else
    {
        uint8_t duty = (uint8_t)roundf(state->bpm_duty_cycle * 100.0f);
        snprintf(scratch, sizeof(scratch), "B%3u", duty);
        put_field(out, 1, 12, 4, scratch);
    }
}

void lumex_render(const session_controller_to_display *state, lumex_frame *out)
{
    // Start blank. Every screen used to open with an explicit ClearDisplay, so a cell no screen
    // writes is a space; rendering the whole grid every time is what lets the driver diff.
    memset(out->cells, ' ', sizeof(out->cells));

    switch (state->screen)
    {
        case DISPLAY_SCREEN_IDLE:
            PUT_LITERAL(out, 0, 6, "DYNO");
            PUT_LITERAL(out, 1, 2, "PRESS SELECT");
            break;

        case DISPLAY_SCREEN_SD_LOGGING:
            PUT_LITERAL(out, 0, 3, "SD LOGGING");
            render_enabled_disabled(out, state->sd_logging_enabled);
            break;

        case DISPLAY_SCREEN_PID_ENABLE:
            PUT_LITERAL(out, 0, 2, "PID LOGGING");
            render_enabled_disabled(out, state->pid_option_toggleable);
            break;

        case DISPLAY_SCREEN_DESIRED_RPM:
        {
            PUT_LITERAL(out, 0, 2, "PID DES RPM");

            char scratch[SCRATCH_SIZE];
            snprintf(scratch, sizeof(scratch), "%5lu", (unsigned long)state->desired_rpm);
            put_field(out, 1, 5, 5, scratch);
            break;
        }

        // The same page with the cursor's step size alongside the value, so the user can see
        // which digit a tick will move.
        case DISPLAY_SCREEN_DESIRED_RPM_EDIT:
        {
            PUT_LITERAL(out, 0, 2, "PID DES RPM");

            char scratch[SCRATCH_SIZE];
            snprintf(scratch, sizeof(scratch), "%5lu %5lu",
                     (unsigned long)state->desired_rpm,
                     (unsigned long)lumex_rpm_digit_increment(state->cursor_digit));
            put_field(out, 1, 2, 11, scratch);
            break;
        }

        case DISPLAY_SCREEN_SESSION:
            render_session(state, out);
            break;

        default:
            break;
    }
}
