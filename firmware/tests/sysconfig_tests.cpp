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
    // The generated table gives every *_OSDELAY the range [1, 60000].
    EXPECT_TRUE(sysconfig_set_raw(SYSCFG_PID_TASK_OSDELAY, 1));
    EXPECT_TRUE(sysconfig_set_raw(SYSCFG_PID_TASK_OSDELAY, 60000));
}

TEST_F(SysConfigTest, RejectsAnOutOfRangeU32WriteAndKeepsTheOldValue)
{
    ASSERT_TRUE(sysconfig_set_raw(SYSCFG_PID_TASK_OSDELAY, 42));

    EXPECT_FALSE(sysconfig_set_raw(SYSCFG_PID_TASK_OSDELAY, 0));     // below min (busy-spin)
    EXPECT_FALSE(sysconfig_set_raw(SYSCFG_PID_TASK_OSDELAY, 60001)); // above max
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

} // namespace
