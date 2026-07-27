#include <gtest/gtest.h>

#include <bit>
#include <cmath>
#include <cstdint>

#include "Config/config.h"
#include "Config/sysconfig.h"

namespace
{

uint32_t Bits(float value)
{
    return std::bit_cast<uint32_t>(value);
}

// The store is one static instance, as on the board; sysconfig_init() is exactly the
// boot-time reset, so each test starts from the config.h defaults.
class SysConfigTest : public ::testing::Test
{
protected:
    void SetUp() override { sysconfig_init(); }
};

TEST_F(SysConfigTest, InitSeedsTheConfigHeaderDefaults)
{
    // Compared against the macros themselves, so a config.h (or config_overrides.h)
    // change can never desynchronize this test from the firmware.
    EXPECT_EQ(sysconfig_get_u32(SYSCFG_USB_TASK_OSDELAY), static_cast<uint32_t>(USB_TASK_OSDELAY));
    EXPECT_EQ(sysconfig_get_u32(SYSCFG_ADS1115_RATE), static_cast<uint32_t>(ADS1115_RATE));
    EXPECT_FLOAT_EQ(sysconfig_get_float(SYSCFG_MAX_FORCE_LBF), static_cast<float>(MAX_FORCE_LBF));
}

TEST_F(SysConfigTest, AppliesAnInRangeU32Write)
{
    ASSERT_TRUE(sysconfig_set_raw(SYSCFG_PID_TASK_OSDELAY, 42));
    EXPECT_EQ(sysconfig_get_u32(SYSCFG_PID_TASK_OSDELAY), 42u);
}

TEST_F(SysConfigTest, U32RangeBoundariesAreInclusive)
{
    // The generated table gives every *_OSDELAY the width of the uint16_t a millisecond
    // delay logically is, so [0, 65535]. Zero is reachable on purpose: a task that stops
    // yielding is a bad idea, not an unrepresentable value, and the range only stops the
    // latter.
    EXPECT_TRUE(sysconfig_set_raw(SYSCFG_PID_TASK_OSDELAY, 0));
    EXPECT_TRUE(sysconfig_set_raw(SYSCFG_PID_TASK_OSDELAY, 65535));
}

TEST_F(SysConfigTest, RejectsAnOutOfRangeU32WriteAndKeepsTheOldValue)
{
    ASSERT_TRUE(sysconfig_set_raw(SYSCFG_PID_TASK_OSDELAY, 42));

    // Only the top of an integer range can be exceeded -- every one of them starts at 0.
    EXPECT_FALSE(sysconfig_set_raw(SYSCFG_PID_TASK_OSDELAY, 65536));
    EXPECT_EQ(sysconfig_get_u32(SYSCFG_PID_TASK_OSDELAY), 42u);
}

TEST_F(SysConfigTest, FloatRangeBoundariesAreInclusive)
{
    // MIN_DUTY_CYCLE_PERCENT accepts [0.0, 1.0].
    EXPECT_TRUE(sysconfig_set_raw(SYSCFG_MIN_DUTY_CYCLE_PERCENT, Bits(0.0f)));
    EXPECT_TRUE(sysconfig_set_raw(SYSCFG_MIN_DUTY_CYCLE_PERCENT, Bits(1.0f)));
}

TEST_F(SysConfigTest, RejectsAnOutOfRangeFloatWriteAndKeepsTheOldValue)
{
    ASSERT_TRUE(sysconfig_set_raw(SYSCFG_MIN_DUTY_CYCLE_PERCENT, Bits(0.5f)));

    EXPECT_FALSE(sysconfig_set_raw(SYSCFG_MIN_DUTY_CYCLE_PERCENT, Bits(1.0001f)));
    EXPECT_FALSE(sysconfig_set_raw(SYSCFG_MIN_DUTY_CYCLE_PERCENT, Bits(-0.0001f)));
    EXPECT_FLOAT_EQ(sysconfig_get_float(SYSCFG_MIN_DUTY_CYCLE_PERCENT), 0.5f);
}

TEST_F(SysConfigTest, RejectsNanAndInfinityBitPatterns)
{
    // K_P's range is [-1e6, 1e6], but no non-finite value is acceptable anywhere: a NaN
    // fed to the PID would poison every output after it.
    EXPECT_FALSE(sysconfig_set_raw(SYSCFG_K_P, Bits(NAN)));
    EXPECT_FALSE(sysconfig_set_raw(SYSCFG_K_P, Bits(INFINITY)));
    EXPECT_FALSE(sysconfig_set_raw(SYSCFG_K_P, Bits(-INFINITY)));
    EXPECT_FLOAT_EQ(sysconfig_get_float(SYSCFG_K_P), static_cast<float>(K_P));
}

TEST_F(SysConfigTest, FloatWritesRoundTripBitExactly)
{
    ASSERT_TRUE(sysconfig_set_raw(SYSCFG_K_P, Bits(2.5f)));
    EXPECT_EQ(Bits(sysconfig_get_float(SYSCFG_K_P)), Bits(2.5f));
}

TEST_F(SysConfigTest, RejectsAnUnknownParameterId)
{
    // One past the last id — what a newer host talking to older firmware would send.
    auto unknown = static_cast<sysconfig_param_t>(SYSCFG_PARAM_COUNT);
    EXPECT_FALSE(sysconfig_set_raw(unknown, 1));
    EXPECT_EQ(sysconfig_get_u32(unknown), 0u);
    EXPECT_FLOAT_EQ(sysconfig_get_float(unknown), 0.0f);
}

TEST_F(SysConfigTest, EnumCodedParametersRejectCodesPastTheirLastOption)
{
    // ADS1115_MODE's options are 0 (continuous) and 1 (single-shot).
    EXPECT_TRUE(sysconfig_set_raw(SYSCFG_ADS1115_MODE, 0));
    EXPECT_TRUE(sysconfig_set_raw(SYSCFG_ADS1115_MODE, 1));
    EXPECT_FALSE(sysconfig_set_raw(SYSCFG_ADS1115_MODE, 2));
    EXPECT_EQ(sysconfig_get_u32(SYSCFG_ADS1115_MODE), 1u);
}

// The brake duty-cycle envelope. Both the BPM task and the UI clamp against it, so it has to
// come back ordered whatever the host left in the store -- a crossed pair reaching std::clamp
// is undefined behaviour, and a UI clamping against an inverted pair would show a duty cycle
// the timer never drives.

TEST_F(SysConfigTest, DutyCycleLimitsAreTheConfiguredDefaults)
{
    float min = NAN;
    float max = NAN;
    sysconfig_get_duty_cycle_limits(&min, &max);

    EXPECT_FLOAT_EQ(min, static_cast<float>(MIN_DUTY_CYCLE_PERCENT));
    EXPECT_FLOAT_EQ(max, static_cast<float>(MAX_DUTY_CYCLE_PERCENT));
}

TEST_F(SysConfigTest, DutyCycleLimitsComeBackOrderedWhenTheHostCrossesThem)
{
    // Each write is individually valid -- the store range-checks against [0,1], not against
    // the other bound -- so this state is reachable, and is also unavoidable transiently
    // whenever a host raises both bounds and the low one lands first.
    ASSERT_TRUE(sysconfig_set_raw(SYSCFG_MIN_DUTY_CYCLE_PERCENT, Bits(0.9f)));
    ASSERT_TRUE(sysconfig_set_raw(SYSCFG_MAX_DUTY_CYCLE_PERCENT, Bits(0.2f)));

    float min = NAN;
    float max = NAN;
    sysconfig_get_duty_cycle_limits(&min, &max);

    EXPECT_FLOAT_EQ(min, 0.2f);
    EXPECT_FLOAT_EQ(max, 0.9f);
    EXPECT_LE(min, max);
}

TEST_F(SysConfigTest, DutyCycleLimitsCollapseToAPointWhenBothBoundsMatch)
{
    ASSERT_TRUE(sysconfig_set_raw(SYSCFG_MIN_DUTY_CYCLE_PERCENT, Bits(0.4f)));
    ASSERT_TRUE(sysconfig_set_raw(SYSCFG_MAX_DUTY_CYCLE_PERCENT, Bits(0.4f)));

    float min = NAN;
    float max = NAN;
    sysconfig_get_duty_cycle_limits(&min, &max);

    EXPECT_FLOAT_EQ(min, 0.4f);
    EXPECT_FLOAT_EQ(max, 0.4f);
    EXPECT_LE(min, max);
}

TEST_F(SysConfigTest, DutyCycleLimitsAcceptEitherOutputBeingUnwanted)
{
    ASSERT_TRUE(sysconfig_set_raw(SYSCFG_MIN_DUTY_CYCLE_PERCENT, Bits(0.9f)));
    ASSERT_TRUE(sysconfig_set_raw(SYSCFG_MAX_DUTY_CYCLE_PERCENT, Bits(0.2f)));

    float only = NAN;
    sysconfig_get_duty_cycle_limits(&only, nullptr);
    EXPECT_FLOAT_EQ(only, 0.2f);

    only = NAN;
    sysconfig_get_duty_cycle_limits(nullptr, &only);
    EXPECT_FLOAT_EQ(only, 0.9f);

    sysconfig_get_duty_cycle_limits(nullptr, nullptr);   // must not fault
}

TEST_F(SysConfigTest, DutyCycleBoundsStillRejectValuesOutsideZeroToOne)
{
    EXPECT_FALSE(sysconfig_set_raw(SYSCFG_MAX_DUTY_CYCLE_PERCENT, Bits(1.5f)));
    EXPECT_FALSE(sysconfig_set_raw(SYSCFG_MIN_DUTY_CYCLE_PERCENT, Bits(-0.1f)));
    EXPECT_FALSE(sysconfig_set_raw(SYSCFG_MAX_DUTY_CYCLE_PERCENT, Bits(NAN)));

    float min = NAN;
    float max = NAN;
    sysconfig_get_duty_cycle_limits(&min, &max);
    EXPECT_FLOAT_EQ(min, static_cast<float>(MIN_DUTY_CYCLE_PERCENT));
    EXPECT_FLOAT_EQ(max, static_cast<float>(MAX_DUTY_CYCLE_PERCENT));
}

} // namespace
