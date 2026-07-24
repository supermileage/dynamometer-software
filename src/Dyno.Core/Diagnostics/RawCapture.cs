using System.Buffers.Binary;
using System.Threading.Channels;

namespace Dyno.Core.Diagnostics;

/// <summary>One chunk exactly as the serial stack handed it over, with when it arrived.</summary>
public readonly record struct CapturedChunk(long Index, DateTime TimestampUtc, byte[] Bytes);

/// <summary>
/// TEMP DIAGNOSTIC (16-byte head-loss investigation): records every chunk
/// <c>DeviceClient</c>'s read loop receives, byte for byte, so the same bytes can be replayed
/// through a fresh <see cref="Protocol.StreamParser"/> offline.
/// </summary>
/// <remarks>
/// This exists to settle one question: when a batch arrives short, were the missing bytes ever
/// handed to us? Replay is deterministic over the captured bytes, so if it reproduces the same
/// shortfall the bytes were already gone when the driver delivered them — the loss is at or below
/// <c>SerialStream.ReadAsync</c> and no parser change can recover it. If replay comes back clean,
/// the fault is ours and lives in how the live path drives the parser.
///
/// The writing is done on its own thread behind an unbounded channel because a slow reader is
/// itself a suspect: an instrument that blocked the read loop on file I/O could manufacture the
/// very stall it is meant to observe. The read loop's only cost here is a copy and an enqueue.
///
/// Enabled by setting <see cref="EnvironmentVariable"/> to a path; absent, nothing is captured and
/// nothing is written.
/// </remarks>
public sealed class RawCapture : IDisposable
{
    /// <summary>Set to a file path to record; unset (the normal case) disables capture entirely.</summary>
    public const string EnvironmentVariable = "DYNO_RAW_CAPTURE";

    private const int HeaderSize = 12; // int64 ticks + int32 length
    private static ReadOnlySpan<byte> Magic => "DYNORAW1"u8;

    private readonly Channel<CapturedChunk> _queue = Channel.CreateUnbounded<CapturedChunk>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );
    private readonly Task _writer;
    private readonly Stream _file;
    private long _index;
    private int _disposed;

    public RawCapture(Stream destination)
    {
        _file = destination;
        _file.Write(Magic);
        _writer = Task.Run(DrainAsync);
    }

    /// <summary>The capture requested by <see cref="EnvironmentVariable"/>, or null when unset or
    /// unopenable — a diagnostic must never be the reason the link fails to start.</summary>
    /// <param name="problem">Why no capture was opened despite one being asked for; null when the
    /// variable was unset (nothing was asked for) or when the capture opened fine. A capture that
    /// silently fails to record is worse than none at all: the run looks like it was captured, and
    /// the absence of a file is not discovered until the fault has already been reproduced and
    /// lost. So the reason travels back to the caller to be shown.</param>
    public static RawCapture? FromEnvironment(out string? problem)
    {
        problem = null;
        string? path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            // Create the directory rather than fail on it. The variable names a file, and a
            // capture refusing to start because its folder does not exist yet is a pure obstacle.
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            return new RawCapture(File.Create(path));
        }
        catch (Exception ex)
        {
            problem = $"{path}: {ex.Message}";
            return null;
        }
    }

    /// <summary>Records one chunk. Copies, because the caller reuses its read buffer.</summary>
    public void Record(ReadOnlySpan<byte> chunk)
    {
        if (Volatile.Read(ref _disposed) != 0 || chunk.IsEmpty)
        {
            return;
        }
        var captured = new CapturedChunk(_index++, DateTime.UtcNow, chunk.ToArray());
        _queue.Writer.TryWrite(captured); // unbounded: only fails once completed
    }

    private async Task DrainAsync()
    {
        var header = new byte[HeaderSize];
        try
        {
            await foreach (var chunk in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                BinaryPrimitives.WriteInt64LittleEndian(header, chunk.TimestampUtc.Ticks);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), chunk.Bytes.Length);
                await _file.WriteAsync(header).ConfigureAwait(false);
                await _file.WriteAsync(chunk.Bytes).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A capture that fails mid-run has still recorded everything up to the failure, and
            // that partial file is the evidence. Losing the link over it would not be a trade.
        }
    }

    /// <summary>Reads a capture back in order. Streams rather than materializing: a long run is
    /// tens of megabytes.</summary>
    public static IEnumerable<CapturedChunk> Read(string path)
    {
        using var file = File.OpenRead(path);
        var magic = new byte[Magic.Length];
        if (!TryFill(file, magic) || !magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException($"{path} is not a raw capture (bad magic)");
        }

        var header = new byte[HeaderSize];
        for (long index = 0; TryFill(file, header); index++)
        {
            long ticks = BinaryPrimitives.ReadInt64LittleEndian(header);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            if (length <= 0)
            {
                throw new InvalidDataException($"{path}: chunk {index} declares {length} bytes");
            }
            var bytes = new byte[length];
            if (!TryFill(file, bytes))
            {
                // A capture cut off by a crash or a kill ends mid-record; everything before it is
                // still sound, so stop cleanly instead of failing the whole read.
                yield break;
            }
            yield return new CapturedChunk(index, new DateTime(ticks, DateTimeKind.Utc), bytes);
        }
    }

    private static bool TryFill(Stream source, Span<byte> destination)
    {
        int filled = 0;
        while (filled < destination.Length)
        {
            int read = source.Read(destination[filled..]);
            if (read == 0)
            {
                return false;
            }
            filled += read;
        }
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _queue.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // drained or faulted; either way the file is as complete as it will get
        }
        _file.Dispose();
    }
}
