using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Dyno.Core.Plotting;

namespace Dyno.App.Controls;

/// <summary>
/// A chart of one or more series over a whole run: the x axis always starts at 0 — the first
/// sample of the session — and extends to <see cref="AnchorTime"/>, the newest sample. It does not
/// scroll; it grows, and stops growing when the device stops streaming, so a finished run stays on
/// screen in full. Series carry different units, so there is no shared y-scale
/// and no dual axis: each series is normalized to its <em>own</em> effective range — the fixed
/// range configured in Settings, or its per-frame autoscale fit — and drawn on the common
/// vertical. The numeric labels belong to <see cref="AxisSeries"/> alone; the picker above the
/// graph chooses which. Dense data is min/max-decimated per pixel column, so spikes survive.
/// </summary>
public class TimeSeriesPlot : Control
{
    /// <summary>The series to draw, in draw order. Re-read every frame (the anchor ticks at
    /// ~30 fps), so membership changes show up without collection subscriptions.</summary>
    public static readonly StyledProperty<IReadOnlyList<IPlotSeries>?> SeriesProperty =
        AvaloniaProperty.Register<TimeSeriesPlot, IReadOnlyList<IPlotSeries>?>(nameof(Series));

    /// <summary>Which series' range labels the y-axis (gridlines and tick values). Other series
    /// still occupy the full height against their own ranges.</summary>
    public static readonly StyledProperty<IPlotSeries?> AxisSeriesProperty =
        AvaloniaProperty.Register<TimeSeriesPlot, IPlotSeries?>(nameof(AxisSeries));

    public static readonly StyledProperty<IBrush?> GridBrushProperty = AvaloniaProperty.Register<
        TimeSeriesPlot,
        IBrush?
    >(nameof(GridBrush), new SolidColorBrush(Color.Parse("#2A2F3A")));

    public static readonly StyledProperty<IBrush?> LabelBrushProperty = AvaloniaProperty.Register<
        TimeSeriesPlot,
        IBrush?
    >(nameof(LabelBrush), new SolidColorBrush(Color.Parse("#8892A2")));

    /// <summary>Right edge of the plot: seconds since the run's first sample, on the device's own
    /// clock. Advanced by the page's frame timer; every change repaints.</summary>
    public static readonly StyledProperty<double> AnchorTimeProperty = AvaloniaProperty.Register<
        TimeSeriesPlot,
        double
    >(nameof(AnchorTime));

    /// <summary>A silence longer than this breaks the line instead of being spanned by a straight
    /// segment. Stopping a session, or a dropped link, leaves a real hole in the record; drawing
    /// through it would invent readings that were never taken.</summary>
    public static readonly StyledProperty<double> GapSecondsProperty = AvaloniaProperty.Register<
        TimeSeriesPlot,
        double
    >(nameof(GapSeconds), 1.0);

    static TimeSeriesPlot()
    {
        AffectsRender<TimeSeriesPlot>(
            SeriesProperty,
            AxisSeriesProperty,
            GridBrushProperty,
            LabelBrushProperty,
            AnchorTimeProperty,
            GapSecondsProperty
        );
    }

    public IReadOnlyList<IPlotSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public IPlotSeries? AxisSeries
    {
        get => GetValue(AxisSeriesProperty);
        set => SetValue(AxisSeriesProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public double AnchorTime
    {
        get => GetValue(AnchorTimeProperty);
        set => SetValue(AnchorTimeProperty, value);
    }

    public double GapSeconds
    {
        get => GetValue(GapSecondsProperty);
        set => SetValue(GapSecondsProperty, value);
    }

    // Margins around the plot area: room for y labels left, x labels below.
    private const double MarginLeft = 48;
    private const double MarginRight = 8;
    private const double MarginTop = 6;
    private const double MarginBottom = 18;
    private const double LabelFontSize = 10;
    private const double MinimumSpanSeconds = 1; // keeps a just-started run from being degenerate

    // Scratch arrays reused across frames and series (sized to the largest buffer on first use).
    private double[]? _times;
    private float[]? _values;
    private double[]? _outTimes;
    private float[]? _outValues;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var plot = new Rect(
            MarginLeft,
            MarginTop,
            Math.Max(0, Bounds.Width - MarginLeft - MarginRight),
            Math.Max(0, Bounds.Height - MarginTop - MarginBottom)
        );
        if (plot.Width < 10 || plot.Height < 10)
        {
            return;
        }

        var typeface = new Typeface(TextElement.GetFontFamily(this));
        var series = Series;
        // The run always starts at 0 -- PlotsViewModel records each sample's device timestamp
        // relative to the first one it saw -- and the axis stretches to the newest sample.
        double t0 = 0;
        double t1 = Math.Max(AnchorTime, MinimumSpanSeconds);

        if (series is null || series.Count == 0)
        {
            DrawEmptyState(context, plot, typeface, "no channels on this graph — toggle one above");
            return;
        }

        // The axis is labeled from one series' range; everything else only needs its own range at
        // draw time. The pick must be a series that actually has data — pointing the axis at a
        // silent channel would otherwise leave the graph with no gridlines at all, hiding the
        // scale of the lines that are drawn. Falls back to the first series with data.
        var axisSeries =
            AxisSeries is { } chosen && series.Contains(chosen) && HasDataInWindow(chosen, t0)
                ? chosen
                : series.FirstOrDefault(s => HasDataInWindow(s, t0));
        if (axisSeries is not null && EffectiveRange(axisSeries, t0, t1) is { } range)
        {
            DrawGridAndLabels(context, plot, typeface, t0, t1, range.Min, range.Max, range.Tick);
        }

        bool drewAnything = false;
        foreach (var s in series)
        {
            if (EffectiveRange(s, t0, t1) is not { } r)
            {
                continue; // autoscale with no data in the window: nothing to fit, nothing to draw
            }
            int count = CopyWindow(s, t0);
            if (count > 0)
            {
                DrawSeries(context, plot, s.Stroke, t0, t1, r.Min, r.Max, count);
                drewAnything = true;
            }
        }

        if (!drewAnything)
        {
            DrawEmptyState(
                context,
                plot,
                typeface,
                "no data yet — samples appear once a session streams"
            );
        }
    }

    /// <summary>Whether this series has any sample inside the window. O(1): the buffer's times are
    /// non-decreasing, so the newest one landing at or after <paramref name="t0"/> is the whole
    /// test.</summary>
    private static bool HasDataInWindow(IPlotSeries s, double t0) =>
        s.Buffer.Count > 0 && s.Buffer.LatestTime >= t0;

    /// <summary>The y-range this series is drawn against: its fixed Settings range when autoscale
    /// is off (exact — the user asked to look at precisely that window), else a tick-widened fit
    /// of its visible data. Null when the series has nothing in the window.</summary>
    private (double Min, double Max, double Tick)? EffectiveRange(
        IPlotSeries s,
        double t0,
        double t1
    )
    {
        // Checked before the configured range, not after: a channel with no samples is absent,
        // not flat, and must contribute nothing at all — no line and no axis. A fixed range that
        // outlived its data would otherwise draw a fully labeled axis for a channel that never
        // streamed, which reads as data sitting at those values.
        if (!HasDataInWindow(s, t0))
        {
            return null;
        }

        if (!s.AutoScale && s.AxisMax > s.AxisMin)
        {
            return (s.AxisMin, s.AxisMax, NiceStep((s.AxisMax - s.AxisMin) / 4));
        }

        int count = CopyWindow(s, t0);
        if (count == 0)
        {
            return null;
        }

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        for (int i = 0; i < count; i++)
        {
            min = Math.Min(min, _values![i]);
            max = Math.Max(max, _values[i]);
        }
        // A flat line still needs a range to sit in; ±1 (or 10%) keeps it mid-strip.
        if (max - min < 1e-9)
        {
            double flatPad = Math.Max(1, Math.Abs(max) * 0.1);
            min -= flatPad;
            max += flatPad;
        }
        else
        {
            double pad = (max - min) * 0.08;
            min -= pad;
            max += pad;
        }

        double tick = NiceStep((max - min) / 4);
        // Widen to tick multiples so the top and bottom gridlines land on labeled values.
        return (Math.Floor(min / tick) * tick, Math.Ceiling(max / tick) * tick, tick);
    }

    private int CopyWindow(IPlotSeries s, double t0)
    {
        EnsureScratch(s.Buffer.Capacity, (int)Math.Max(1, Bounds.Width));
        return s.Buffer.CopyWindow(t0, _times!, _values!);
    }

    private void EnsureScratch(int bufferCapacity, int plotWidth)
    {
        if (_times is null || _times.Length < bufferCapacity)
        {
            _times = new double[bufferCapacity];
            _values = new float[bufferCapacity];
        }
        int needed = 2 * Math.Max(1, plotWidth);
        if (_outTimes is null || _outTimes.Length < needed)
        {
            _outTimes = new double[needed];
            _outValues = new float[needed];
        }
    }

    /// <summary>The nearest 1/2/5×10ⁿ at or above <paramref name="raw"/> — axis steps people can
    /// read multiples of at a glance.</summary>
    private static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw))
        {
            return 1;
        }
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double normalized = raw / magnitude;
        return (
                normalized <= 1 ? 1
                : normalized <= 2 ? 2
                : normalized <= 5 ? 5
                : 10
            ) * magnitude;
    }

    private static string FormatValue(double value) =>
        Math.Abs(value) switch
        {
            >= 10000 => value.ToString("0.#e0", CultureInfo.InvariantCulture),
            >= 100 => value.ToString("0", CultureInfo.InvariantCulture),
            >= 1 => value.ToString("0.##", CultureInfo.InvariantCulture),
            >= 0.001 => value.ToString("0.###", CultureInfo.InvariantCulture),
            0 => "0",
            _ => value.ToString("0.#e0", CultureInfo.InvariantCulture),
        };

    private void DrawGridAndLabels(
        DrawingContext context,
        Rect plot,
        Typeface typeface,
        double t0,
        double t1,
        double yMin,
        double yMax,
        double yTick
    )
    {
        var gridPen = new Pen(GridBrush, 1);

        // Horizontal gridlines + y labels at each tick multiple inside the range (a manual range's
        // bounds need not be multiples themselves). Values are the axis series' alone.
        double firstTick = Math.Ceiling(yMin / yTick - 1e-9) * yTick;
        for (double y = firstTick; y <= yMax + yTick * 0.01; y += yTick)
        {
            double py = plot.Bottom - (y - yMin) / (yMax - yMin) * plot.Height;
            context.DrawLine(gridPen, new Point(plot.Left, py), new Point(plot.Right, py));
            var label = Text(FormatValue(y), typeface);
            context.DrawText(label, new Point(plot.Left - 6 - label.Width, py - label.Height / 2));
        }

        // Vertical gridlines + elapsed-seconds labels, starting at 0 on the left. The step is
        // chosen per frame rather than fixed: the same axis has to stay readable at 5 seconds and
        // at an hour, so it walks the 1/2/5 sequence as the run grows.
        double xTick = NiceStep((t1 - t0) / 6);
        for (double t = 0; t <= t1 + xTick * 0.01; t += xTick)
        {
            double px = plot.Left + (t - t0) / (t1 - t0) * plot.Width;
            context.DrawLine(gridPen, new Point(px, plot.Top), new Point(px, plot.Bottom));
            var label = Text(FormatSeconds(t, xTick), typeface);
            double lx = Math.Max(
                plot.Left,
                Math.Min(px - label.Width / 2, plot.Right - label.Width)
            );
            context.DrawText(label, new Point(lx, plot.Bottom + 3));
        }
    }

    /// <summary>Elapsed time in seconds, per the request that every plot read in seconds. The
    /// decimals follow the tick step, so a 0.5 s grid does not label every line "0".</summary>
    private static string FormatSeconds(double seconds, double tick) =>
        tick >= 1
            ? seconds.ToString("0", CultureInfo.InvariantCulture) + "s"
            : seconds.ToString(tick >= 0.1 ? "0.#" : "0.##", CultureInfo.InvariantCulture) + "s";

    /// <summary>Draws the series currently in the scratch arrays against its own range — the
    /// normalization that lets differently-scaled series share the strip.</summary>
    private void DrawSeries(
        DrawingContext context,
        Rect plot,
        IBrush? stroke,
        double t0,
        double t1,
        double yMin,
        double yMax,
        int count
    )
    {
        int points = Envelope.Decimate(
            _times!,
            _values!,
            count,
            t0,
            t1,
            (int)plot.Width,
            _outTimes!,
            _outValues!
        );
        if (points == 0)
        {
            return;
        }

        // A silence longer than the gap threshold starts a new figure rather than being bridged.
        // The threshold also has to clear a few decimation buckets: once a run is long enough that
        // one pixel column spans several seconds, adjacent output points are legitimately that far
        // apart and must not read as holes.
        double bucketSeconds = (t1 - t0) / Math.Max(1, plot.Width);
        double gapThreshold = Math.Max(GapSeconds, bucketSeconds * 3);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(ToPixel(0), isFilled: false);
            for (int i = 1; i < points; i++)
            {
                if (_outTimes![i] - _outTimes[i - 1] > gapThreshold)
                {
                    g.EndFigure(isClosed: false);
                    g.BeginFigure(ToPixel(i), isFilled: false);
                    continue;
                }
                g.LineTo(ToPixel(i));
            }
            g.EndFigure(isClosed: false);
        }

        var pen = new Pen(stroke, 2) { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round };
        using (context.PushClip(plot))
        {
            context.DrawGeometry(null, pen, geometry);
        }
        return;

        Point ToPixel(int i) =>
            new(
                plot.Left + (_outTimes![i] - t0) / (t1 - t0) * plot.Width,
                plot.Bottom - (_outValues![i] - yMin) / (yMax - yMin) * plot.Height
            );
    }

    private void DrawEmptyState(
        DrawingContext context,
        Rect plot,
        Typeface typeface,
        string message
    )
    {
        context.DrawRectangle(new Pen(GridBrush, 1), plot);
        var label = Text(message, typeface);
        context.DrawText(
            label,
            new Point(
                plot.Left + (plot.Width - label.Width) / 2,
                plot.Top + (plot.Height - label.Height) / 2
            )
        );
    }

    private FormattedText Text(string text, Typeface typeface) =>
        new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            LabelFontSize,
            LabelBrush
        );
}
