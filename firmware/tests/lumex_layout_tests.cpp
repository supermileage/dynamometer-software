// Pins the Lumex panel's rendering, cell for cell.
//
// The layout used to live in FiniteStateMachine.cpp as runs of WriteText(row, column, "...")
// with hand-counted padding. Moving it behind a screen-state message is a refactor, so the
// expectations below are the literals that code wrote, transcribed by hand from it -- these
// tests exist to catch the move changing what a user sees.
//
// Written as whole 16-character rows rather than as field assertions, because the bugs worth
// catching here are off-by-one column errors that a field-level check would step over.

#include <gtest/gtest.h>

#include <string>

extern "C" {
#include "Tasks/LCD/lumex_layout.h"
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
        DISPLAY_SCREEN_IDLE,      DISPLAY_SCREEN_SD_LOGGING,
        DISPLAY_SCREEN_PID_ENABLE, DISPLAY_SCREEN_DESIRED_RPM,
        DISPLAY_SCREEN_DESIRED_RPM_EDIT, DISPLAY_SCREEN_SESSION,
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
    state.angular_velocity = 123.4f;
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

TEST(LumexLayout, SdLoggingPageShowsItsOwnFlag)
{
    session_controller_to_display state = State(DISPLAY_SCREEN_SD_LOGGING);

    state.sd_logging_enabled = false;
    //                                      0123456789012345
    EXPECT_EQ(Row(Render(state), 0), "   SD LOGGING   ");
    EXPECT_EQ(Row(Render(state), 1), "    DISABLED    ");

    state.sd_logging_enabled = true;
    EXPECT_EQ(Row(Render(state), 1), "    ENABLED     ");
}

TEST(LumexLayout, PidEnablePageShowsTheToggleableFlagNotTheLiveOne)
{
    // The page is about whether the option may be armed at all, so it reads
    // pid_option_toggleable; pid_enabled is the in-session state and must not leak in here.
    session_controller_to_display state = State(DISPLAY_SCREEN_PID_ENABLE);
    state.pid_enabled = true;

    state.pid_option_toggleable = false;
    //                                      0123456789012345
    EXPECT_EQ(Row(Render(state), 0), "  PID LOGGING   ");
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

TEST(LumexLayout, EveryCursorPositionMapsToItsStep)
{
    EXPECT_EQ(lumex_rpm_digit_increment(DISPLAY_RPM_DIGIT_TEN_THOUSAND), 10000u);
    EXPECT_EQ(lumex_rpm_digit_increment(DISPLAY_RPM_DIGIT_THOUSAND), 1000u);
    EXPECT_EQ(lumex_rpm_digit_increment(DISPLAY_RPM_DIGIT_HUNDRED), 100u);
    EXPECT_EQ(lumex_rpm_digit_increment(DISPLAY_RPM_DIGIT_TEN), 10u);
    EXPECT_EQ(lumex_rpm_digit_increment(DISPLAY_RPM_DIGIT_ONE), 1u);
}

// --------------------------------------------------------------------------- session

TEST(LumexLayout, SessionScreenAtRest)
{
    session_controller_to_display state = State(DISPLAY_SCREEN_SESSION);
    state.pid_option_toggleable = false;

    //                                      0123456789012345
    EXPECT_EQ(Row(Render(state), 0), "n:     0 rpm    ");
}

TEST(LumexLayout, SessionScreenRoundsAndRightAlignsTheRpmField)
{
    session_controller_to_display state = State(DISPLAY_SCREEN_SESSION);

    state.angular_velocity = 1234.6f;
    //                                      0123456789012345
    EXPECT_EQ(Row(Render(state), 0), "n:  1235 rpm    ");

    state.angular_velocity = 7.0f;
    EXPECT_EQ(Row(Render(state), 0), "n:     7 rpm    ");
}

TEST(LumexLayout, SessionScreenForceFieldLeavesStaleDigits)
{
    // Pins a pre-existing artifact rather than blessing it. The label literal carries "0.00"
    // at columns 6-9, but the force field is six characters at columns 2-7 -- so columns 8-9
    // keep a "00" that nothing ever rewrites, and 12.34 N reads as "12.3400". Reproduced here
    // exactly because this commit moves the layout without changing it; the fix is to shorten
    // the literal in lumex_layout.c, which is a display change and its own decision.
    session_controller_to_display state = State(DISPLAY_SCREEN_SESSION);
    state.pid_option_toggleable = false;
    state.force = 12.34f;

    //                                      0123456789012345
    EXPECT_EQ(Row(Render(state), 1), "F: 12.3400 NB  0");
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
