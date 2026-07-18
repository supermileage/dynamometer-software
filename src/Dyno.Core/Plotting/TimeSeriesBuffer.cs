namespace Dyno.Core.Plotting;

/// <summary>
/// Fixed-capacity ring of (time, value) samples for one plotted channel. Appends drop the oldest
/// sample once full, so it always holds the most recent stretch of a stream — which is all a
/// scrolling time plot can show anyway.
/// </summary>
/// <remarks>
/// Not thread-safe: writes and reads must come from one thread (the UI thread — samples arrive
/// through the same dispatcher post that updates the readouts). Times must be non-decreasing;
/// <see cref="CopyWindow"/> binary-searches on that assumption.
/// </remarks>
public sealed class TimeSeriesBuffer
{
    private readonly double[] _times;
    private readonly float[] _values;
    private int _start; // physical index of the oldest sample
    private int _count;

    public TimeSeriesBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _times = new double[capacity];
        _values = new float[capacity];
    }

    public int Capacity => _times.Length;

    public int Count => _count;

    /// <summary>Time of the newest sample; meaningless (0) while empty — check <see cref="Count"/>.</summary>
    public double LatestTime => _count == 0 ? 0 : _times[Physical(_count - 1)];

    public void Add(double time, float value)
    {
        int index = Physical(_count);
        _times[index] = time;
        _values[index] = value;
        if (_count < Capacity)
        {
            _count++;
        }
        else
        {
            _start = Physical(1); // overwrote the oldest; the next one along is now oldest
        }
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
    }

    /// <summary>
    /// Copies every sample with <c>time &gt;= fromTime</c>, oldest first, into the supplied arrays
    /// (each at least <see cref="Capacity"/> long) and returns how many were written.
    /// </summary>
    public int CopyWindow(double fromTime, double[] times, float[] values)
    {
        // Binary search for the first logical index whose time is >= fromTime.
        int lo = 0;
        int hi = _count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_times[Physical(mid)] < fromTime)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        int written = 0;
        for (int i = lo; i < _count; i++)
        {
            int index = Physical(i);
            times[written] = _times[index];
            values[written] = _values[index];
            written++;
        }
        return written;
    }

    private int Physical(int logical) => (_start + logical) % Capacity;
}
