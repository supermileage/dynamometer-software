using System.Text;
using Dyno.Core.Protocol;

namespace Dyno.Core.Diagnostics;

/// <summary>A parser fault, tied to the chunk that was being appended when it fired.</summary>
/// <param name="ChunkIndex">Which delivered chunk the parser was consuming.</param>
/// <param name="ChunkOffset">Byte offset of that chunk's first byte within the whole capture.</param>
/// <param name="ChunkLength">How many bytes that chunk carried.</param>
/// <param name="Context">The last few deliveries up to and including the faulting one, in order.
/// This is what says <em>where</em> the bytes went missing: if the gap falls exactly between two
/// chunks, the driver skipped bytes between deliveries, whereas a gap inside a single chunk would
/// mean one delivered buffer arrived internally discontinuous — a much stranger fault, and a
/// different bug.</param>
public sealed record ReplayFault(
    long ChunkIndex,
    long ChunkOffset,
    int ChunkLength,
    string Description,
    IReadOnlyList<CapturedChunk> Context
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
    /// <summary>How many deliveries of run-up to keep with each fault. Enough to cover the faulting
    /// batch and the healthy one before it, so the gap has a known-good neighbour to be read
    /// against.</summary>
    private const int ContextChunks = 6;

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

        // The deliveries leading up to whatever fires, kept so a fault can show its own
        // neighbourhood. Faults are rare enough (single digits across a million bytes) that
        // carrying this costs nothing, and without it every fault needs a second run to explain.
        var recent = new Queue<CapturedChunk>();

        void Record(string description) =>
            faults.Add(
                new ReplayFault(
                    currentIndex,
                    currentOffset,
                    currentLength,
                    description,
                    [.. recent]
                )
            );

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
            recent.Enqueue(chunk);
            while (recent.Count > ContextChunks)
            {
                recent.Dequeue();
            }
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
            foreach (var chunk in fault.Context)
            {
                // One line per delivery, so a gap that falls on a boundary shows up as one: the
                // bytes are contiguous within a line by definition, and only the joins are in doubt.
                string marker = chunk.Index == fault.ChunkIndex ? ">>" : "  ";
                text.AppendLine(
                    $"    {marker} #{chunk.Index} ({chunk.Bytes.Length, 4} B) "
                        + Convert.ToHexString(chunk.Bytes)
                );
            }
            text.AppendLine();
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
