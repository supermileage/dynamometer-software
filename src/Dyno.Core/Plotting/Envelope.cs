namespace Dyno.Core.Plotting;

/// <summary>
/// Min/max decimation for drawing a dense time series as a polyline. A window can hold tens of
/// thousands of samples but a plot is only a few hundred pixels wide; drawing every sample wastes
/// time and anti-aliases the line into mush. Keeping the per-pixel-column extremes instead
/// preserves exactly what the eye could ever see at that width — every spike survives, at a few
/// points per column.
/// </summary>
public static class Envelope
{
    /// <summary>
    /// Reduces <paramref name="count"/> samples to at most 2 points per bucket (the bucket's min
    /// and max, emitted in time order), writing them into <paramref name="outTimes"/> /
    /// <paramref name="outValues"/> (each at least <c>2 * bucketCount</c> long). Buckets divide
    /// [<paramref name="t0"/>, <paramref name="t1"/>) evenly; buckets with one sample pass it
    /// through, and empty buckets emit nothing — a gap in the data stays a gap in the line.
    /// Returns the number of points written. Input times must be non-decreasing.
    /// </summary>
    public static int Decimate(
        double[] times,
        float[] values,
        int count,
        double t0,
        double t1,
        int bucketCount,
        double[] outTimes,
        float[] outValues
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bucketCount, 1);
        if (count <= 0 || t1 <= t0)
        {
            return 0;
        }

        double bucketWidth = (t1 - t0) / bucketCount;
        int written = 0;
        int i = 0;

        // Group each run by the samples' own computed bucket index (not a precomputed bucket-end
        // time): sorted input makes the index non-decreasing, so runs cover strictly increasing
        // buckets and the output provably fits in 2 * bucketCount — float rounding at a bucket
        // boundary can shift where one bucket ends, but never revisit or split one.
        while (i < count)
        {
            int bucket = BucketOf(times[i]);

            int minIndex = i;
            int maxIndex = i;
            int last = i + 1;
            for (; last < count && BucketOf(times[last]) == bucket; last++)
            {
                if (values[last] < values[minIndex])
                {
                    minIndex = last;
                }
                if (values[last] > values[maxIndex])
                {
                    maxIndex = last;
                }
            }

            if (minIndex == maxIndex)
            {
                outTimes[written] = times[minIndex];
                outValues[written] = values[minIndex];
                written++;
            }
            else
            {
                // Both extremes, in the order they actually occurred.
                int first = Math.Min(minIndex, maxIndex);
                int second = Math.Max(minIndex, maxIndex);
                outTimes[written] = times[first];
                outValues[written] = values[first];
                written++;
                outTimes[written] = times[second];
                outValues[written] = values[second];
                written++;
            }

            i = last;
        }

        return written;

        int BucketOf(double time) =>
            Math.Clamp((int)((time - t0) / bucketWidth), 0, bucketCount - 1);
    }
}
