// The encoder arithmetic, checked against hand-computed physics.
//
// The headline property is resolution. Measuring between real pulse edges makes the swept angle
// exact, so velocity error comes only from the 1 us timestamps; the old fixed-window counter
// carried a +/-1 count ambiguity worth +/-9.8 rad/s at 64 apertures and 10 ms, which the
// derivative then multiplied into +/-982 rad/s^2. Several tests below pin those numbers down.

#include <gtest/gtest.h>

#include <cmath>

extern "C" {
#include "Tasks/OpticalSensor/encoder_math.h"
}

namespace
{
constexpr uint32_t kApertures = 64;   // config.h: tied to the 3D printed disc
constexpr uint32_t kTicksPerSecond = 1000000;   // TIM2 at 1 MHz -- 1 us per tick
constexpr float kRadiansPerCount = 2.0f * (float)M_PI / (float)kApertures;

// Ticks spanned by `counts` apertures at a given speed.
uint32_t TicksFor(uint32_t counts, double radPerSec)
{
    double seconds = counts * (double)kRadiansPerCount / radPerSec;
    return (uint32_t)std::llround(seconds * kTicksPerSecond);
}
}

TEST(EncoderMathTest, OneFullRevolutionPerSecondIsTwoPi)
{
    // 64 apertures in exactly one second.
    float w = encoder_angular_velocity(kApertures, kTicksPerSecond, kApertures, kTicksPerSecond);
    EXPECT_NEAR(w, 2.0f * (float)M_PI, 1e-4f);
}

TEST(EncoderMathTest, VelocityMatchesHandComputedSpeeds)
{
    // 3000 RPM = 314.159 rad/s. Measure over 32 apertures.
    const double target = 3000.0 * 2.0 * M_PI / 60.0;
    uint32_t ticks = TicksFor(32, target);
    float w = encoder_angular_velocity(32, ticks, kApertures, kTicksPerSecond);
    EXPECT_NEAR(w, (float)target, (float)target * 1e-3f);

    // 300 RPM: an order of magnitude slower, and still exact -- this is the regime where the
    // fixed-window counter was +/-31% off because it only saw about 3 counts per window.
    const double slow = 300.0 * 2.0 * M_PI / 60.0;
    ticks = TicksFor(3, slow);
    w = encoder_angular_velocity(3, ticks, kApertures, kTicksPerSecond);
    EXPECT_NEAR(w, (float)slow, (float)slow * 1e-3f);
}

// The point of the rewrite, stated as a test: with the interval pinned to pulse edges, the error
// is set by timestamp resolution and not by how many counts happened to land in a window.
TEST(EncoderMathTest, ResolutionIsSetByTimestampsNotCountQuantization)
{
    const double target = 1000.0 * 2.0 * M_PI / 60.0;   // 104.7 rad/s
    uint32_t ticks = TicksFor(10, target);

    // Worst case the interval is off by one tick at each end.
    float exact = encoder_angular_velocity(10, ticks, kApertures, kTicksPerSecond);
    float low = encoder_angular_velocity(10, ticks + 2, kApertures, kTicksPerSecond);
    float high = encoder_angular_velocity(10, ticks - 2, kApertures, kTicksPerSecond);

    float worstErrorFraction =
        std::max(std::fabs(high - exact), std::fabs(low - exact)) / exact;
    EXPECT_LT(worstErrorFraction, 0.001f) << "2 us of timing error on a ~57 ms interval";

    // For contrast, the old method's +/-1 count on a 10 ms window at this speed:
    const float oldQuantum = kRadiansPerCount / 0.01f;   // 9.82 rad/s
    EXPECT_NEAR(oldQuantum, 9.82f, 0.01f);
    EXPECT_GT(oldQuantum / (float)target, 0.09f) << "old error was ~9% at 1000 RPM";
}

TEST(EncoderMathTest, VelocityIsZeroWhenNoPulsesOrNoInterval)
{
    EXPECT_EQ(encoder_angular_velocity(0, 1000, kApertures, kTicksPerSecond), 0.0f);
    EXPECT_EQ(encoder_angular_velocity(5, 0, kApertures, kTicksPerSecond), 0.0f);
}

// Bad sysconfig must not produce an infinity that then flows into torque and power.
TEST(EncoderMathTest, DegenerateConfigurationYieldsZeroRatherThanInfinity)
{
    EXPECT_EQ(encoder_angular_velocity(10, 1000, 0, kTicksPerSecond), 0.0f);
    EXPECT_EQ(encoder_angular_velocity(10, 1000, kApertures, 0), 0.0f);
    EXPECT_EQ(encoder_velocity_upper_bound(1000, 0, kTicksPerSecond), 0.0f);
    EXPECT_TRUE(std::isfinite(encoder_angular_velocity(10, 1, kApertures, kTicksPerSecond)));
}

// While no pulse arrives, the reported speed is a ceiling that falls as the silence grows, so a
// slowly turning shaft decays toward zero instead of flapping between 0 and a full quantum.
TEST(EncoderMathTest, TheNoPulseBoundDecaysAsSilenceGrows)
{
    float after10ms = encoder_velocity_upper_bound(10000, kApertures, kTicksPerSecond);
    float after50ms = encoder_velocity_upper_bound(50000, kApertures, kTicksPerSecond);
    float after1s = encoder_velocity_upper_bound(1000000, kApertures, kTicksPerSecond);

    EXPECT_NEAR(after10ms, kRadiansPerCount / 0.01f, 1e-3f);
    EXPECT_LT(after50ms, after10ms);
    EXPECT_LT(after1s, after50ms);
    EXPECT_NEAR(after1s, kRadiansPerCount, 1e-4f);
}

TEST(EncoderMathTest, AccelerationIsTheVelocityDifferenceOverElapsedTime)
{
    // +10 rad/s over 100 ms = 100 rad/s^2.
    float a = encoder_angular_acceleration(50.0f, 60.0f, 100000, kTicksPerSecond);
    EXPECT_NEAR(a, 100.0f, 1e-2f);

    // Deceleration keeps its sign.
    a = encoder_angular_acceleration(60.0f, 50.0f, 100000, kTicksPerSecond);
    EXPECT_NEAR(a, -100.0f, 1e-2f);
}

TEST(EncoderMathTest, ConstantSpeedProducesNoAcceleration)
{
    const double target = 2000.0 * 2.0 * M_PI / 60.0;
    uint32_t ticks = TicksFor(20, target);

    float first = encoder_angular_velocity(20, ticks, kApertures, kTicksPerSecond);
    float second = encoder_angular_velocity(20, ticks, kApertures, kTicksPerSecond);
    float a = encoder_angular_acceleration(first, second, ticks, kTicksPerSecond);

    // Exactly zero: identical intervals give identical velocities. Under the old fixed-window
    // scheme this same steady shaft alternated counts and swung +/-982 rad/s^2.
    EXPECT_EQ(a, 0.0f);
}

// A steady shaft whose pulses land one tick differently between intervals -- the realistic
// worst case now -- must stay far below the old scheme's quantization noise.
TEST(EncoderMathTest, JitterOfOneTickDoesNotSwampAcceleration)
{
    const double target = 1000.0 * 2.0 * M_PI / 60.0;
    uint32_t ticks = TicksFor(10, target);

    float first = encoder_angular_velocity(10, ticks, kApertures, kTicksPerSecond);
    float second = encoder_angular_velocity(10, ticks + 1, kApertures, kTicksPerSecond);
    float a = encoder_angular_acceleration(first, second, ticks, kTicksPerSecond);

    const float oldNoise = (kRadiansPerCount / 0.01f) / 0.01f;   // 982 rad/s^2
    EXPECT_NEAR(oldNoise, 982.0f, 1.0f);
    EXPECT_LT(std::fabs(a), oldNoise / 100.0f) << "at least 100x quieter than the old scheme";
}

TEST(EncoderMathTest, AccelerationIsZeroWithoutAnInterval)
{
    EXPECT_EQ(encoder_angular_acceleration(10.0f, 20.0f, 0, kTicksPerSecond), 0.0f);
    EXPECT_EQ(encoder_angular_acceleration(10.0f, 20.0f, 1000, 0), 0.0f);
}

// Unsigned subtraction of raw timestamps is what the task feeds in, and it has to survive TIM2
// rolling over -- every ~71.6 minutes now that a tick is 1 us.
TEST(EncoderMathTest, IntervalsSpanningACounterWrapAreCorrect)
{
    uint32_t before = 0xFFFFF000u;   // shortly before the wrap
    uint32_t after = 0x00000FA0u;    // shortly after
    uint32_t delta = after - before; // modular arithmetic: 8096 ticks
    EXPECT_EQ(delta, 8096u);

    float wrapped = encoder_angular_velocity(10, delta, kApertures, kTicksPerSecond);
    float plain = encoder_angular_velocity(10, 8096, kApertures, kTicksPerSecond);
    EXPECT_FLOAT_EQ(wrapped, plain);
}
