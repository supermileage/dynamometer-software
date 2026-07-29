// Pins the Lumex panel's rendering, cell for cell.
//
// The layout used to live in FiniteStateMachine.cpp as runs of WriteText(row, column, "...")
// with hand-counted padding. Most expectations below are those literals transcribed by hand,
// so the move behind a screen-state message cannot quietly change what a user sees.
//
// The session screen's row 1 is the exception, and deliberately so: the old label literal
// carried a "0.00" at columns 6-9 while the force field wrote columns 2-7, leaving two digits
// stranded (12.34 N read as "12.3400"). That row is pinned to the corrected layout.
//
// Written as whole 16-character rows rather than as field assertions, because the bugs worth
// catching here are off-by-one column errors that a field-level check would step over.

#include <gtest/gtest.h>

#include <string>

extern "C" {
#include "Tasks/Display/display_common.h"
#include "Tasks/Display/Lumex/lumex_layout.h"
}

namespace
{

std::string Row(const lumex_frame &frame, unsigned row)
{
    return std::string(frame.cells[row], LUMEX_LCD_COLUMNS);
}

session_controller_to_display State(display_screen_id screen)
{
    session_controller_to_display state{};
    state.screen = screen;
    return state;
}

lumex_frame Render(const session_controller_to_display &state)
{
    lumex_frame frame{};
    lumex_render(&state, &frame);
    return frame;
}

// --------------------------------------------------------------------------- frame invariants

TEST(LumexLayout, EveryScreenFillsEveryCell)
{
    // No cell is left uninitialised: the driver diffs whole frames, so a stray NUL would be a
    // difference it tried to write to the panel.
    const display_screen_id screens[] = {
        DISPLAY_SCREEN_IDLE,       DISPLAY_SCREEN_PID_ENABLE,
        DISPLAY_SCREEN_DESIRED_RPM, DISPLAY_SCREEN_DESIRED_RPM_EDIT,
        DISPLAY_SCREEN_SESSION,
    };

    for (display_screen_id screen : screens)
    {
        const lumex_frame frame = Render(State(screen));

        for (unsigned row = 0; row < LUMEX_LCD_ROWS; row++)
        {
            for (unsigned column = 0; column < LUMEX_LCD_COLUMNS; column++)
            {
                EXPECT_GE(frame.cells[row][column], ' ')
                    << "screen " << screen << " cell (" << row << ", " << column << ")";
            }
        }
    }
}

TEST(LumexLayout, RenderingIsAPureFunctionOfState)
{
    // The driver relies on this: it renders, diffs against the last frame, and trusts that an
    // unchanged state produces an unchanged frame.
    session_controller_to_display state = State(DISPLAY_SCREEN_SESSION);
    state.rpm = 123.4f;
    state.force = 56.78f;

    const lumex_frame first = Render(state);
    const lumex_frame second = Render(state);

    EXPECT_EQ(Row(first, 0), Row(second, 0));
    EXPECT_EQ(Row(first, 1), Row(second, 1));
}

// --------------------------------------------------------------------------- idle

TEST(LumexLayout, IdleScreen)
{
    const lumex_frame frame = Render(State(DISPLAY_SCREEN_IDLE));

    //                       0123456789012345
    EXPECT_EQ(Row(frame, 0), "      DYNO      ");
    EXPECT_EQ(Row(frame, 1), "  PRESS SELECT  ");
}

// --------------------------------------------------------------------------- settings pages

TEST(LumexLayout, PidEnablePageShowsTheToggleableFlagNotTheLiveOne)
{
    // The page is about whether the option may be armed at all, so it reads
    // pid_option_toggleable; pid_enabled is the in-session state and must not leak in here.
    session_controller_to_display state = State(DISPLAY_SCREEN_PID_ENABLE);
    state.pid_enabled = true;

    state.pid_option_toggleable = false;
    //                                      0123456789012345
    EXPECT_EQ(Row(Render(state), 0), "  PID CONTROL   ");
    EXPECT_EQ(Row(Render(state), 1), "    DISABLED    ");

    state.pid_option_toggleable = true;
    EXPECT_EQ(Row(Render(state), 1), "    ENABLED     ");
}

TEST(LumexLayout, DesiredRpmPage)
{
    session_controller_to_display state = State(DISPLAY_SCREEN_DESIRED_RPM);
    state.desired_rpm = 5000;

    //                                      0123456789012345
    EXPECT_EQ(Row(Render(state), 0), "  PID DES RPM   ");
    EXPECT_EQ(Row(Render(state), 1), "      5000      ");
}

TEST(LumexLayout, DesiredRpmPagePadsToFiveColumns)
{
    session_controller_to_display state = State(DISPLAY_SCREEN_DESIRED_RPM);

    state.desired_rpm = 0;
    //                                      0123456789012345
    EXPECT_EQ(Row(Render(state), 1), "         0      ");

    state.desired_rpm = 99999;
    EXPECT_EQ(Row(Render(state), 1), "     99999      ");
}

TEST(LumexLayout, DesiredRpmEditorShowsTheStepBesideTheValue)
{
    session_controller_to_display state = State(DISPLAY_SCREEN_DESIRED_RPM_EDIT);
    state.desired_rpm = 5000;
    state.cursor_digit = DISPLAY_RPM_DIGIT_HUNDRED;

    //                                      0123456789012345
    EXPECT_EQ(Row(Render(state), 0), "  PID DES RPM   ");
    EXPECT_EQ(Row(Render(state), 1), "   5000   100   ");
}

TEST(DisplayCommon, EveryCursorPositionMapsToItsStep)
{
    EXPECT_EQ(display_rpm_digit_increment(DISPLAY_RPM_DIGIT_TEN_THOUSAND), 10000u);
    EXPECT_EQ(display_rpm_digit_increment(DISPLAY_RPM_DIGIT_THOUSAND), 1000u);
    EXPECT_EQ(display_rpm_digit_increment(DISPLAY_RPM_DIGIT_HUNDRED), 100u);
    EXPECT_EQ(display_rpm_digit_increment(DISPLAY_RPM_DIGIT_TEN), 10u);
    EXPECT_EQ(display_rpm_digit_increment(DISPLAY_RPM_DIGIT_ONE), 1u);
}

// --------------------------------------------------------------------------- session

TEST(LumexLayout, SessionScreenAtRest)
{
    session_controller_to_display state = State(DISPLAY_SCREEN_SESSION);
    state.pid_option_toggleable = false;

    //                                      0123456789012345
    EXPECT_EQ(Row(Render(state), 0), "n:     0 rpm    ");
    EXPECT_EQ(Row(Render(state), 1), "F:  0.00 N  B  0");
}

TEST(LumexLayout, SessionScreenRoundsAndRightAlignsTheRpmField)
{
    session_controller_to_display state = State(DISPLAY_SCREEN_SESSION);

    state.rpm = 1234.6f;
    //                                      0123456789012345
    EXPECT_EQ(Row(Render(state), 0), "n:  1235 rpm    ");

    state.rpm = 7.0f;
    EXPECT_EQ(Row(Render(state), 0), "n:     7 rpm    ");
}

TEST(LumexLayout, SessionScreenForceFieldEndsWhereItsUnitBegins)
{
    // Regression: the label literal used to carry a "0.00" of its own at columns 6-9 while the
    // force field wrote columns 2-7, stranding two of its digits on screen -- 12.34 N read as
    // "12.3400". The literal now holds labels and units only.
    session_controller_to_display state = State(DISPLAY_SCREEN_SESSION);
    state.pid_option_toggleable = false;
    state.force = 12.34f;

    //                                      0123456789012345
    EXPECT_EQ(Row(Render(state), 1), "F: 12.34 N  B  0");
}

TEST(LumexLayout, SessionScreenForceFieldStaysClearOfTheDriveModeField)
{
    // The widest reading the six-character field can hold must not reach column 12.
    session_controller_to_display state = State(DISPLAY_SCREEN_SESSION);
    state.pid_option_toggleable = false;
    state.force = 999.99f;

    //                                      0123456789012345
    EXPECT_EQ(Row(Render(state), 1), "F:999.99 N  B  0");
}

TEST(LumexLayout, SessionScreenShowsPidStateWhenTheOptionIsArmable)
{
    session_controller_to_display state = State(DISPLAY_SCREEN_SESSION);
    state.pid_option_toggleable = true;

    state.pid_enabled = true;
    EXPECT_EQ(Row(Render(state), 1).substr(12, 4), "PIDE");

    state.pid_enabled = false;
    EXPECT_EQ(Row(Render(state), 1).substr(12, 4), "PIDD");
}

TEST(LumexLayout, SessionScreenShowsBrakeDutyWhenTheOptionIsNot)
{
    // With the option off the PID cannot be armed, so the cell shows what the encoder is
    // actually driving -- the brake -- as a whole-percent B-prefixed field.
    session_controller_to_display state = State(DISPLAY_SCREEN_SESSION);
    state.pid_option_toggleable = false;
    state.pid_enabled = true;   // must be ignored

    state.bpm_duty_cycle = 0.0f;
    EXPECT_EQ(Row(Render(state), 1).substr(12, 4), "B  0");

    state.bpm_duty_cycle = 0.07f;
    EXPECT_EQ(Row(Render(state), 1).substr(12, 4), "B  7");

    state.bpm_duty_cycle = 0.95f;
    EXPECT_EQ(Row(Render(state), 1).substr(12, 4), "B 95");
}

}   // namespace

// --------------------------------------------------------- fixed-point force formatting

// display_format_fixed2 replaced snprintf("%6.2f"). That call was the only floating-point
// conversion in the firmware and it overflowed the display task's 1 KB stack on the session
// screen -- newlib's float formatter needs ~400 bytes on top of the ~180 the render path
// already used, and the overflow hook disables interrupts and spins, so the board looked dead
// the moment the brake button was pressed.
//
// It was reintroduced by the revert in dacdd1f and is removed again here, this time from both
// the force reading and the peak-force readout that was added after the original fix.
//
// These pin the replacement against what %6.2f produced, so the fix cannot quietly change the
// reading. Expectations are what printf gives for the same inputs.
namespace
{

std::string Fixed2(float value, int width = 6)
{
    char buffer[32];
    display_format_fixed2(buffer, sizeof(buffer), value, width);
    return std::string(buffer);
}

TEST(DisplayFormatFixed2, MatchesPrintfForOrdinaryValues)
{
    EXPECT_EQ(Fixed2(0.0f),      "  0.00");
    EXPECT_EQ(Fixed2(12.34f),    " 12.34");
    EXPECT_EQ(Fixed2(1.5f),      "  1.50");
    EXPECT_EQ(Fixed2(999.99f),   "999.99");
    EXPECT_EQ(Fixed2(100.0f),    "100.00");
}

TEST(DisplayFormatFixed2, RoundsHalfAwayFromZeroLikePrintf)
{
    EXPECT_EQ(Fixed2(1.005f),  "  1.01");
    EXPECT_EQ(Fixed2(1.004f),  "  1.00");
    EXPECT_EQ(Fixed2(-1.005f), " -1.01");
}

TEST(DisplayFormatFixed2, KeepsTheSignWhenTheWholePartIsZero)
{
    // Truncating toward zero makes the whole part 0 for these, so the sign has to be put back
    // by hand -- "-0.50" must not come out as "0.50".
    EXPECT_EQ(Fixed2(-0.5f),  " -0.50");
    EXPECT_EQ(Fixed2(-0.01f), " -0.01");
    EXPECT_EQ(Fixed2(-0.99f), " -0.99");
}

TEST(DisplayFormatFixed2, HandlesNegativesGenerally)
{
    EXPECT_EQ(Fixed2(-1.5f),   " -1.50");
    EXPECT_EQ(Fixed2(-12.34f), "-12.34");
}

TEST(DisplayFormatFixed2, DoesNotTruncateOversizedValues)
{
    // printf lets a value wider than the field push past it rather than clipping; the callers
    // clip to their own field width afterwards.
    EXPECT_EQ(Fixed2(12345.67f), "12345.67");
}

TEST(DisplayFormatFixed2, RespectsTheRequestedWidth)
{
    EXPECT_EQ(Fixed2(1.5f, 8), "    1.50");
    EXPECT_EQ(Fixed2(1.5f, 4), "1.50");
}

}   // namespace
