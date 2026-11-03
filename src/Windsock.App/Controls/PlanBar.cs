using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>
/// How much of a plan's allowance is used: a filled track, a tick at each of
/// the plan's alert levels, a thin mark where the forecast says the cycle will
/// end, and anything past the allowance in the warning colour.
/// </summary>
/// <remarks>
/// A picture of figures that are written out beside it, so it takes no focus;
/// its automation name says what it shows.
/// </remarks>
public sealed class PlanBar : FrameworkElement
{
    private const double TrackHeight = 10;
    private const double TickHeight = 16;

    public static readonly DependencyProperty FractionProperty = Register(nameof(Fraction), 0.0);

    public static readonly DependencyProperty ProjectedFractionProperty = Register<double?>(nameof(ProjectedFraction), null);

    public static readonly DependencyProperty LevelsProperty = Register<IReadOnlyList<int>>(nameof(Levels), []);

    public static readonly DependencyProperty TrackBrushProperty = Register<Brush?>(nameof(TrackBrush), null);

    public static readonly DependencyProperty FillBrushProperty = Register<Brush?>(nameof(FillBrush), null);

    public static readonly DependencyProperty OverBrushProperty = Register<Brush?>(nameof(OverBrush), null);

    public static readonly DependencyProperty TickBrushProperty = Register<Brush?>(nameof(TickBrush), null);

    public static readonly DependencyProperty ProjectedBrushProperty = Register<Brush?>(nameof(ProjectedBrush), null);

    /// <summary>Used over allowance; may pass 1.</summary>
    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    /// <summary>Where the forecast puts the end of the cycle, as a share of the allowance; null while too early.</summary>
    public double? ProjectedFraction
    {
        get => (double?)GetValue(ProjectedFractionProperty);
        set => SetValue(ProjectedFractionProperty, value);
    }

    /// <summary>The plan's alert levels, in percent.</summary>
    public IReadOnlyList<int> Levels
    {
        get => (IReadOnlyList<int>)GetValue(LevelsProperty);
        set => SetValue(LevelsProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush? OverBrush
    {
        get => (Brush?)GetValue(OverBrushProperty);
        set => SetValue(OverBrushProperty, value);
    }

    public Brush? TickBrush
    {
        get => (Brush?)GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    public Brush? ProjectedBrush
    {
        get => (Brush?)GetValue(ProjectedBrushProperty);
        set => SetValue(ProjectedBrushProperty, value);
    }

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(PlanBar), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this, Summary);

    /// <summary>What a screen reader is told: how much is used, and where this pace ends.</summary>
    private string Summary()
    {
        string used = string.Format(CultureInfo.CurrentCulture, "{0:P0} of the allowance used.", Fraction);
        return ProjectedFraction is { } projected
            ? string.Format(CultureInfo.CurrentCulture, "{0} At this pace, {1:P0} by the end of the cycle.", used, projected)
            : used;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, TickHeight + 2);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        double width = ActualWidth;

        if (width <= 4)
        {
            return;
        }

        // Past the allowance the whole track is the allowance plus what is
        // over, so the overshoot shows in proportion rather than off the end.
        double fraction = Math.Max(0, Fraction);
        double scale = Math.Max(1, Math.Max(fraction, ProjectedFraction ?? 0));
        double top = (TickHeight - TrackHeight) / 2 + 1;
        double radius = TrackHeight / 2;
        double allowanceEnd = width / scale;

        if (TrackBrush is { } track)
        {
            drawingContext.DrawRoundedRectangle(track, null, new Rect(0, top, width, TrackHeight), radius, radius);
        }

        double used = width * Math.Min(fraction, 1) / scale;

        if (FillBrush is { } fill && used > 0)
        {
            drawingContext.DrawRoundedRectangle(fill, null, new Rect(0, top, Math.Max(TrackHeight, used), TrackHeight), radius, radius);
        }

        if (fraction > 1 && OverBrush is { } over)
        {
            double overEnd = width * fraction / scale;
            drawingContext.DrawRoundedRectangle(over, null, new Rect(allowanceEnd - radius, top, Math.Max(TrackHeight, overEnd - allowanceEnd + radius), TrackHeight), radius, radius);
        }

        if (TickBrush is { } tick)
        {
            Pen pen = new(tick, 1.5);
            pen.Freeze();

            foreach (int level in Levels)
            {
                double x = Math.Round(Math.Clamp(width * level / 100.0 / scale, 1, width - 1)) + 0.5;
                drawingContext.DrawLine(pen, new Point(x, 0), new Point(x, TickHeight + 1));
            }
        }

        if (ProjectedFraction is { } projected && projected > 0 && ProjectedBrush is { } mark)
        {
            double x = Math.Clamp(width * projected / scale, 2, width - 2);
            drawingContext.DrawEllipse(mark, null, new Point(x, top + radius), 3.5, 3.5);
        }
    }
}
