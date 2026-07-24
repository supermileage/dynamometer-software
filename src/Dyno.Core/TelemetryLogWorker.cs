using System.Threading.Channels;
using Dyno.Core.Protocol;

namespace Dyno.Core;

/// <summary>
/// Decouples CSV telemetry logging from the <see cref="DeviceClient"/> read loop: the read thread
/// calls <see cref="Enqueue"/> (never blocks, never touches a file) and a background task drains
/// the queue into a <see cref="TelemetryLogger"/>, flushing on an interval instead of per row.
/// </summary>
/// <remarks>
/// The read loop is the one thread that must never wait: while it stalls, the OS serial buffer is
/// the only thing absorbing the device's ~17 kB/s, and when that overflows bytes are lost
/// mid-record — the resync warnings in the event log. Per-row file flushes on that thread were
/// exactly such a stall (their cost grows with the file), hence this worker. If the disk falls so
/// far behind that the queue fills, telemetry rows are dropped and counted — reported via
/// <see cref="RowsDropped"/> — because a complete CSV is never worth a lossy stream.
/// </remarks>
public sealed class TelemetryLogWorker : IDisposable
{
    /// <summary>How often buffered rows are pushed to the OS. Also bounds what an unclean
    /// shutdown can lose from the file.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>~5 s of headroom at the full stream rate before rows are dropped.</summary>
    private const int DefaultCapacity = 8192;

    private readonly TelemetryLogger _logger;
    private readonly Channel<DeviceMessage> _queue;
    private readonly Task _writeLoop;
    private int _dropped; // rows rejected by a full queue since the last RowsDropped report

    /// <summary>Raised (from the writer thread, at most once per flush interval) with how many
    /// rows were dropped because the disk could not keep up. The stream itself is unaffected —
    /// that is the entire point — but the CSV has holes and the user should know.</summary>
    public event Action<int>? RowsDropped;

    public TelemetryLogWorker(TelemetryLogger logger, int capacity = DefaultCapacity)
    {
        _logger = logger;
        _queue = Channel.CreateBounded<DeviceMessage>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true, // the DeviceClient read loop
                FullMode = BoundedChannelFullMode.Wait, // makes TryWrite return false when full
            }
        );
        _writeLoop = Task.Run(WriteLoopAsync);
    }

    /// <summary>Hands a message to the writer. Constant-time on the caller's thread; a full queue
    /// drops the row (counted) rather than making the reader wait on the disk.</summary>
    public void Enqueue(DeviceMessage message)
    {
        if (!_queue.Writer.TryWrite(message))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private async Task WriteLoopAsync()
    {
        var reader = _queue.Reader;
        var sinceFlush = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (true)
            {
                // Wake on data or on the flush interval, whichever comes first — so rows written
                // just before the stream goes quiet (session stop) still reach the disk promptly.
                var wait = reader.WaitToReadAsync().AsTask();
                if (
                    await Task.WhenAny(wait, Task.Delay(FlushInterval)).ConfigureAwait(false)
                        == wait
                    && !await wait.ConfigureAwait(false)
                )
                {
                    break; // completed and drained: Dispose was called
                }

                while (reader.TryRead(out var message))
                {
                    _logger.Log(message);
                }

                if (sinceFlush.Elapsed >= FlushInterval)
                {
                    _logger.Flush();
                    sinceFlush.Restart();
                    ReportDrops();
                }
            }
        }
        catch (IOException)
        {
            // The disk went away mid-session. Telemetry logging ends here; the stream, plots and
            // readouts continue — losing the CSV must never take the link down with it.
        }
    }

    private void ReportDrops()
    {
        int dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
        {
            RowsDropped?.Invoke(dropped);
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try
        {
            _writeLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // writer faulted; fall through to dispose the file
        }
        ReportDrops();
        try
        {
            _logger.Flush();
        }
        catch (IOException)
        {
            // same disk failure the loop saw; nothing left to save
        }
        _logger.Dispose();
    }
}
