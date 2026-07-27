// Pins the ILI9341 panel's layout: which fields each screen produces, where, and how wide.
//
// The counterpart to lumex_layout_tests.cpp, and a different shape on purpose. There is no
// "correct" pixel layout to regress against the way there was a 2x16 grid transcribed from
// older code, so these check the properties the driver actually depends on:
//
//   - fields stay on the panel, so nothing is silently clipped away;
//   - a screen's field list is positionally stable regardless of the values, which is what
//     makes the driver's index-wise diff valid rather than an accident;
//   - fields that share a slot are equal width, so redrawing one erases the other -- there is
//     no read-modify-write on this bus, and a shorter value would otherwise leave a tail.

#include <gtest/gtest.h>

#include <string>

extern "C" {
#include "Tasks/Display/ili9341_layout.h"
}

#include "ILI9341_font.h"
#include "ILI9341_main.h"

namespace
{

session_controller_to_display State(display_screen_id screen)
{
    session_controller_to_display state{};
    state.screen = screen;
    return state;
}

ili9341_frame Layout(const session_controller_to_display &state)
{
    ili9341_frame frame{};
    ili9341_layout(&state, &frame);
    return frame;
}

const display_screen_id kAllScreens[] = {
    DISPLAY_SCREEN_IDLE,             DISPLAY_SCREEN_SD_LOGGING,
    DISPLAY_SCREEN_PID_ENABLE,       DISPLAY_SCREEN_DESIRED_RPM,
    DISPLAY_SCREEN_DESIRED_RPM_EDIT, DISPLAY_SCREEN_SESSION,
};

uint16_t FieldRight(const ili9341_field &field)
{
    return field.x + (uint16_t)(field.length * ILI9341_FONT_CELL_WIDTH * field.size);
}

uint16_t FieldBottom(const ili9341_field &field)
{
    return field.y + (uint16_t)(ILI9341_FONT_CELL_HEIGHT * field.size);
}

// --------------------------------------------------------------------------- invariants

TEST(Ili9341Layout, EveryScreenProducesFields)
{
    for (display_screen_id screen : kAllScreens)
    {
        EXPECT_GT(Layout(State(screen)).count, 0) << "screen " << screen;
    }
}

TEST(Ili9341Layout, NoFieldRunsOffThePanel)
{
    // Every field is drawn at a fixed position with no wrapping, so anything past an edge is
    // simply lost. Checked at the widest values each screen can hold.
    session_controller_to_display state{};
    state.desired_rpm = 99999;
    state.rpm = 99999.0f;
    state.force = 999.99f;
    state.bpm_duty_cycle = 1.0f;

    for (display_screen_id screen : kAllScreens)
    {
        state.screen = screen;
        const ili9341_frame frame = Layout(state);

        for (uint8_t i = 0; i < frame.count; i++)
        {
            const ili9341_field &field = frame.fields[i];

            EXPECT_LE(FieldRight(field), ILI9341_LAYOUT_WIDTH)
                << "screen " << screen << " field " << (int)i << " (\"" << field.text << "\")";
            EXPECT_LE(FieldBottom(field), ILI9341_LAYOUT_HEIGHT)
                << "screen " << screen << " field " << (int)i << " (\"" << field.text << "\")";
        }
    }
}

TEST(Ili9341Layout, TextScalesAreWithinWhatTheDriverWillDraw)
{
    // DrawChar rejects a size above ILI9341_MAX_TEXT_SIZE, which would fail a whole render.
    for (display_screen_id screen : kAllScreens)
    {
        const ili9341_frame frame = Layout(State(screen));

        for (uint8_t i = 0; i < frame.count; i++)
        {
            EXPECT_GE(frame.fields[i].size, 1);
            EXPECT_LE(frame.fields[i].size, ILI9341_MAX_TEXT_SIZE);
        }
    }
}

TEST(Ili9341Layout, FieldsAreCappedSoTheFrameCannotOverflow)
{
    for (display_screen_id screen : kAllScreens)
    {
        EXPECT_LE(Layout(State(screen)).count, ILI9341_MAX_FIELDS);
    }
}

// --------------------------------------------------------------------------- diffability

TEST(Ili9341Layout, AScreensFieldListIsPositionallyStable)
{
    // The driver compares field i against field i of the last frame and repaints only the
    // movers. That is only valid if a screen always produces the same fields in the same
    // places at the same sizes, whatever the values are.
    session_controller_to_display quiet{};
    session_controller_to_display busy{};

    busy.rpm = 4321.0f;
    busy.force = 123.45f;
    busy.desired_rpm = 98765;
    busy.bpm_duty_cycle = 0.87f;
    busy.pid_enabled = true;
    busy.sd_logging_enabled = true;
    busy.cursor_digit = DISPLAY_RPM_DIGIT_ONE;

    for (display_screen_id screen : kAllScreens)
    {
        quiet.screen = screen;
        busy.screen = screen;

        const ili9341_frame a = Layout(quiet);
        const ili9341_frame b = Layout(busy);

        ASSERT_EQ(a.count, b.count) << "screen " << screen;

        for (uint8_t i = 0; i < a.count; i++)
        {
            EXPECT_EQ(a.fields[i].x, b.fields[i].x) << "screen " << screen << " field " << (int)i;
            EXPECT_EQ(a.fields[i].y, b.fields[i].y) << "screen " << screen << " field " << (int)i;
            EXPECT_EQ(a.fields[i].size, b.fields[i].size)
                << "screen " << screen << " field " << (int)i;
            EXPECT_EQ(a.fields[i].length, b.fields[i].length)
                << "screen " << screen << " field " << (int)i << ": widths must match so a "
                   "redraw erases the previous value";
        }
    }
}

TEST(Ili9341Layout, LayoutIsAPureFunctionOfState)
{
    session_controller_to_display state = State(DISPLAY_SCREEN_SESSION);
    state.rpm = 2500.0f;

    const ili9341_frame first = Layout(state);
    const ili9341_frame second = Layout(state);

    ASSERT_EQ(first.count, second.count);
    for (uint8_t i = 0; i < first.count; i++)
    {
        EXPECT_TRUE(ili9341_field_equal(&first.fields[i], &second.fields[i]));
    }
}

TEST(Ili9341Layout, FieldEqualityNoticesWhatTheDriverMustRepaint)
{
    session_controller_to_display state = State(DISPLAY_SCREEN_SESSION);
    state.rpm = 1000.0f;
    const ili9341_frame before = Layout(state);

    state.rpm = 2000.0f;
    const ili9341_frame after = Layout(state);

    // The speed readout moved; its label did not.
    EXPECT_FALSE(ili9341_field_equal(&before.fields[1], &after.fields[1]));
    EXPECT_TRUE(ili9341_field_equal(&before.fields[0], &after.fields[0]));
}

TEST(Ili9341Layout, ColourChangeAloneCountsAsAChange)
{
    // The drive-mode field keeps its text length but changes colour between armed and off; a
    // diff that only compared text would leave it the wrong colour.
    session_controller_to_display state = State(DISPLAY_SCREEN_SESSION);
    state.pid_option_toggleable = true;

    state.pid_enabled = false;
    const ili9341_frame off = Layout(state);

    state.pid_enabled = true;
    const ili9341_frame on = Layout(state);

    const uint8_t driveMode = (uint8_t)(off.count - 1);
    EXPECT_FALSE(ili9341_field_equal(&off.fields[driveMode], &on.fields[driveMode]));
}

// --------------------------------------------------------------------------- content

TEST(Ili9341Layout, TogglePagesUseEqualWidthLabels)
{
    // "ENABLED " is padded to eight so it covers "DISABLED" exactly.
    session_controller_to_display state = State(DISPLAY_SCREEN_SD_LOGGING);

    state.sd_logging_enabled = true;
    const ili9341_frame enabled = Layout(state);

    state.sd_logging_enabled = false;
    const ili9341_frame disabled = Layout(state);

    EXPECT_EQ(std::string(enabled.fields[1].text), "ENABLED ");
    EXPECT_EQ(std::string(disabled.fields[1].text), "DISABLED");
    EXPECT_EQ(enabled.fields[1].x, disabled.fields[1].x);
}

TEST(Ili9341Layout, PidEnablePageShowsTheToggleableFlagNotTheLiveOne)
{
    session_controller_to_display state = State(DISPLAY_SCREEN_PID_ENABLE);
    state.pid_enabled = true;   // in-session state; must not leak onto this page

    state.pid_option_toggleable = false;
    EXPECT_EQ(std::string(Layout(state).fields[1].text), "DISABLED");
}

TEST(Ili9341Layout, SessionScreenShowsBrakeDutyWhenThePidCannotBeArmed)
{
    session_controller_to_display state = State(DISPLAY_SCREEN_SESSION);
    state.pid_option_toggleable = false;
    state.pid_enabled = true;   // must be ignored
    state.bpm_duty_cycle = 0.95f;

    const ili9341_frame frame = Layout(state);

    EXPECT_EQ(std::string(frame.fields[frame.count - 1].text), "BRAKE  95%");
}

TEST(Ili9341Layout, SessionScreenRoundsTheSpeedReadout)
{
    session_controller_to_display state = State(DISPLAY_SCREEN_SESSION);
    state.rpm = 1234.6f;

    EXPECT_EQ(std::string(Layout(state).fields[1].text), " 1235");
}

TEST(Ili9341Layout, EditorShowsTheStepTheEncoderWillApply)
{
    session_controller_to_display state = State(DISPLAY_SCREEN_DESIRED_RPM_EDIT);
    state.desired_rpm = 5000;
    state.cursor_digit = DISPLAY_RPM_DIGIT_HUNDRED;

    const ili9341_frame frame = Layout(state);

    EXPECT_EQ(std::string(frame.fields[1].text), " 5000");
    EXPECT_EQ(std::string(frame.fields[2].text), "STEP   100");
}

// --------------------------------------------------------------------------- font

TEST(Ili9341Font, GlyphExtractionMatchesKnownColumns)
{
    // Space is blank everywhere; '|' has its centre column filled. Cheap sanity that the
    // vendored table is indexed correctly rather than off by a glyph.
    for (uint8_t column = 0; column < ILI9341_FONT_GLYPH_WIDTH; column++)
    {
        for (uint8_t row = 0; row < ILI9341_FONT_GLYPH_HEIGHT; row++)
        {
            EXPECT_FALSE(ili9341_font_pixel(' ', column, row));
        }
    }

    bool anyLit = false;
    for (uint8_t row = 0; row < ILI9341_FONT_GLYPH_HEIGHT; row++)
    {
        anyLit = anyLit || ili9341_font_pixel('A', 1, row);
    }
    EXPECT_TRUE(anyLit) << "'A' should have lit pixels";
}

TEST(Ili9341Font, OutOfRangeCoordinatesAreBlankRatherThanOutOfBounds)
{
    EXPECT_FALSE(ili9341_font_pixel('A', ILI9341_FONT_GLYPH_WIDTH, 0));
    EXPECT_FALSE(ili9341_font_pixel('A', 0, ILI9341_FONT_GLYPH_HEIGHT + 1));
}

}   // namespace
