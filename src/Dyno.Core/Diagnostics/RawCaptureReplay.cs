using System.Text;
using Dyno.Core.Protocol;

namespace Dyno.Core.Diagnostics;

/// <summary>A parser fault, tied to the chunk that was being appended when it fired.</summary>
/// <param name="ChunkIndex">Which delivered chunk the parser was consuming.</param>
/// <param name="ChunkOffset">Byte offset of that chunk's first byte within the whole capture.</param>
/// <param name="ChunkLength">How many bytes that chunk carried.</param>
public sealed record ReplayFault(
    long ChunkIndex,
    long ChunkOffset,
    int ChunkLength,
    string Description
);

/// <summary>What a replay found. <paramref name="Shortfalls"/> is the number that matters: it is
/// the count of batches that arrived short <em>from bytes already on disk</em>.</summary>
public sealed record ReplayReport(
    long Chunks,
    long Bytes,
    int Messages,
    int Resyncs,
    int Shortfalls,
    int SequenceGaps,
    IReadOnlyList<ReplayFault> Faults
);

/// <summary>
/// TEMP DIAGNOSTIC (16-byte head-loss investigation): replays a <see cref="RawCapture"/> through a
/// fresh <see cref="StreamParser"/>, attributing every fault to the chunk being consumed when it
/// fired.
/// </summary>
/// <remarks>
/// The verdict is in whether faults reproduce at all. The capture holds precisely what
/// <c>SerialStream.ReadAsync</c> returned, and replay is deterministic over it, so a shortfall that
/// shows up here is a shortfall in the bytes the host was given — nothing above the driver could
/// have caused it, and nothing above the driver can fix it. A capture that replays clean while the
/// live run reported faults says the opposite, and puts the bug in our own live path.
///
/// The chunk attribution is the follow-up question: loss that lands exactly on a chunk boundary
/// looks like a delivery that skipped bytes, whereas loss in a chunk's interior would mean bytes
/// went missing inside a single delivered buffer.
/// </remarks>
public static class RawCaptureReplay
{
    public static ReplayReport Replay(IEnumerable<CapturedChunk> chunks)
    {
        var parser = new StreamParser();
        var faults = new List<ReplayFault>();
        long chunkCount = 0;
        long bytes = 0;
        int messages = 0;
        int resyncs = 0;
        int shortfalls = 0;
        int gaps = 0;

        // Mutable cursor over the chunk currently being appended: the parser raises its events
        // synchronously from inside Append, so whatever this holds at that moment is the chunk
        // that carried the bytes which triggered the fault.
        long currentIndex = 0;
        long currentOffset = 0;
        int currentLength = 0;

        void Record(string description) =>
            faults.Add(new ReplayFault(currentIndex, currentOffset, currentLength, description));

        parser.MessageReceived += _ => messages++;
        parser.Resynced += details =>
        {
            resyncs++;
            Record(
                $"resync: dropped {details.BytesDropped} B "
                    + $"(skipped {Convert.ToHexString(details.SkippedBytes)})"
            );
        };
        parser.BatchMisaccounted += batch =>
        {
            if (batch.MissingTransfers > 0)
            {
                gaps++;
                Record(
                    $"sequence gap: {batch.MissingTransfers} transfer(s) missing before "
                        + $"batch #{batch.Sequence}"
                );
            }
            else
            {
                shortfalls++;
                Record(
                    $"batch #{batch.Sequence} short by {batch.Shortfall} B "
                        + $"({batch.ObservedBytes} of {batch.DeclaredBytes})"
                );
            }
        };

        foreach (var chunk in chunks)
        {
            currentIndex = chunk.Index;
            currentOffset = bytes;
            currentLength = chunk.Bytes.Length;
            parser.Append(chunk.Bytes);
            chunkCount++;
            bytes += chunk.Bytes.Length;
        }

        return new ReplayReport(chunkCount, bytes, messages, resyncs, shortfalls, gaps, faults);
    }

    /// <summary>Human-readable report, including the verdict the whole exercise exists to reach.</summary>
    public static string Format(ReplayReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(
            $"{report.Chunks} chunks, {report.Bytes} bytes, {report.Messages} messages decoded"
        );
        text.AppendLine(
            $"{report.Resyncs} resync(s), {report.Shortfalls} short batch(es), "
                + $"{report.SequenceGaps} sequence gap(s)"
        );
        text.AppendLine();

        foreach (var fault in report.Faults)
        {
            text.AppendLine(
                $"  chunk #{fault.ChunkIndex} @ byte {fault.ChunkOffset} "
                    + $"({fault.ChunkLength} B): {fault.Description}"
            );
        }

        text.AppendLine();
        text.AppendLine(
            report.Shortfalls > 0 || report.SequenceGaps > 0
                ? "VERDICT: the bytes were already missing when the driver delivered them. The loss "
                    + "is at or below SerialStream.ReadAsync — not in the parser, and not in the firmware."
                : "VERDICT: this capture replays clean. If the live run reported short batches over "
                    + "these same bytes, the fault is in our live path, not below it."
        );
        return text.ToString();
    }
}
