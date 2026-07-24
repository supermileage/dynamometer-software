using System.Globalization;
using Dyno.Core.Export;
using Dyno.Core.Plotting;
using Xunit;

namespace Dyno.Core.Tests;

/// <summary>
/// The export is long and sparse: one row per instant, carrying only what was measured then. That
/// shape is forced by the device — force, encoder and BPM samples come from independent tasks and
/// never share a timestamp — so the interesting cases are which fields land together on a row and
/// which are marked absent.
/// </summary>
public class TelemetryExporterTests
{
    private static TimeSeriesBuffer Buffer(params (double Time, float Value)[] samples)
    {
        var buffer = new TimeSeriesBuffer();
        foreach (var (time, value) in samples)
        {
            buffer.Add(time, value);
        }
        return buffer;
    }

    /// <summary>Exports with a stand-in time column, so these tests cover the row shape rather
    /// than how the app happens to render an instant.</summary>
    private static string[] Export(params ExportChannel[] channels)
    {
        var writer = new StringWriter();
        TelemetryExporter.Write(
            writer,
            channels,
            "t",
            time => time.ToString("0.000", CultureInfo.InvariantCulture)
        );
        return writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void HeaderLeadsWithElapsedTimeThenEveryChannel()
    {
        var lines = Export(
            new ExportChannel("force_n", Buffer((0.0, 1f))),
            new ExportChannel("torque_nm", Buffer((0.0, 2f)))
        );

        Assert.Equal("t,force_n,torque_nm", lines[0]);
    }

    [Fact]
    public void AChannelWithNoSampleAtAnInstantIsMarkedAbsent()
    {
        // Force at t=0, torque at t=1: two rows, each with the other field marked.
        var lines = Export(
            new ExportChannel("force_n", Buffer((0.0, 10f))),
            new ExportChannel("torque_nm", Buffer((1.0, 20f)))
        );

        Assert.Equal(3, lines.Length); // header + two rows
        Assert.Equal("0.000,10,X", lines[1]);
        Assert.Equal("1.000,X,20", lines[2]);
    }

    /// <summary>Fields recorded from one device message share a timestamp exactly, so they belong
    /// on one row — velocity and acceleration from an encoder sample, torque/geared/power from a
    /// derived reading.</summary>
    [Fact]
    public void FieldsSharingAnInstantShareARow()
    {
        var lines = Export(
            new ExportChannel("angular_velocity_rad_s", Buffer((2.5, 60f))),
            new ExportChannel("angular_acceleration_rad_s2", Buffer((2.5, -3f))),
            new ExportChannel("force_n", Buffer((2.5, 12f)))
        );

        Assert.Equal(2, lines.Length); // header + one row
        Assert.Equal("2.500,60,-3,12", lines[1]);
    }

    [Fact]
    public void RowsComeOutInTimeOrderAcrossChannels()
    {
        var lines = Export(
            new ExportChannel("a", Buffer((0.0, 1f), (2.0, 3f))),
            new ExportChannel("b", Buffer((1.0, 2f), (3.0, 4f)))
        );

        Assert.Equal(["0.000,1,X", "1.000,X,2", "2.000,3,X", "3.000,X,4"], lines.Skip(1));
    }

    [Fact]
    public void EverySampleAppearsExactlyOnce()
    {
        var a = Buffer(Enumerable.Range(0, 50).Select(i => (i * 0.01, (float)i)).ToArray());
        var b = Buffer(Enumerable.Range(0, 30).Select(i => (i * 0.017, (float)i)).ToArray());

        var lines = Export(new ExportChannel("a", a), new ExportChannel("b", b));

        int aCells = lines.Skip(1).Count(l => l.Split(',')[1] != TelemetryExporter.Missing);
        int bCells = lines.Skip(1).Count(l => l.Split(',')[2] != TelemetryExporter.Missing);
        Assert.Equal(50, aCells);
        Assert.Equal(30, bCells);
    }

    [Fact]
    public void NoSamplesYieldsAHeaderOnly()
    {
        var writer = new StringWriter();
        int rows = TelemetryExporter.Write(
            writer,
            [new ExportChannel("force_n", new TimeSeriesBuffer())],
            "device_ts",
            _ => "0"
        );

        Assert.Equal(0, rows);
        Assert.Equal("device_ts,force_n", writer.ToString().Trim());
    }

    [Fact]
    public void TheRowCountIsReported()
    {
        var writer = new StringWriter();
        int rows = TelemetryExporter.Write(
            writer,
            [new ExportChannel("a", Buffer((0.0, 1f), (1.0, 2f), (2.0, 3f)))],
            "device_ts",
            _ => "0"
        );

        Assert.Equal(3, rows);
    }

    /// <summary>A stop/start leaves a real hole in the timeline; the export simply shows the jump,
    /// since a row exists only where something was measured.</summary>
    [Fact]
    public void AGapBetweenRunsIsVisibleAsAJumpInElapsedTime()
    {
        var lines = Export(new ExportChannel("force_n", Buffer((0.0, 1f), (0.01, 2f), (40.0, 3f))));

        Assert.Equal("0.000,1", lines[1]);
        Assert.Equal("0.010,2", lines[2]);
        Assert.Equal("40.000,3", lines[3]);
    }

    /// <summary>Values are round-tripped at full float precision rather than rounded for display —
    /// the file is the record, and re-deriving from it must not accumulate error.</summary>
    [Fact]
    public void ValuesKeepFullPrecision()
    {
        var lines = Export(new ExportChannel("a", Buffer((0.0, 0.1234567f))));

        Assert.Equal("0.000,0.1234567", lines[1]);
    }

    /// <summary>The time column is rendered by the caller, which is how the app puts the device's
    /// own timestamp there instead of the elapsed seconds the buffers hold.</summary>
    [Fact]
    public void TheTimeColumnIsRenderedByTheCaller()
    {
        var writer = new StringWriter();
        TelemetryExporter.Write(
            writer,
            [new ExportChannel("force_n", Buffer((0.0, 1f), (0.25, 2f)))],
            "device_ts",
            seconds =>
                (1_000_000 + (ulong)Math.Round(seconds * 1_000_000)).ToString(
                    CultureInfo.InvariantCulture
                )
        );

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("device_ts,force_n", lines[0]);
        Assert.Equal("1000000,1", lines[1]);
        Assert.Equal("1250000,2", lines[2]);
    }
}
