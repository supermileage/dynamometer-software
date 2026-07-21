using Dyno.Core.Diagnostics;
using Dyno.Core.Messages;
using Xunit;

namespace Dyno.Core.Tests;

public class RawCaptureTests
{
    private static byte[] Encoder(uint timestamp) =>
        Wire.Message(
            usb_msg_type_t.USB_MSG_STREAM,
            task_offset_t.TASK_OFFSET_OPTICAL_ENCODER,
            new optical_encoder_output_data { timestamp = timestamp }
        );

    /// <summary>Captures to a real file and hands back the path; the capture is closed on return,
    /// so the file is complete and readable.</summary>
    private static string CaptureToFile(params byte[][] chunks)
    {
        string path = Path.Combine(Path.GetTempPath(), $"dyno-raw-{Guid.NewGuid():N}.bin");
        using (var capture = new RawCapture(File.Create(path)))
        {
            foreach (var chunk in chunks)
            {
                capture.Record(chunk);
            }
        }
        return path;
    }

    [Fact]
    public void Capture_RoundTripsChunkBoundariesAndBytes()
    {
        // Chunk boundaries are evidence, not an implementation detail: where a delivery started and
        // stopped is what tells a gap at a boundary from one inside a single delivered buffer. A
        // capture that concatenated its chunks would lose exactly the thing it was opened to record.
        byte[][] chunks =
        [
            [1, 2, 3],
            [4],
            [5, 6, 7, 8, 9],
        ];
        string path = CaptureToFile(chunks);

        try
        {
            var read = RawCapture.Read(path).ToList();

            Assert.Equal(3, read.Count);
            Assert.Equal([0, 1, 2], read.Select(c => c.Index));
            Assert.Equal(chunks, read.Select(c => c.Bytes));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Capture_IgnoresEmptyChunks()
    {
        // A zero-length read means the loop was woken with nothing to show, not that a zero-byte
        // delivery happened. Recording it would invent a chunk boundary the driver never drew.
        string path = CaptureToFile([1, 2], [], [3]);

        try
        {
            Assert.Equal(2, RawCapture.Read(path).Count());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TruncatedCapture_YieldsTheCompleteRecordsAndStops()
    {
        // A capture is most valuable when the run it was recording died badly, which is precisely
        // when the file ends mid-record. Everything written before that point is still sound.
        string path = CaptureToFile([1, 2, 3], [4, 5, 6]);

        try
        {
            byte[] whole = File.ReadAllBytes(path);
            File.WriteAllBytes(path, whole[..^2]); // clip into the final chunk's payload

            var read = RawCapture.Read(path).ToList();

            Assert.Equal([1, 2, 3], Assert.Single(read).Bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CaptureWithoutMagic_IsRejected()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dyno-raw-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, [0, 1, 2, 3, 4, 5, 6, 7, 8]);

        try
        {
            // Enumerating is what runs the check, hence ToList rather than the call alone.
            Assert.Throws<InvalidDataException>(() => RawCapture.Read(path).ToList());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RecordAfterDispose_IsIgnored()
    {
        // The read loop and Dispose race by construction: Stop() unblocks a pending read, which can
        // deliver one more chunk on its way out. Losing that chunk is fine; faulting is not.
        string path = Path.Combine(Path.GetTempPath(), $"dyno-raw-{Guid.NewGuid():N}.bin");
        var capture = new RawCapture(File.Create(path));
        capture.Dispose();

        try
        {
            capture.Record([1, 2, 3]);

            Assert.Empty(RawCapture.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Replay_ReproducesAShortfallFromTheCapturedBytes()
    {
        // The field fault, delivered as the driver would: a transfer whose leading 16 bytes are
        // already absent from the bytes handed over. Replay reproducing it is what proves the loss
        // happened at or below the read, since nothing above it touched this file.
        byte[] first = Wire.Batch(1, Encoder(10));
        byte[] second = Wire.Batch(2, Encoder(20), Encoder(30));
        string path = CaptureToFile(first, second[16..]);

        try
        {
            var report = RawCaptureReplay.Replay(RawCapture.Read(path));

            Assert.Equal(1, report.Shortfalls);
            Assert.Equal(0, report.SequenceGaps);

            // Attributed to the chunk that was short, not the healthy one before it.
            var fault = report.Faults.Single(f => f.Description.Contains("short by 16 B"));
            Assert.Equal(1, fault.ChunkIndex);
            Assert.Equal(first.Length, fault.ChunkOffset);
            Assert.Contains("at or below SerialStream.ReadAsync", RawCaptureReplay.Format(report));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Replay_OfAnIntactCaptureIsClean()
    {
        // The other half of the verdict, and the one that would indict us: bytes that all arrived,
        // replayed without complaint. If a live run faults over a capture that replays like this,
        // the fault is in how the live path drives the parser.
        string path = CaptureToFile(Wire.Batch(1, Encoder(10)), Wire.Batch(2, Encoder(20)));

        try
        {
            var report = RawCaptureReplay.Replay(RawCapture.Read(path));

            Assert.Equal(0, report.Shortfalls);
            Assert.Equal(0, report.SequenceGaps);
            Assert.Equal(0, report.Resyncs);
            Assert.Equal(2, report.Messages);
            Assert.Contains("replays clean", RawCaptureReplay.Format(report));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Replay_SurvivesChunkBoundariesMidFrame()
    {
        // Deliveries cut records in half all the time; that is normal and must not read as loss.
        // Without this, the diagnostic would report a fault on every healthy capture.
        byte[] stream = [.. Wire.Batch(1, Encoder(10)), .. Wire.Batch(2, Encoder(20))];
        var chunks = new List<byte[]>();
        for (int i = 0; i < stream.Length; i += 7)
        {
            chunks.Add(stream[i..Math.Min(i + 7, stream.Length)]);
        }
        string path = CaptureToFile([.. chunks]);

        try
        {
            var report = RawCaptureReplay.Replay(RawCapture.Read(path));

            Assert.Empty(report.Faults);
            Assert.Equal(2, report.Messages);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
