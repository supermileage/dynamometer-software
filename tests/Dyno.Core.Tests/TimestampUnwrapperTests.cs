using Dyno.Core.Plotting;
using Xunit;

namespace Dyno.Core.Tests;

/// <summary>
/// The device stamps samples with a free-running 32-bit counter at 1 MHz, so it rolls over every
/// ~71.6 minutes. Unwrapping is what keeps a long run plotting as one continuous timeline instead
/// of jumping 71 minutes backwards mid-session — and it has to do that while tolerating the few
/// milliseconds of skew that come from reading each device task in turn.
/// </summary>
public class TimestampUnwrapperTests
{
    private const uint Max = uint.MaxValue;

    [Fact]
    public void TheFirstSampleIsTakenAtFaceValue()
    {
        var clock = new TimestampUnwrapper();
        Assert.Equal(1_500_000ul, clock.ToTicks(1_500_000));
        Assert.Equal(1.5, clock.ToSeconds(1_500_000));
    }

    [Fact]
    public void MonotonicTimestampsPassThroughUnchanged()
    {
        var clock = new TimestampUnwrapper();
        for (uint t = 0; t < 1_000_000; t += 100_000)
        {
            Assert.Equal(t, clock.ToTicks(t));
        }
    }

    [Fact]
    public void ARolloverKeepsTimeMovingForwards()
    {
        var clock = new TimestampUnwrapper();
        clock.ToTicks(Max - 1000); // shortly before the wrap

        // 2000 ticks later the counter has rolled through zero.
        ulong ticks = clock.ToTicks(999);

        Assert.Equal((ulong)Max + 1000ul, ticks);
        // The step across the boundary is exactly the elapsed 2000 ticks, not a 71-minute jump.
        Assert.Equal(2000ul, ticks - (Max - 1000));
    }

    [Fact]
    public void SuccessiveRolloversAccumulate()
    {
        var clock = new TimestampUnwrapper();
        clock.ToTicks(0);

        // Walk forward in quarter-counter steps so each lap crosses the boundary once. Every step
        // is well under the half-range threshold, so each is unambiguously forward progress.
        const uint quarter = 0x4000_0000u;
        uint raw = 0;
        ulong previous = 0;
        for (int step = 1; step <= 12; step++) // three full laps
        {
            raw = unchecked(raw + quarter);
            ulong ticks = clock.ToTicks(raw);
            Assert.True(ticks > previous, $"step {step} went backwards");
            Assert.Equal(previous + quarter, ticks);
            previous = ticks;
        }

        Assert.Equal(3ul * 0x1_0000_0000ul, previous); // exactly three rollovers accumulated
    }

    [Fact]
    public void OutOfOrderSamplesAreNotMistakenForARollover()
    {
        var clock = new TimestampUnwrapper();
        clock.ToTicks(10_000_000);

        // A different task's sample, stamped 5 ms earlier. Ordinary interleaving skew: it must map
        // to a slightly earlier time, not to a whole rollover 71 minutes into the future.
        ulong earlier = clock.ToTicks(9_995_000);
        Assert.Equal(9_995_000ul, earlier);

        // And the stream carries on from where it was.
        Assert.Equal(10_001_000ul, clock.ToTicks(10_001_000));
    }

    [Fact]
    public void SkewAcrossARolloverBoundaryStaysContinuous()
    {
        var clock = new TimestampUnwrapper();
        clock.ToTicks(Max - 5000);
        ulong afterWrap = clock.ToTicks(1000); // rolled over

        // A straggler from just before the boundary arrives late.
        ulong straggler = clock.ToTicks(Max - 2000);

        Assert.Equal((ulong)Max + 1001ul, afterWrap);
        // It lands before the post-wrap sample and after the pre-wrap one — no 71-minute excursion.
        Assert.True(
            straggler < afterWrap,
            "late pre-wrap sample should sort before the wrapped one"
        );
        Assert.True(straggler > Max - 5000ul);
    }

    [Fact]
    public void ResetStartsAFreshReference()
    {
        var clock = new TimestampUnwrapper();
        clock.ToTicks(Max - 100);
        clock.ToTicks(50); // wrapped, so an offset is accumulated

        clock.Reset();

        Assert.Equal(777ul, clock.ToTicks(777));
    }

    [Fact]
    public void SecondsUseTheDeviceTickRate()
    {
        var clock = new TimestampUnwrapper();
        Assert.Equal(1_000_000.0, TimestampUnwrapper.TicksPerSecond);
        Assert.Equal(2.5, clock.ToSeconds(2_500_000), 9);
    }

    /// <summary>A run long enough to cross the boundary must still read as one increasing
    /// timeline — this is the property the plots depend on.</summary>
    [Fact]
    public void AnHourLongRunAcrossTheBoundaryIsStrictlyIncreasing()
    {
        var clock = new TimestampUnwrapper();
        const uint step = 10_000; // 10 ms
        uint raw = Max - 300_000; // start ~0.3 s before the rollover
        double previous = -1;

        for (int i = 0; i < 20_000; i++) // ~200 s of samples, straddling the wrap
        {
            double seconds = clock.ToSeconds(raw);
            Assert.True(seconds > previous, $"time went backwards at sample {i}");
            previous = seconds;
            raw = unchecked(raw + step);
        }
    }
}
