using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Windsock.Core.Wifi;

namespace Windsock.App.Controls;

/// <summary>
/// The Wi-Fi signal over time as a thin line, on the same time axis as the
/// chart above it: a strip under the throughput chart, so a slow moment can be
/// seen to line up with a weak signal, or a chart of its own on the Adapters
/// and Wi-Fi page.
/// </summary>
/// <remarks>
/// The vertical scale is fixed, -90 dBm at the foot to -30 dBm at the top, so
/// the line's height means the same thing every time it is seen; faint lines
/// mark -67 dBm (good enough for calls) and -80 dBm (weak). Readings without a
/// signal - Wi-Fi not connected, or not read - break the line rather than
/// drawing it at the floor. It paints no ground of its own.
/// </remarks>
public sealed class SignalStrip : FrameworkElement
{
    private const double Strongest = -30;
    private const double Weakest = -90;
    private const double LabelSize = 10;

    public static readonly DependencyProperty PointsProperty = Register<IReadOnlyList<SignalPoint>?>(nameof(Points), null);

    public static readonly DependencyProperty RevisionProperty = Register(nameof(Revision), 0);

    public static readonly DependencyProperty VisibleSampleCountProperty = Register(nameof(VisibleSampleCount), 120d);

    public static readonly DependencyProperty SampleIntervalSecondsProperty = Register(nameof(SampleIntervalSeconds), 0.5);

    public static readonly DependencyProperty LeftInsetProperty = Register(nameof(LeftInset), 54d);

    public static readonly DependencyProperty LineBrushProperty = Register<Brush?>(nameof(LineBrush), null);

    public static readonly DependencyProperty GridBrushProperty = Register<Brush?>(nameof(GridBrush), null);

    public static readonly DependencyProperty AxisBrushProperty = Register<Brush?>(nameof(AxisBrush), null);

    public static readonly DependencyProperty LabelProperty = Register(nameof(Label), string.Empty);

    public static readonly DependencyProperty ShowScaleProperty = Register(nameof(ShowScale), false);

    public static readonly DependencyProperty EndAtProperty = Register(nameof(EndAt), DateTimeOffset.MinValue);

    private readonly Typeface _typeface = new(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this, Summary);

    /// <summary>The latest signal and the weakest in view, in words.</summary>
    private string Summary()
    {
        int[] readings = [.. (Points ?? []).Where(each => each.Rssi is not null).Select(each => each.Rssi.GetValueOrDefault())];

        if (readings.Length == 0)
        {
            return "Nothing is drawn yet.";
        }

        int latest = readings[^1];
        int weakest = readings.Min();
        int strongest = readings.Max();

        return string.Create(CultureInfo.CurrentCulture, $"Signal now {latest} dBm. Weakest in view {weakest} dBm, strongest {strongest} dBm.");
    }

    /// <summary>The readings, oldest first.</summary>
    public IReadOnlyList<SignalPoint>? Points
    {
        get => (IReadOnlyList<SignalPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>Bumped whenever the readings change.</summary>
    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    /// <summary>How many of the chart above's samples are on screen: its zoom.</summary>
    public double VisibleSampleCount
    {
        get => (double)GetValue(VisibleSampleCountProperty);
        set => SetValue(VisibleSampleCountProperty, value);
    }

    /// <summary>Seconds between the chart above's samples.</summary>
    public double SampleIntervalSeconds
    {
        get => (double)GetValue(SampleIntervalSecondsProperty);
        set => SetValue(SampleIntervalSecondsProperty, value);
    }

    /// <summary>Where the plot starts: the chart above's value axis width, so the two line up.</summary>
    public double LeftInset
    {
        get => (double)GetValue(LeftInsetProperty);
        set => SetValue(LeftInsetProperty, value);
    }

    public Brush? LineBrush
    {
        get => (Brush?)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush? GridBrush
    {
        get => (Brush?)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush? AxisBrush
    {
        get => (Brush?)GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    /// <summary>What the strip says in its left inset, such as "Wi-Fi -58 dBm".</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Whether to write the dBm marks beside their lines; for a chart tall enough to read them.</summary>
    public bool ShowScale
    {
        get => (bool)GetValue(ShowScaleProperty);
        set => SetValue(ShowScaleProperty, value);
    }

    /// <summary>
    /// The moment at the right-hand edge, or <see cref="DateTimeOffset.MinValue"/>
    /// for now. Under a chart, the chart's newest sample, so a paused chart holds
    /// the strip still with it.
    /// </summary>
    public DateTimeOffset EndAt
    {
        get => (DateTimeOffset)GetValue(EndAtProperty);
        set => SetValue(EndAtProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        double left = Math.Min(LeftInset, width);
        double plotWidth = width - left;

        if (plotWidth <= 4 || height <= 4)
        {
            return;
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (GridBrush is { } grid)
        {
            Pen pen = new(grid, 1);
            pen.Freeze();

            foreach (double mark in new[] { -67d, -80d })
            {
                double y = Math.Round(Y(mark, height)) + 0.5;
                drawingContext.DrawLine(pen, new Point(left, y), new Point(width, y));

                if (ShowScale && AxisBrush is { } axis)
                {
                    FormattedText text = Text(mark.ToString("0", CultureInfo.InvariantCulture), axis, dpi);
                    drawingContext.DrawText(text, new Point(left - text.Width - 8, y - (text.Height / 2)));
                }
            }
        }

        if (!ShowScale && Label.Length > 0 && AxisBrush is { } labelBrush && left > 20)
        {
            FormattedText text = Text(Label, labelBrush, dpi);
            text.MaxTextWidth = left - 6;
            text.Trimming = TextTrimming.CharacterEllipsis;
            text.MaxLineCount = 2;
            drawingContext.DrawText(text, new Point(0, Math.Max(0, (height - text.Height) / 2)));
        }

        if (Points is not { Count: > 0 } points || LineBrush is not { } line)
        {
            return;
        }

        double span = Math.Max(1, VisibleSampleCount * SampleIntervalSeconds);
        DateTimeOffset end = EndAt == DateTimeOffset.MinValue ? DateTimeOffset.Now : EndAt;
        DateTimeOffset start = end - TimeSpan.FromSeconds(span);

        Pen stroke = new(line, 1.5) { LineJoin = PenLineJoin.Round };
        stroke.Freeze();

        StreamGeometry geometry = new();

        using (StreamGeometryContext context = geometry.Open())
        {
            bool open = false;

            foreach (SignalPoint point in points)
            {
                if (point.At < start || point.At > end || point.Rssi is not { } rssi)
                {
                    open = false;
                    continue;
                }

                double x = left + ((point.At - start).TotalSeconds / span * plotWidth);
                Point at = new(Math.Min(x, width), Y(rssi, height));

                if (!open)
                {
                    context.BeginFigure(at, isFilled: false, isClosed: false);
                    open = true;
                }
                else
                {
                    context.LineTo(at, isStroked: true, isSmoothJoin: true);
                }
            }
        }

        geometry.Freeze();
        drawingContext.PushClip(new RectangleGeometry(new Rect(left, 0, plotWidth, height)));
        drawingContext.DrawGeometry(null, stroke, geometry);
        drawingContext.Pop();
    }

    private static double Y(double rssi, double height)
    {
        double share = (Math.Clamp(rssi, Weakest, Strongest) - Weakest) / (Strongest - Weakest);
        return 2 + ((1 - share) * (height - 4));
    }

    private FormattedText Text(string text, Brush brush, double dpi) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, LabelSize, brush, dpi);

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(SignalStrip), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));
}
