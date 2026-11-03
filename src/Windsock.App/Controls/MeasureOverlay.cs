using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using Windsock.Core.Formatting;
using Windsock.Core.History;

namespace Windsock.App.Controls;

/// <summary>
/// Lies over History's usage chart while measuring: a drag across it marks a
/// stretch, whose start and end are <see cref="From"/> and <see cref="Until"/>.
/// </summary>
/// <remarks>
/// <para>
/// Off, it is not there at all - collapsed, so presses go to the chart beneath,
/// which keeps its panning and its clicks, and neither Tab nor a screen reader
/// meets it. It keeps to the chart's zoom through <see cref="Follow"/> and uses
/// the chart's gutters.
/// </para>
/// <para>
/// Marking with the mouse is one way in. The page has the same stretch in two
/// date boxes, and changing either redraws the mark here; and while measuring
/// the overlay itself takes the keyboard: the arrows move the stretch a bar
/// (<see cref="Granularity"/>) at a time and Shift with them moves its end. A
/// screen reader hears it as a chart named for the stretch, which it says.
/// </para>
/// </remarks>
public sealed class MeasureOverlay : FrameworkElement
{
    private const double LeftGutter = 58;
    private const double RightPadding = 8;
    private const double BottomGutter = 20;

    private double _offset;
    private double _span = 1;
    private DateTimeOffset? _anchor;

    public static readonly DependencyProperty RangeStartProperty = Register(nameof(RangeStart), default(DateTimeOffset));

    public static readonly DependencyProperty RangeEndProperty = Register(nameof(RangeEnd), default(DateTimeOffset));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(MeasureOverlay), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnActiveChanged));

    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(DateTime), typeof(MeasureOverlay),
        new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty UntilProperty = DependencyProperty.Register(
        nameof(Until), typeof(DateTime), typeof(MeasureOverlay),
        new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SelectionBrushProperty = Register<Brush?>(nameof(SelectionBrush), null);

    public static readonly DependencyProperty GranularityProperty = Register(nameof(Granularity), UsageGranularity.Hour);

    public MeasureOverlay()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        Focusable = true;
    }

    public DateTimeOffset RangeStart
    {
        get => (DateTimeOffset)GetValue(RangeStartProperty);
        set => SetValue(RangeStartProperty, value);
    }

    public DateTimeOffset RangeEnd
    {
        get => (DateTimeOffset)GetValue(RangeEndProperty);
        set => SetValue(RangeEndProperty, value);
    }

    /// <summary>Whether a drag marks a stretch rather than panning the chart.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The marked stretch's first minute, local time.</summary>
    public DateTime From
    {
        get => (DateTime)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    /// <summary>The marked stretch's last minute, local time, counted in full.</summary>
    public DateTime Until
    {
        get => (DateTime)GetValue(UntilProperty);
        set => SetValue(UntilProperty, value);
    }

    public Brush? SelectionBrush
    {
        get => (Brush?)GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    /// <summary>How wide one bar of the chart is: how far an arrow key moves the stretch.</summary>
    public UsageGranularity Granularity
    {
        get => (UsageGranularity)GetValue(GranularityProperty);
        set => SetValue(GranularityProperty, value);
    }

    /// <summary>Takes the usage chart's view.</summary>
    public void Follow((double Offset, double Span) view)
    {
        _offset = Math.Clamp(view.Offset, 0, 1);
        _span = Math.Clamp(view.Span, 0.000001, 1);
        InvalidateVisual();
    }

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(MeasureOverlay), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    private static void OnActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MeasureOverlay overlay)
        {
            overlay.IsHitTestVisible = e.NewValue is true;
            overlay.Cursor = e.NewValue is true ? Cursors.IBeam : null;
            overlay.Visibility = e.NewValue is true ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>A screen reader hears a chart named for the stretch, and the stretch it marks.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this, Summary);

    private string Summary() =>
        From == default || Until == default
            ? "Nothing marked yet. Drag across the chart, or use the arrow keys: Shift with them moves the end."
            : string.Concat("From ", ClockFormat.Day(From), " to ", ClockFormat.Day(Until), ", counted in full. The arrow keys move it; Shift with them moves its end.");

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        ArgumentNullException.ThrowIfNull(e);

        if (!IsActive || e.Handled || e.Key is not (Key.Left or Key.Right) || RangeEnd <= RangeStart)
        {
            return;
        }

        TimeSpan step = Granularity switch
        {
            UsageGranularity.Minute => TimeSpan.FromMinutes(1),
            UsageGranularity.Hour => TimeSpan.FromHours(1),
            _ => TimeSpan.FromDays(1),
        };

        int direction = e.Key == Key.Right ? 1 : -1;
        DateTime first = Minute(RangeStart.LocalDateTime);
        DateTime last = Minute(RangeEnd.LocalDateTime.AddMinutes(-1));

        // Nothing marked yet: the arrows start from the newest bar.
        if (From == default || Until == default)
        {
            From = Clamp(last - step + TimeSpan.FromMinutes(1), first, last);
            Until = last;
        }
        else if ((e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0)
        {
            Until = Clamp(Until + (step * direction), From, last);
        }
        else
        {
            TimeSpan length = Until - From;
            DateTime from = Clamp(From + (step * direction), first, last - length);
            From = from;
            Until = from + length;
        }

        e.Handled = true;
    }

    private static DateTime Minute(DateTime local) =>
        new(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);

    private static DateTime Clamp(DateTime value, DateTime low, DateTime high) =>
        high < low ? low : value < low ? low : value > high ? high : value;

    private double PlotWidth => Math.Max(1, ActualWidth - LeftGutter - RightPadding);

    private DateTime TimeAt(double x)
    {
        double total = (RangeEnd - RangeStart).TotalSeconds;
        double fraction = Math.Clamp((x - LeftGutter) / PlotWidth, 0, 1);
        DateTimeOffset at = RangeStart + TimeSpan.FromSeconds((_offset + (fraction * _span)) * total);
        DateTime local = at.LocalDateTime;
        return new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);
    }

    private double XOf(DateTime local)
    {
        double total = (RangeEnd - RangeStart).TotalSeconds;
        DateTimeOffset at = new(local, TimeZoneInfo.Local.GetUtcOffset(local));
        double fraction = total <= 0 ? 0 : (((at - RangeStart).TotalSeconds / total) - _offset) / _span;
        return LeftGutter + (fraction * PlotWidth);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        ArgumentNullException.ThrowIfNull(e);

        if (!IsActive)
        {
            return;
        }

        DateTime at = TimeAt(e.GetPosition(this).X);
        _anchor = new DateTimeOffset(at, TimeZoneInfo.Local.GetUtcOffset(at));
        From = at;
        Until = at;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        ArgumentNullException.ThrowIfNull(e);

        if (_anchor is not { } anchor || !IsMouseCaptured)
        {
            return;
        }

        DateTime at = TimeAt(e.GetPosition(this).X);
        DateTime start = anchor.LocalDateTime;

        if (at < start)
        {
            From = at;
            Until = start;
        }
        else
        {
            From = start;
            Until = at;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _anchor = null;

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        if (!IsActive)
        {
            return;
        }

        // The whole area answers presses while measuring.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (SelectionBrush is not { } brush || From == default || RangeEnd <= RangeStart)
        {
            return;
        }

        double left = Math.Max(LeftGutter, XOf(From));
        double right = Math.Min(LeftGutter + PlotWidth, XOf(Until.AddMinutes(1)));

        if (right <= left)
        {
            right = left + 1;
        }

        double height = Math.Max(0, ActualHeight - BottomGutter);
        drawingContext.PushOpacity(0.22);
        drawingContext.DrawRectangle(brush, null, new Rect(left, 0, right - left, height));
        drawingContext.Pop();
        drawingContext.DrawRectangle(brush, null, new Rect(left, 0, 1.5, height));
        drawingContext.DrawRectangle(brush, null, new Rect(right - 1.5, 0, 1.5, height));
    }
}
