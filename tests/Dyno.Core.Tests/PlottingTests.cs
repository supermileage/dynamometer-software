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
        var buffer = new TimeSeriesBuffer(4);
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
