using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Dyno.Core.Plotting;

namespace Dyno.App.Controls;

/// <summary>
/// A single scrolling strip chart: one series drawn over the last <see cref="WindowSeconds"/>
/// seconds ending at <see cref="AnchorTime"/>. The y-axis auto-scales to the visible data — each
/// strip is a small multiple with its own units, so sharing a scale with siblings would be
/// meaningless. Dense data is min/max-decimated per pixel column, so spikes survive at any rate.
/// </summary>
public class TimeSeriesPlot : Control
{
    public static readonly StyledProperty<TimeSeriesBuffer?> BufferProperty =
        AvaloniaProperty.Register<TimeSeriesPlot, TimeSeriesBuffer?>(nameof(Buffer));

    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<
        TimeSeriesPlot,
        IBrush?
    >(nameof(Stroke), Brushes.White);

    public static readonly StyledProperty<IBrush?> GridBrushProperty = AvaloniaProperty.Register<
        TimeSeriesPlot,
        IBrush?
    >(nameof(GridBrush), new SolidColorBrush(Color.Parse("#2A2F3A")));

    public static readonly StyledProperty<IBrush?> LabelBrushProperty = AvaloniaProperty.Register<
        TimeSeriesPlot,
        IBrush?
    >(nameof(LabelBrush), new SolidColorBrush(Color.Parse("#8892A2")));

    /// <summary>Right edge of the window (seconds, same clock as the buffer's samples). Advanced
    /// by the page's frame timer; every change repaints the strip.</summary>
    public static readonly StyledProperty<double> AnchorTimeProperty = AvaloniaProperty.Register<
        TimeSeriesPlot,
        double
    >(nameof(AnchorTime));

    public static readonly StyledProperty<double> WindowSecondsProperty = AvaloniaProperty.Register<
        TimeSeriesPlot,
        double
    >(nameof(WindowSeconds), 30.0);

    static TimeSeriesPlot()
    {
        AffectsRender<TimeSeriesPlot>(
            BufferProperty,
            StrokeProperty,
            GridBrushProperty,
            LabelBrushProperty,
            AnchorTimeProperty,
            WindowSecondsProperty
        );
    }

    public TimeSeriesBuffer? Buffer
    {
        get => GetValue(BufferProperty);
        set => SetValue(BufferProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
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

    public double WindowSeconds
    {
        get => GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
    }

    // Margins around the plot area: room for y labels left, x labels below.
    private const double MarginLeft = 48;
    private const double MarginRight = 8;
    private const double MarginTop = 6;
    private const double MarginBottom = 18;
    private const double LabelFontSize = 10;
    private const double XTickStep = 5; // seconds between vertical gridlines

    // Scratch arrays reused across frames (sized to the buffer on first use).
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
        var buffer = Buffer;

        double t1 = AnchorTime;
        double t0 = t1 - WindowSeconds;

        int count = 0;
        if (buffer is not null && buffer.Count > 0)
        {
            EnsureScratch(buffer.Capacity, (int)plot.Width);
            count = buffer.CopyWindow(t0, _times!, _values!);
        }

        if (count == 0)
        {
            DrawEmptyState(context, plot, typeface);
            return;
        }

        (double yMin, double yMax) = VisibleRange(count);
        double yTick = NiceStep((yMax - yMin) / 4);
        // Widen to tick multiples so the top and bottom gridlines land on labeled values.
        yMin = Math.Floor(yMin / yTick) * yTick;
        yMax = Math.Ceiling(yMax / yTick) * yTick;

        DrawGridAndLabels(context, plot, typeface, t0, t1, yMin, yMax, yTick);
        DrawSeries(context, plot, t0, t1, yMin, yMax, count);
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

    private (double Min, double Max) VisibleRange(int count)
    {
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
            double pad = Math.Max(1, Math.Abs(max) * 0.1);
            min -= pad;
            max += pad;
        }
        else
        {
            double pad = (max - min) * 0.08;
            min -= pad;
            max += pad;
        }
        return (min, max);
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

        // Horizontal gridlines + y labels at each tick.
        for (double y = yMin; y <= yMax + yTick * 0.01; y += yTick)
        {
            double py = plot.Bottom - (y - yMin) / (yMax - yMin) * plot.Height;
            context.DrawLine(gridPen, new Point(plot.Left, py), new Point(plot.Right, py));
            var label = Text(FormatValue(y), typeface);
            context.DrawText(label, new Point(plot.Left - 6 - label.Width, py - label.Height / 2));
        }

        // Vertical gridlines + relative-seconds labels, anchored so "0" is the newest edge.
        for (double t = 0; t >= -(t1 - t0); t -= XTickStep)
        {
            double px = plot.Right + t / (t1 - t0) * plot.Width;
            context.DrawLine(gridPen, new Point(px, plot.Top), new Point(px, plot.Bottom));
            var label = Text(
                t == 0 ? "now" : $"{t.ToString("0", CultureInfo.InvariantCulture)}s",
                typeface
            );
            double lx = Math.Max(
                plot.Left,
                Math.Min(px - label.Width / 2, plot.Right - label.Width)
            );
            context.DrawText(label, new Point(lx, plot.Bottom + 3));
        }
    }

    private void DrawSeries(
        DrawingContext context,
        Rect plot,
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

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(ToPixel(0), isFilled: false);
            for (int i = 1; i < points; i++)
            {
                g.LineTo(ToPixel(i));
            }
            g.EndFigure(isClosed: false);
        }

        var pen = new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round };
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

    private void DrawEmptyState(DrawingContext context, Rect plot, Typeface typeface)
    {
        context.DrawRectangle(new Pen(GridBrush, 1), plot);
        var label = Text("no data yet — samples appear once a session streams", typeface);
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
