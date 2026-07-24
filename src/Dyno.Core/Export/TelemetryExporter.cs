using System.Globalization;
using Dyno.Core.Plotting;

namespace Dyno.Core.Export;

/// <summary>One exported column: its header and the samples behind it.</summary>
public sealed record ExportChannel(string ColumnName, TimeSeriesBuffer Buffer);

/// <summary>
/// Writes the recorded channels to a CSV in long, sparse form: one row per instant, carrying only
/// the fields actually measured at that instant, with <see cref="Missing"/> everywhere else.
/// </summary>
/// <remarks>
/// The shape follows from how the device streams. Force, encoder and BPM samples come from
/// independent tasks at different rates and are stamped from the same counter but never at the
/// same moment, so almost every row holds one source's fields and nothing else. Fields do arrive
/// in groups — an encoder sample carries velocity and acceleration together, and a derived reading
/// carries torque, geared torque and power — and those share a row because they share a timestamp
/// exactly, having been recorded from one message.
///
/// Empty cells are marked rather than left blank so that a gap is visibly a gap: a reader scanning
/// the file can tell "not measured here" from a value that failed to write. The cost is that
/// spreadsheets treat those columns as text, so charting one means replacing the marker first.
/// </remarks>
public static class TelemetryExporter
{
    /// <summary>What a field that was not measured at this instant is written as.</summary>
    public const string Missing = "X";

    /// <summary>
    /// Writes every sample in <paramref name="channels"/> and returns the row count.
    /// </summary>
    /// <param name="timeColumnName">Header for the leading time column.</param>
    /// <param name="formatTime">Renders a buffer time as that column's value. The buffers hold
    /// elapsed seconds because that is what the plots draw; the export turns each back into the
    /// device timestamp it came from, so a row can be matched against the raw telemetry log.</param>
    public static int Write(
        TextWriter writer,
        IReadOnlyList<ExportChannel> channels,
        string timeColumnName,
        Func<double, string> formatTime
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(formatTime);

        // Pin the line ending so the file is byte-for-byte identical wherever the app runs, rather
        // than following the host's Environment.NewLine — the file is the record, and its format is
        // ours to decide, not the platform's.
        writer.NewLine = "\n";

        var culture = CultureInfo.InvariantCulture;
        writer.WriteLine(
            string.Join(',', channels.Select(c => c.ColumnName).Prepend(timeColumnName))
        );

        int channelCount = channels.Count;
        var times = new double[channelCount][];
        var values = new float[channelCount][];
        var counts = new int[channelCount];
        var cursors = new int[channelCount];

        for (int i = 0; i < channelCount; i++)
        {
            var buffer = channels[i].Buffer;
            times[i] = new double[buffer.Capacity];
            values[i] = new float[buffer.Capacity];
            counts[i] = buffer.CopyWindow(double.MinValue, times[i], values[i]);
        }

        // Merge the per-channel sample lists, which are each already in time order, taking every
        // channel sitting on the earliest remaining instant at once so that fields recorded from
        // one device message land on one row.
        var cells = new string[channelCount];
        int rows = 0;
        while (true)
        {
            double next = double.MaxValue;
            bool any = false;
            for (int i = 0; i < channelCount; i++)
            {
                if (cursors[i] < counts[i] && times[i][cursors[i]] < next)
                {
                    next = times[i][cursors[i]];
                    any = true;
                }
            }
            if (!any)
            {
                break;
            }

            for (int i = 0; i < channelCount; i++)
            {
                // Exact equality is deliberate: samples sharing a row were recorded from the same
                // message, so their timestamps are the same double, not merely close.
                if (cursors[i] < counts[i] && times[i][cursors[i]] == next)
                {
                    cells[i] = values[i][cursors[i]].ToString("R", culture);
                    cursors[i]++;
                }
                else
                {
                    cells[i] = Missing;
                }
            }

            writer.Write(formatTime(next));
            for (int i = 0; i < channelCount; i++)
            {
                writer.Write(',');
                writer.Write(cells[i]);
            }
            writer.WriteLine();
            rows++;
        }

        return rows;
    }
}
