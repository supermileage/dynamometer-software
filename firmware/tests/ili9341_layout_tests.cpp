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
#include "Tasks/Display/ILI9341/ili9341_layout.h"
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

ili9341_frame Layout(const session_controller_to_display &state,
                     const ili9341_session_detail &detail = {})
{
    ili9341_frame frame{};
    ili9341_layout(&state, &detail, &frame);
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

// ------------------------------------------------- extended session detail (ILI9341 only)

// These three readouts exist on this panel and not on the character LCD, which discards them
// through its one-line DisplayDriver stubs. The widths matter as much as the values: the
// driver's field-by-field diff assumes a screen's field widths never move, so a reading that
// outgrew its format would shift its neighbours and strand the old pixels.
namespace
{

ili9341_session_detail Detail(float accel, float peak, uint32_t seconds)
{
    ili9341_session_detail detail{};
    detail.angular_acceleration = accel;
    detail.peak_force = peak;
    detail.session_seconds = seconds;
    return detail;
}

const ili9341_field &DetailField(const ili9341_frame &frame, int which)
{
    // The three detail fields sit between the "N" unit and the drive mode.
    return frame.fields[6 + which];
}

TEST(Ili9341SessionDetail, ShowsAccelerationPeakForceAndElapsedTime)
{
    const ili9341_frame frame =
        Layout(State(DISPLAY_SCREEN_SESSION), Detail(-42.0f, 123.45f, 87));

    EXPECT_EQ(std::string(DetailField(frame, 0).text), "A   -42");
    EXPECT_EQ(std::string(DetailField(frame, 1).text), "P123.45");
    EXPECT_EQ(std::string(DetailField(frame, 2).text), "T  87s");
}

// The same invariant for the two primary readouts. They are the ones the rig actually drives to
// extremes -- braking hard is what sends force up -- and unlike the detail row they sit beside a
// unit label, so a field that grows a character paints straight over it and the tail is stranded
// when the reading comes back down.
TEST(Ili9341Session, PrimaryReadoutWidthsDoNotMoveWithTheValues)
{
    session_controller_to_display quiet = State(DISPLAY_SCREEN_SESSION);
    quiet.rpm = 0.0f;
    quiet.force = 0.0f;

    session_controller_to_display extreme = State(DISPLAY_SCREEN_SESSION);
    extreme.rpm = 1e9f;
    extreme.force = 1e9f;

    const ili9341_frame small = Layout(quiet);
    const ili9341_frame large = Layout(extreme);

    ASSERT_EQ(small.count, large.count);

    // Field 1 is the rpm value, field 4 the force value.
    for (int i : {1, 4})
    {
        EXPECT_EQ(small.fields[i].length, large.fields[i].length)
            << "primary field " << i << " changed width: \"" << small.fields[i].text
            << "\" vs \"" << large.fields[i].text << "\"";
    }
}

// A negative reading must stay inside the format too. The load cell can sit slightly below zero
// on offset alone, and (uint32_t)roundf() of a negative float is undefined -- in practice it
// wraps to a ten-digit number that swamps the row.
TEST(Ili9341Session, NegativeReadingsStayInsideTheirFormat)
{
    session_controller_to_display quiet = State(DISPLAY_SCREEN_SESSION);

    session_controller_to_display negative = State(DISPLAY_SCREEN_SESSION);
    negative.rpm = -5.0f;
    negative.force = -1e9f;

    const ili9341_frame small = Layout(quiet);
    const ili9341_frame large = Layout(negative);

    for (int i : {1, 4})
    {
        EXPECT_EQ(small.fields[i].length, large.fields[i].length)
            << "primary field " << i << " changed width: \"" << small.fields[i].text
            << "\" vs \"" << large.fields[i].text << "\"";
    }
}

TEST(Ili9341SessionDetail, WidthsDoNotMoveWithTheValues)
{
    const ili9341_frame small = Layout(State(DISPLAY_SCREEN_SESSION), Detail(0.0f, 0.0f, 0));
    const ili9341_frame large =
        Layout(State(DISPLAY_SCREEN_SESSION), Detail(1e9f, 1e9f, 4000000000u));

    ASSERT_EQ(small.count, large.count);

    for (int i = 0; i < 3; i++)
    {
        EXPECT_EQ(DetailField(small, i).length, DetailField(large, i).length)
            << "detail field " << i << " changed width: \"" << DetailField(small, i).text
            << "\" vs \"" << DetailField(large, i).text << "\"";
        EXPECT_EQ(DetailField(small, i).x, DetailField(large, i).x);
    }
}

TEST(Ili9341SessionDetail, ClampsRatherThanOverflowingItsField)
{
    const ili9341_frame frame =
        Layout(State(DISPLAY_SCREEN_SESSION), Detail(1e9f, 1e9f, 4000000000u));

    EXPECT_EQ(std::string(DetailField(frame, 0).text), "A 99999");
    EXPECT_EQ(std::string(DetailField(frame, 1).text), "P999.99");
    EXPECT_EQ(std::string(DetailField(frame, 2).text), "T9999s");
}

TEST(Ili9341SessionDetail, OnlyAppearsOnTheSessionScreen)
{
    // Every other screen ignores the detail entirely, so a stale peak or clock cannot leak onto
    // the idle or settings pages.
    const ili9341_session_detail busy = Detail(999.0f, 500.0f, 1234);

    for (display_screen_id screen : kAllScreens)
    {
        if (screen == DISPLAY_SCREEN_SESSION) continue;

        EXPECT_EQ(Layout(State(screen)).count, Layout(State(screen), busy).count)
            << "screen " << screen << " changed with session detail";
    }
}

TEST(Ili9341SessionDetail, DetailChangesAreVisibleToTheDiff)
{
    const ili9341_frame before = Layout(State(DISPLAY_SCREEN_SESSION), Detail(10.0f, 1.0f, 5));
    const ili9341_frame after  = Layout(State(DISPLAY_SCREEN_SESSION), Detail(20.0f, 1.0f, 5));

    EXPECT_FALSE(ili9341_field_equal(&DetailField(before, 0), &DetailField(after, 0)));
    EXPECT_TRUE(ili9341_field_equal(&DetailField(before, 1), &DetailField(after, 1)));
}

}   // namespace
