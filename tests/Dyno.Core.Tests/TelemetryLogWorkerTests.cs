using Dyno.Core;
using Dyno.Core.Messages;
using Dyno.Core.Protocol;
using Xunit;

namespace Dyno.Core.Tests;

public class TelemetryLogWorkerTests
{
    private static ForceSensorSample Sample(uint timestamp) =>
        new(
            task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115,
            new forcesensor_output_data
            {
                timestamp = timestamp,
                force = 1.5f,
                raw_value = 7,
            }
        );

    [Fact]
    public void Dispose_DrainsEverythingEnqueued_ToTheWriter()
    {
        var sink = new StringWriter { NewLine = "\n" };
        var worker = new TelemetryLogWorker(new TelemetryLogger(sink));

        for (uint i = 0; i < 500; i++)
        {
            worker.Enqueue(Sample(i));
        }
        worker.Dispose();

        // Header + one row per sample: nothing queued may be lost by an orderly shutdown.
        string[] lines = sink.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(501, lines.Length);
        Assert.Equal(TelemetryLogger.Header, lines[0]);
    }

    [Fact]
    public void Enqueue_AfterDispose_DoesNotThrow()
    {
        var worker = new TelemetryLogWorker(new TelemetryLogger(new StringWriter()));
        worker.Dispose();

        worker.Enqueue(Sample(1)); // silently dropped: the link outlives the log file
    }

    [Fact]
    public void AFullQueue_DropsRows_AndReportsTheCount_InsteadOfBlocking()
    {
        var sink = new StringWriter { NewLine = "\n" };
        var logger = new TelemetryLogger(sink);
        // Capacity 1 and a queue nobody drains yet: fill it, then overflow it. The worker thread
        // races us for the first item, so assert on the *sum* of written + dropped, not either.
        var worker = new TelemetryLogWorker(logger, capacity: 1);
        int dropped = 0;
        worker.RowsDropped += n => Interlocked.Add(ref dropped, n);

        for (uint i = 0; i < 200; i++)
        {
            worker.Enqueue(Sample(i));
        }
        worker.Dispose(); // drains what queued, reports what didn't

        int written = sink.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;
        Assert.Equal(200, written + dropped);
    }
}
