namespace Dyno.Core.Plotting;

/// <summary>
/// Turns the device's raw 32-bit timestamp counter into a continuously increasing number of
/// seconds.
/// </summary>
/// <remarks>
/// The device stamps samples with TIM2's free-running counter at 1 MHz (see
/// <c>firmware/Core/Inc/TimeKeeping/timestamps.h</c>), so a tick is 1 µs and the counter rolls
/// over every 2^32 µs ≈ 71.6 minutes. Plotted raw, a session crossing that boundary would jump
/// backwards by 71 minutes; every rollover is instead folded into a running 64-bit tick count.
///
/// One instance covers every channel, because every task stamps from the same counter. That also
/// means samples arrive slightly out of order — different tasks are read in turn, so a few ms of
/// skew between channels is normal. Rollovers and skew are told apart by size: any step is read as
/// the shorter of the two ways round the counter, so a jump of less than half the range (≈35.8
/// minutes) is movement and only a larger one could be a rollover, which no interleaving can fake.
///
/// Not thread-safe: called from the UI thread alongside the buffers it feeds.
/// </remarks>
public sealed class TimestampUnwrapper
{
    /// <summary>Device timer rate: TIM2 at 1 MHz, one tick per microsecond.</summary>
    public const double TicksPerSecond = 1_000_000.0;

    private uint _previous; // raw counter value of the last sample that moved time forward
    private ulong _reference; // that same sample, unwrapped
    private bool _started;

    /// <summary>Total ticks since the first sample seen, unwrapped.</summary>
    public ulong ToTicks(uint raw)
    {
        if (!_started)
        {
            _started = true;
            _previous = raw;
            _reference = raw;
            return raw;
        }

        // Modular subtraction reinterpreted as signed gives the true distance from the last
        // sample in [-2^31, 2^31), which is what makes both awkward cases fall out for free: a
        // sample just past a rollover reads as a small positive step, and a straggler from just
        // before one reads as a small negative step rather than a lap into the future.
        int delta = unchecked((int)(raw - _previous));
        long ticks = (long)_reference + delta;
        if (ticks < 0)
        {
            ticks = 0; // a straggler older than the very first sample seen
        }

        // Only forward progress moves the reference; out-of-order samples are positioned against
        // it without becoming it, so ordinary inter-task skew cannot drag the clock backwards.
        if (delta > 0)
        {
            _previous = raw;
            _reference = (ulong)ticks;
        }

        return (ulong)ticks;
    }

    /// <summary>Unwrapped seconds for a raw device timestamp.</summary>
    public double ToSeconds(uint raw) => ToTicks(raw) / TicksPerSecond;

    /// <summary>Forgets the counter's history, so the next sample starts a fresh reference.</summary>
    public void Reset()
    {
        _previous = 0;
        _reference = 0;
        _started = false;
    }
}
