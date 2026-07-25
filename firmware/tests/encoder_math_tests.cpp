// The encoder arithmetic, checked against hand-computed physics.
//
// Pulses are counted in hardware (TIM4 clocked by the encoder pin) and sampled on a fixed window,
// so the headline property is what that window costs: a partial aperture at each end leaves a
// +/-1 count ambiguity no matter how fast the shaft turns, and the window length alone sets how
// big that is. At 64 apertures it is +/-9.8 rad/s over 10 ms but +/-0.49 rad/s over 200 ms, and
// the derivative multiplies both by the same factor again. Several tests below pin those numbers
// down, because they are the whole justification for the 200 ms window in config.h.
//
// The second thing worth proving is that the free-running 16-bit counter is read correctly: it is
// never reset, so every count comes from a difference that has to survive both the counter's wrap
// and the wrap of the software overflow tally above it.

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

constexpr uint32_t kWindowTicks = 200000;      // config.h: OPTICAL_ENCODER_TASK_OSDELAY, 200 ms
constexpr uint32_t kOldWindowTicks = 10000;    // the 10 ms window it replaced

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

    // 300 RPM: an order of magnitude slower, and the arithmetic is just as exact. What changes at
    // low speed is not this formula but how many counts the window collects to feed it.
    const double slow = 300.0 * 2.0 * M_PI / 60.0;
    ticks = TicksFor(3, slow);
    w = encoder_angular_velocity(3, ticks, kApertures, kTicksPerSecond);
    EXPECT_NEAR(w, (float)slow, (float)slow * 1e-3f);
}

// The tradeoff the 200 ms window buys, stated as a test: with the interval bounded by task
// wake-ups rather than pulse edges, +/-1 count is unavoidable, and its size is set purely by the
// window. This is why lengthening the window is the lever for resolution.
TEST(EncoderMathTest, WindowLengthAloneSetsTheQuantizationError)
{
    const float quantumAt200ms = kRadiansPerCount / 0.2f;
    const float quantumAt10ms = kRadiansPerCount / 0.01f;

    EXPECT_NEAR(quantumAt200ms, 0.491f, 0.001f);   // ~4.7 RPM
    EXPECT_NEAR(quantumAt10ms, 9.82f, 0.01f);      // ~94 RPM
    EXPECT_NEAR(quantumAt10ms / quantumAt200ms, 20.0f, 1e-3f) << "20x window, 20x resolution";

    // And it really is independent of speed: one extra count is worth the same rad/s whether the
    // shaft is crawling or flying.
    const double slow = 300.0 * 2.0 * M_PI / 60.0;
    const double fast = 6000.0 * 2.0 * M_PI / 60.0;
    auto errorFor = [](double radPerSec) {
        uint32_t counts = (uint32_t)std::llround(radPerSec * 0.2 / kRadiansPerCount);
        float exact = encoder_angular_velocity(counts, kWindowTicks, kApertures, kTicksPerSecond);
        float plusOne =
            encoder_angular_velocity(counts + 1, kWindowTicks, kApertures, kTicksPerSecond);
        return plusOne - exact;
    };
    EXPECT_NEAR(errorFor(slow), quantumAt200ms, 1e-3f);
    EXPECT_NEAR(errorFor(fast), quantumAt200ms, 1e-3f);
}

// The floor that comes with the window: below one count per window there is nothing to measure,
// which is what encoder_velocity_upper_bound() exists to cover.
TEST(EncoderMathTest, TheWindowSetsTheSlowestDetectableSpeed)
{
    const float slowestAt200ms = kRadiansPerCount / 0.2f;
    EXPECT_NEAR(slowestAt200ms * 60.0f / (2.0f * (float)M_PI), 4.69f, 0.01f);   // RPM

    const float slowestAt10ms = kRadiansPerCount / 0.01f;
    EXPECT_NEAR(slowestAt10ms * 60.0f / (2.0f * (float)M_PI), 93.75f, 0.01f);

    // A shaft turning at 3 RPM produces no counts in a 200 ms window at all.
    const double threeRpm = 3.0 * 2.0 * M_PI / 60.0;
    EXPECT_EQ((uint32_t)(threeRpm * 0.2 / kRadiansPerCount), 0u);
    EXPECT_EQ(encoder_angular_velocity(0, kWindowTicks, kApertures, kTicksPerSecond), 0.0f);
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

    // Exactly zero: identical windows with identical counts give identical velocities.
    EXPECT_EQ(a, 0.0f);
}

// The realistic worst case for a fixed window: a steady shaft whose speed does not divide evenly
// into the window alternates between N and N+1 counts, and the derivative amplifies that. The
// window appears twice -- once in the velocity, once in the differentiation -- so the noise falls
// with its square, which is the real reason 200 ms was worth the drop in update rate.
TEST(EncoderMathTest, OneCountOfAlternationIsQuietAtTheChosenWindow)
{
    const double target = 1000.0 * 2.0 * M_PI / 60.0;
    const uint32_t counts = (uint32_t)std::llround(target * 0.2 / kRadiansPerCount);

    float first = encoder_angular_velocity(counts, kWindowTicks, kApertures, kTicksPerSecond);
    float second = encoder_angular_velocity(counts + 1, kWindowTicks, kApertures, kTicksPerSecond);
    float a = encoder_angular_acceleration(first, second, kWindowTicks, kTicksPerSecond);

    const float noiseAt200ms = (kRadiansPerCount / 0.2f) / 0.2f;
    const float noiseAt10ms = (kRadiansPerCount / 0.01f) / 0.01f;
    EXPECT_NEAR(std::fabs(a), noiseAt200ms, 1e-2f);
    EXPECT_NEAR(noiseAt200ms, 2.45f, 0.01f);
    EXPECT_NEAR(noiseAt10ms, 982.0f, 1.0f);
    EXPECT_NEAR(noiseAt10ms / noiseAt200ms, 400.0f, 1.0f) << "20x window, 400x quieter derivative";
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

// --- Reading the free-running hardware counter ------------------------------------------------

TEST(EncoderCounterTest, TheTotalIsTheWrapCountAboveTheCounter)
{
    EXPECT_EQ(encoder_extended_count(0, 0, false), 0u);
    EXPECT_EQ(encoder_extended_count(0, 1234, false), 1234u);
    EXPECT_EQ(encoder_extended_count(3, 7, false), 3u * 65536u + 7u);
}

// The property the whole pending-flag dance exists for: the reading must not depend on whether the
// overflow ISR has run yet. Right after a wrap the task can catch either state, and both have to
// produce the same total -- otherwise one window loses 65536 counts and the next invents them.
TEST(EncoderCounterTest, APendingWrapReadsTheSameAsAServicedOne)
{
    const uint32_t serviced = encoder_extended_count(1, 4, false);   // ISR already ran
    const uint32_t pending = encoder_extended_count(0, 4, true);     // ISR still masked
    EXPECT_EQ(serviced, pending);
    EXPECT_EQ(serviced, 65540u);
}

TEST(EncoderCounterTest, DeltasSurviveTheCounterWrapping)
{
    // 65530 -> 4, i.e. ten pulses across the 16-bit boundary.
    const uint32_t before = encoder_extended_count(0, 65530, false);
    const uint32_t after = encoder_extended_count(1, 4, false);
    EXPECT_EQ(encoder_count_delta(after, before), 10u);

    // Same ten pulses, read before the ISR got its turn.
    EXPECT_EQ(encoder_count_delta(encoder_extended_count(0, 4, true), before), 10u);
}

// A window can legitimately span more than one wrap -- nothing about the math caps a window at
// 65535 pulses, it just needs the wraps to have been counted.
TEST(EncoderCounterTest, DeltasLargerThanTheCounterAreCarriedByTheWrapCount)
{
    const uint32_t before = encoder_extended_count(0, 0, false);
    const uint32_t after = encoder_extended_count(5, 100, false);
    EXPECT_EQ(encoder_count_delta(after, before), 5u * 65536u + 100u);
}

// The wrap count is itself a uint32 that is never reset, so it eventually wraps too. That is not a
// special case: the total is only ever consumed as a difference, and modular arithmetic carries
// through both wraps at once. At 64 apertures this boundary is ~67 million revolutions away, but
// getting it wrong would be a single catastrophic reading rather than a small drift.
TEST(EncoderCounterTest, DeltasSurviveTheWrapCountItselfWrapping)
{
    const uint32_t before = encoder_extended_count(0xFFFFFFFFu, 0xFFF0, false);
    EXPECT_EQ(before, 0xFFFFFFF0u);

    // Five more pulses: the counter wraps, and incrementing 0xFFFFFFFF wraps the tally to 0.
    const uint32_t afterPending = encoder_extended_count(0xFFFFFFFFu, 0x0005, true);
    const uint32_t afterServiced = encoder_extended_count(0x00000000u, 0x0005, false);
    EXPECT_EQ(afterPending, afterServiced);
    EXPECT_EQ(encoder_count_delta(afterPending, before), 21u);   // 0x10 remaining + 0x05
}

// Differencing a counter that is never written is what makes this lossless: pulses arriving while
// the task is reading land in the next window instead of vanishing, which a read-and-reset scheme
// cannot promise.
TEST(EncoderCounterTest, ConsecutiveWindowsLoseNoCounts)
{
    const uint32_t readings[] = {
        encoder_extended_count(0, 1000, false),
        encoder_extended_count(0, 4500, false),
        encoder_extended_count(1, 100, false),
        encoder_extended_count(1, 60000, false),
    };

    uint32_t summed = 0;
    for (size_t i = 1; i < std::size(readings); ++i)
    {
        summed += encoder_count_delta(readings[i], readings[i - 1]);
    }
    EXPECT_EQ(summed, encoder_count_delta(readings[std::size(readings) - 1], readings[0]));
}
