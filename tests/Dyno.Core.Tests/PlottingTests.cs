using Dyno.Core.Plotting;
using Xunit;

namespace Dyno.Core.Tests;

public class TimeSeriesBufferTests
{
    [Fact]
    public void CopyWindow_ReturnsOnlySamplesAtOrAfterFromTime_InOrder()
    {
        var buffer = new TimeSeriesBuffer(8);
        for (int i = 0; i < 5; i++)
        {
            buffer.Add(i, i * 10f);
        }

        var times = new double[8];
        var values = new float[8];
        int count = buffer.CopyWindow(2.0, times, values);

        Assert.Equal(3, count);
        Assert.Equal([2.0, 3.0, 4.0], times.Take(count));
        Assert.Equal([20f, 30f, 40f], values.Take(count));
    }

    [Fact]
    public void WhenFull_OldestSamplesAreOverwritten()
    {
        var buffer = new TimeSeriesBuffer(4, maxCapacity: 4); // at the ceiling: no room to grow
        for (int i = 0; i < 6; i++)
        {
            buffer.Add(i, i);
        }

        Assert.Equal(4, buffer.Count);
        Assert.Equal(5.0, buffer.LatestTime);

        var times = new double[4];
        var values = new float[4];
        int count = buffer.CopyWindow(double.MinValue, times, values);

        Assert.Equal(4, count);
        Assert.Equal([2.0, 3.0, 4.0, 5.0], times.Take(count)); // 0 and 1 rolled off
    }

    [Fact]
    public void Clear_EmptiesTheBuffer()
    {
        var buffer = new TimeSeriesBuffer(4);
        buffer.Add(1, 1f);
        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.CopyWindow(double.MinValue, new double[4], new float[4]));
    }
}

public class EnvelopeTests
{
    [Fact]
    public void SparseData_PassesThroughUnchanged()
    {
        double[] times = [0.5, 1.5, 2.5];
        float[] values = [1f, 5f, 3f];
        var outTimes = new double[20];
        var outValues = new float[20];

        // 10 buckets over [0,10): at most one sample per bucket, so nothing to reduce.
        int written = Envelope.Decimate(times, values, 3, 0, 10, 10, outTimes, outValues);

        Assert.Equal(3, written);
        Assert.Equal(times, outTimes.Take(written));
        Assert.Equal(values, outValues.Take(written));
    }

    [Fact]
    public void DenseData_KeepsEveryBucketsExtremes()
    {
        // 1000 samples in [0,1) with a one-sample spike; a single bucket must still surface it.
        var times = new double[1000];
        var values = new float[1000];
        for (int i = 0; i < 1000; i++)
        {
            times[i] = i / 1000.0;
            values[i] = 1f;
        }
        values[537] = 99f; // the spike naive stride-decimation would drop

        var outTimes = new double[4];
        var outValues = new float[4];
        int written = Envelope.Decimate(times, values, 1000, 0, 1, 1, outTimes, outValues);

        Assert.Equal(2, written);
        Assert.Contains(99f, outValues.Take(written));
        Assert.Contains(1f, outValues.Take(written));
    }

    [Fact]
    public void OutputTimes_AreNonDecreasing()
    {
        var random = new Random(42);
        var times = new double[500];
        var values = new float[500];
        for (int i = 0; i < 500; i++)
        {
            times[i] = i * 0.01;
            values[i] = (float)random.NextDouble();
        }

        var outTimes = new double[100];
        var outValues = new float[100];
        int written = Envelope.Decimate(times, values, 500, 0, 5, 50, outTimes, outValues);

        for (int i = 1; i < written; i++)
        {
            Assert.True(outTimes[i] >= outTimes[i - 1]);
        }
    }

    [Fact]
    public void EmptyInput_WritesNothing()
    {
        Assert.Equal(0, Envelope.Decimate([], [], 0, 0, 1, 10, new double[20], new float[20]));
    }
}

/// <summary>
/// The plot decides whether a channel is present in the visible window with an O(1) shortcut --
/// <c>Count > 0 &amp;&amp; LatestTime >= windowStart</c> -- rather than copying the window first.
/// That shortcut gates the channel's line <em>and</em> its axis, so a disagreement with the real
/// windowing would either hide a live channel or draw a labeled axis for a silent one.
/// </summary>
public class WindowPresenceTests
{
    private static bool HasDataInWindow(TimeSeriesBuffer buffer, double windowStart) =>
        buffer.Count > 0 && buffer.LatestTime >= windowStart;

    private static bool CopyFindsData(TimeSeriesBuffer buffer, double windowStart)
    {
        var times = new double[buffer.Capacity];
        var values = new float[buffer.Capacity];
        return buffer.CopyWindow(windowStart, times, values) > 0;
    }

    [Fact]
    public void AnEmptyBufferIsAbsentFromEveryWindow()
    {
        var buffer = new TimeSeriesBuffer(16);

        Assert.False(HasDataInWindow(buffer, 0));
        Assert.False(CopyFindsData(buffer, 0));
        Assert.False(HasDataInWindow(buffer, -100));
        Assert.False(CopyFindsData(buffer, -100));
    }

    [Fact]
    public void AChannelThatStoppedStreamingLeavesTheWindowOnceItsLastSampleScrollsOff()
    {
        var buffer = new TimeSeriesBuffer(16);
        for (int i = 0; i < 5; i++)
        {
            buffer.Add(i, i);
        }

        // Newest sample is at t=4; a window still reaching it keeps the channel present...
        Assert.True(HasDataInWindow(buffer, 4.0));
        Assert.True(CopyFindsData(buffer, 4.0));

        // ...and the instant the window starts past it, the channel is gone -- no line, no axis.
        Assert.False(HasDataInWindow(buffer, 4.001));
        Assert.False(CopyFindsData(buffer, 4.001));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    [InlineData(2.5)]
    [InlineData(9.0)]
    [InlineData(9.5)]
    [InlineData(100.0)]
    public void TheShortcutAgreesWithCopyWindowAtEveryOffset(double windowStart)
    {
        var buffer = new TimeSeriesBuffer(8, maxCapacity: 8);
        for (int i = 0; i < 10; i++) // wraps: exercises the ring, not just a fresh buffer
        {
            buffer.Add(i, i * 2f);
        }

        Assert.Equal(CopyFindsData(buffer, windowStart), HasDataInWindow(buffer, windowStart));
    }

    [Fact]
    public void ClearingForANewSessionMakesTheChannelAbsentAgain()
    {
        var buffer = new TimeSeriesBuffer(16);
        buffer.Add(1, 1);
        Assert.True(HasDataInWindow(buffer, 0));

        buffer.Clear();

        Assert.False(HasDataInWindow(buffer, 0));
        Assert.False(CopyFindsData(buffer, 0));
    }
}

/// <summary>
/// Depth behaviour. The plots show a whole run rather than a trailing window, so the buffer grows
/// with what has actually been recorded instead of allocating for the worst case up front — and
/// says so when a run finally outgrows the ceiling and its start is no longer on screen.
/// </summary>
public class TimeSeriesBufferGrowthTests
{
    [Fact]
    public void GrowsInsteadOfDroppingUntilTheCeilingIsReached()
    {
        var buffer = new TimeSeriesBuffer(initialCapacity: 4, maxCapacity: 64);
        for (int i = 0; i < 40; i++)
        {
            buffer.Add(i, i);
        }

        Assert.Equal(40, buffer.Count);
        Assert.False(buffer.HasDroppedSamples);
        Assert.Equal(0.0, buffer.EarliestTime); // the very first sample is still there
        Assert.Equal(39.0, buffer.LatestTime);
        Assert.True(buffer.Capacity >= 40);
        Assert.True(buffer.Capacity <= 64);
    }

    [Fact]
    public void GrowthPreservesEverySampleInOrder()
    {
        var buffer = new TimeSeriesBuffer(initialCapacity: 2, maxCapacity: 1024);
        for (int i = 0; i < 100; i++)
        {
            buffer.Add(i * 0.5, i);
        }

        var times = new double[buffer.Capacity];
        var values = new float[buffer.Capacity];
        int count = buffer.CopyWindow(double.MinValue, times, values);

        Assert.Equal(100, count);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(i * 0.5, times[i]);
            Assert.Equal(i, values[i]);
        }
    }

    [Fact]
    public void GrowthAfterAWrapStillPreservesOrder()
    {
        // Fill to the initial capacity and past it so _start is mid-array, then force a grow.
        var buffer = new TimeSeriesBuffer(initialCapacity: 4, maxCapacity: 4);
        for (int i = 0; i < 6; i++)
        {
            buffer.Add(i, i);
        }
        Assert.True(buffer.HasDroppedSamples);

        var times = new double[buffer.Capacity];
        var values = new float[buffer.Capacity];
        int count = buffer.CopyWindow(double.MinValue, times, values);
        Assert.Equal([2.0, 3.0, 4.0, 5.0], times.Take(count));
    }

    [Fact]
    public void PastTheCeilingTheOldestSamplesRollOffAndItIsReported()
    {
        var buffer = new TimeSeriesBuffer(initialCapacity: 4, maxCapacity: 8);
        for (int i = 0; i < 8; i++)
        {
            buffer.Add(i, i);
        }
        Assert.False(buffer.HasDroppedSamples);

        buffer.Add(8, 8);

        Assert.True(buffer.HasDroppedSamples);
        Assert.Equal(8, buffer.Count);
        Assert.Equal(1.0, buffer.EarliestTime); // sample 0 is gone: the run's start is lost
    }

    [Fact]
    public void ClearForgetsThatSamplesWereEverDropped()
    {
        var buffer = new TimeSeriesBuffer(initialCapacity: 2, maxCapacity: 2);
        for (int i = 0; i < 5; i++)
        {
            buffer.Add(i, i);
        }
        Assert.True(buffer.HasDroppedSamples);

        buffer.Clear();

        Assert.False(buffer.HasDroppedSamples);
        Assert.Equal(0, buffer.Count);
    }
}
