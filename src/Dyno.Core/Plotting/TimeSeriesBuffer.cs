namespace Dyno.Core.Plotting;

/// <summary>
/// Growing ring of (time, value) samples for one plotted channel. Starts small and doubles as it
/// fills, so memory tracks how much has actually been recorded; past <see cref="MaxCapacity"/> it
/// stops growing and drops the oldest sample per append.
/// </summary>
/// <remarks>
/// The plots show a whole run rather than a trailing window, so depth is the difference between
/// "since the session started" and "since a few minutes ago". At roughly 100 samples/second the
/// default ceiling holds about 87 minutes per channel; a run longer than that loses its oldest
/// samples, and the plot's left edge stops being the start of the session.
///
/// Not thread-safe: writes and reads must come from one thread (the UI thread — samples arrive
/// through the same dispatcher post that updates the readouts). Times must be non-decreasing;
/// <see cref="CopyWindow"/> binary-searches on that assumption.
/// </remarks>
public sealed class TimeSeriesBuffer
{
    public const int DefaultInitialCapacity = 4096;
    public const int DefaultMaxCapacity = 524288; // ~87 min at 100 Hz, 6.3 MB per channel

    private readonly int _maxCapacity;
    private double[] _times;
    private float[] _values;
    private int _start; // physical index of the oldest sample
    private int _count;

    public TimeSeriesBuffer(
        int initialCapacity = DefaultInitialCapacity,
        int maxCapacity = DefaultMaxCapacity
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, initialCapacity);
        _maxCapacity = maxCapacity;
        _times = new double[initialCapacity];
        _values = new float[initialCapacity];
    }

    /// <summary>Currently allocated depth. Grows up to <see cref="MaxCapacity"/> as samples arrive.</summary>
    public int Capacity => _times.Length;

    public int MaxCapacity => _maxCapacity;

    public int Count => _count;

    /// <summary>Whether the ring has started discarding the oldest samples — past this point the
    /// buffer no longer holds the whole run.</summary>
    public bool HasDroppedSamples { get; private set; }

    /// <summary>Time of the oldest retained sample; meaningless (0) while empty.</summary>
    public double EarliestTime => _count == 0 ? 0 : _times[Physical(0)];

    /// <summary>Time of the newest sample; meaningless (0) while empty — check <see cref="Count"/>.</summary>
    public double LatestTime => _count == 0 ? 0 : _times[Physical(_count - 1)];

    public void Add(double time, float value)
    {
        // The non-decreasing requirement is load-bearing well beyond this class — CopyWindow
        // binary-searches on it, Envelope.Decimate's output bound depends on it, and the CSV
        // exporter's merge assumes it — yet a violation is silent everywhere: the plot just draws
        // a line that doubles back. Feeding one buffer from two device tasks did exactly that.
        // Debug-only, so the cost is nil in a release build and the fault surfaces in testing.
        System.Diagnostics.Debug.Assert(
            _count == 0 || time >= LatestTime,
            $"TimeSeriesBuffer times must be non-decreasing; got {time} after {LatestTime}. "
                + "Is this buffer being fed from more than one device task's clock?"
        );

        if (_count == Capacity && Capacity < _maxCapacity)
        {
            Grow();
        }

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
            HasDroppedSamples = true;
        }
    }

    /// <summary>Doubles the storage (capped), re-laying the samples oldest-first so the ring
    /// starts from zero again.</summary>
    private void Grow()
    {
        int grown = (int)Math.Min((long)Capacity * 2, _maxCapacity);
        var times = new double[grown];
        var values = new float[grown];
        for (int i = 0; i < _count; i++)
        {
            int index = Physical(i);
            times[i] = _times[index];
            values[i] = _values[index];
        }
        _times = times;
        _values = values;
        _start = 0;
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
        HasDroppedSamples = false;
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
