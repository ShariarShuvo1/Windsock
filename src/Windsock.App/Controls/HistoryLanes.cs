using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>A stretch or a moment drawn in one of <see cref="HistoryLanes"/>' strips.</summary>
/// <param name="From">Where it starts.</param>
/// <param name="Until">Where it ends; the same as <paramref name="From"/> for a moment.</param>
/// <param name="Hue">
/// Which colour it takes: for a network, one of the five network hues (-1 for a
/// network not known); for an event, 0 worth knowing, 1 worth a look, 2 wrong.
/// </param>
/// <param name="Label">What it is: a network's name, an event's title and detail, a note's text.</param>
/// <param name="Id">The note's row, for a note; 0 otherwise.</param>
public sealed record LaneSpan(DateTimeOffset From, DateTimeOffset Until, int Hue, string Label, long Id = 0)
{
    /// <summary>Whether it is a moment rather than a stretch.</summary>
    public bool IsMoment => Until <= From;
}

/// <summary>
/// Three strips under History's usage chart, on its time axis: which network
/// each stretch was measured on, what happened to the connection and what was
/// alerted (outages as stretches, everything else as ticks), and the notes
/// written on the period.
/// </summary>
/// <remarks>
/// Keeps to the usage chart's zoom and pan through <see cref="Follow"/>, as the
/// machine's lanes do, and uses the same gutters, so a moment is in the same
/// place in every chart. Pointing at anything shows what it is. A picture of
/// what the key, the notes list and the Alerts page say in words, so it takes no
/// focus; clicking a note asks for it to be edited.
/// </remarks>
public sealed class HistoryLanes : FrameworkElement
{
    private const double LeftGutter = 58;
    private const double RightPadding = 8;
    private const double NetworkHeight = 8;
    private const double EventHeight = 14;
    private const double NoteHeight = 20;
    private const double RowGap = 6;
    private const double LabelSize = 10.5;
    private const double TickWidth = 3;

    private double _offset;
    private double _span = 1;

    public static readonly DependencyProperty RangeStartProperty = Register(nameof(RangeStart), default(DateTimeOffset));

    public static readonly DependencyProperty RangeEndProperty = Register(nameof(RangeEnd), default(DateTimeOffset));

    public static readonly DependencyProperty NetworksProperty = Register<IReadOnlyList<LaneSpan>>(nameof(Networks), []);

    public static readonly DependencyProperty EventsProperty = Register<IReadOnlyList<LaneSpan>>(nameof(Events), []);

    public static readonly DependencyProperty NotesProperty = Register<IReadOnlyList<LaneSpan>>(nameof(Notes), []);

    public static readonly DependencyProperty Hue0BrushProperty = Register<Brush?>(nameof(Hue0Brush), null);

    public static readonly DependencyProperty Hue1BrushProperty = Register<Brush?>(nameof(Hue1Brush), null);

    public static readonly DependencyProperty Hue2BrushProperty = Register<Brush?>(nameof(Hue2Brush), null);

    public static readonly DependencyProperty Hue3BrushProperty = Register<Brush?>(nameof(Hue3Brush), null);

    public static readonly DependencyProperty Hue4BrushProperty = Register<Brush?>(nameof(Hue4Brush), null);

    public static readonly DependencyProperty InfoBrushProperty = Register<Brush?>(nameof(InfoBrush), null);

    public static readonly DependencyProperty WarningBrushProperty = Register<Brush?>(nameof(WarningBrush), null);

    public static readonly DependencyProperty ProblemBrushProperty = Register<Brush?>(nameof(ProblemBrush), null);

    public static readonly DependencyProperty NoteBrushProperty = Register<Brush?>(nameof(NoteBrush), null);

    public static readonly DependencyProperty NoteTextBrushProperty = Register<Brush?>(nameof(NoteTextBrush), null);

    public static readonly DependencyProperty AxisBrushProperty = Register<Brush?>(nameof(AxisBrush), null);

    public static readonly DependencyProperty EmptyBrushProperty = Register<Brush?>(nameof(EmptyBrush), null);

    /// <summary>Raised when a note is clicked, with its row.</summary>
    public event EventHandler<long>? NoteClicked;

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

    /// <summary>Each stretch's network.</summary>
    public IReadOnlyList<LaneSpan> Networks
    {
        get => (IReadOnlyList<LaneSpan>)GetValue(NetworksProperty);
        set => SetValue(NetworksProperty, value);
    }

    /// <summary>Outages, network and VPN changes, and alerts.</summary>
    public IReadOnlyList<LaneSpan> Events
    {
        get => (IReadOnlyList<LaneSpan>)GetValue(EventsProperty);
        set => SetValue(EventsProperty, value);
    }

    /// <summary>The notes on the period.</summary>
    public IReadOnlyList<LaneSpan> Notes
    {
        get => (IReadOnlyList<LaneSpan>)GetValue(NotesProperty);
        set => SetValue(NotesProperty, value);
    }

    public Brush? Hue0Brush
    {
        get => (Brush?)GetValue(Hue0BrushProperty);
        set => SetValue(Hue0BrushProperty, value);
    }

    public Brush? Hue1Brush
    {
        get => (Brush?)GetValue(Hue1BrushProperty);
        set => SetValue(Hue1BrushProperty, value);
    }

    public Brush? Hue2Brush
    {
        get => (Brush?)GetValue(Hue2BrushProperty);
        set => SetValue(Hue2BrushProperty, value);
    }

    public Brush? Hue3Brush
    {
        get => (Brush?)GetValue(Hue3BrushProperty);
        set => SetValue(Hue3BrushProperty, value);
    }

    public Brush? Hue4Brush
    {
        get => (Brush?)GetValue(Hue4BrushProperty);
        set => SetValue(Hue4BrushProperty, value);
    }

    /// <summary>An event worth knowing: a network or VPN change, an alert for information.</summary>
    public Brush? InfoBrush
    {
        get => (Brush?)GetValue(InfoBrushProperty);
        set => SetValue(InfoBrushProperty, value);
    }

    /// <summary>An event worth a look.</summary>
    public Brush? WarningBrush
    {
        get => (Brush?)GetValue(WarningBrushProperty);
        set => SetValue(WarningBrushProperty, value);
    }

    /// <summary>Something wrong: an outage, a problem alert.</summary>
    public Brush? ProblemBrush
    {
        get => (Brush?)GetValue(ProblemBrushProperty);
        set => SetValue(ProblemBrushProperty, value);
    }

    public Brush? NoteBrush
    {
        get => (Brush?)GetValue(NoteBrushProperty);
        set => SetValue(NoteBrushProperty, value);
    }

    public Brush? NoteTextBrush
    {
        get => (Brush?)GetValue(NoteTextBrushProperty);
        set => SetValue(NoteTextBrushProperty, value);
    }

    public Brush? AxisBrush
    {
        get => (Brush?)GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    /// <summary>The strips' ground where nothing is drawn.</summary>
    public Brush? EmptyBrush
    {
        get => (Brush?)GetValue(EmptyBrushProperty);
        set => SetValue(EmptyBrushProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this, Summary);

    /// <summary>What a screen reader is told: how many of each, with the networks named.</summary>
    private string Summary()
    {
        string[] networks = [.. Networks.Select(span => span.Label).Distinct(StringComparer.Ordinal)];

        return string.Format(
            CultureInfo.CurrentCulture,
            "{0}. {1} events, {2} notes; each is listed under Notes and events.",
            networks.Length == 0 ? "No network known" : "Networks: " + string.Join(", ", networks),
            Events.Count,
            Notes.Count);
    }

    /// <summary>Takes the usage chart's view: where it starts, as a share of the period, and how much shows.</summary>
    public void Follow((double Offset, double Span) view)
    {
        _offset = Math.Clamp(view.Offset, 0, 1);
        _span = Math.Clamp(view.Span, 0.000001, 1);
        InvalidateVisual();
    }

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(HistoryLanes), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    private static double EventTop => NetworkHeight + RowGap;

    private static double NoteTop => EventTop + EventHeight + RowGap;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, NoteTop + NoteHeight + 2);

    private Brush? HueOf(int hue) => hue switch
    {
        0 => Hue0Brush,
        1 => Hue1Brush,
        2 => Hue2Brush,
        3 => Hue3Brush,
        4 => Hue4Brush,
        _ => null,
    };

    private Brush? LevelOf(int level) => level switch
    {
        2 => ProblemBrush,
        1 => WarningBrush,
        _ => InfoBrush,
    };

    /// <summary>Where a moment is across the strip.</summary>
    private double XOf(DateTimeOffset at, double width)
    {
        double total = (RangeEnd - RangeStart).TotalSeconds;
        double fraction = total <= 0 ? 0 : (((at - RangeStart).TotalSeconds / total) - _offset) / _span;
        return LeftGutter + (fraction * width);
    }

    /// <summary>The left and right of a span as drawn, a moment given a tick's width to be found by.</summary>
    private (double Left, double Right) Extent(LaneSpan span, double width, double least)
    {
        double left = XOf(span.From, width);
        double right = span.IsMoment ? left : XOf(span.Until, width);
        return span.IsMoment ? (left - (least / 2), left + (least / 2)) : (left, Math.Max(right, left + least));
    }

    /// <summary>The span under a point, in whichever strip the point is in.</summary>
    private LaneSpan? Find(Point at)
    {
        double width = ActualWidth - LeftGutter - RightPadding;

        if (width <= 0 || at.X < LeftGutter)
        {
            return null;
        }

        (IReadOnlyList<LaneSpan> spans, double least) = at.Y < EventTop - (RowGap / 2)
            ? (Networks, 1.0)
            : at.Y < NoteTop - (RowGap / 2)
                ? (Events, 8.0)
                : (Notes, 6.0);

        // Moments first, so a tick inside an outage is still found.
        foreach (LaneSpan span in spans.OrderBy(each => each.IsMoment ? 0 : 1))
        {
            (double left, double right) = Extent(span, width, least);

            if (at.X >= left && at.X <= right)
            {
                return span;
            }
        }

        return null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ArgumentNullException.ThrowIfNull(e);

        Point at = e.GetPosition(this);

        if (at.Y >= NoteTop - (RowGap / 2) && Find(at) is { Id: > 0 } note)
        {
            NoteClicked?.Invoke(this, note.Id);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        ArgumentNullException.ThrowIfNull(e);

        Point at = e.GetPosition(this);
        LaneSpan? found = Find(at);
        string? tip = found?.Label;

        if (!Equals(ToolTip, tip))
        {
            ToolTip = tip;
        }

        Cursor = found is { Id: > 0 } ? Cursors.Hand : null;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        double width = ActualWidth - LeftGutter - RightPadding;

        if (width <= 4 || RangeEnd <= RangeStart)
        {
            return;
        }

        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        drawingContext.PushClip(new RectangleGeometry(new Rect(LeftGutter, 0, width, ActualHeight)));

        if (EmptyBrush is { } empty)
        {
            drawingContext.DrawRoundedRectangle(empty, null, new Rect(LeftGutter, 0, width, NetworkHeight), 2, 2);
            drawingContext.DrawRoundedRectangle(empty, null, new Rect(LeftGutter, EventTop + (EventHeight / 2) - 1, width, 2), 1, 1);
        }

        foreach (LaneSpan span in Networks)
        {
            if (HueOf(span.Hue) is not { } brush)
            {
                continue;
            }

            double left = XOf(span.From, width);
            double right = XOf(span.Until, width);

            if (right > LeftGutter && left < LeftGutter + width)
            {
                drawingContext.DrawRectangle(brush, null, new Rect(left, 0, Math.Max(1, right - left), NetworkHeight));
            }
        }

        DrawEvents(drawingContext, width);
        DrawNotes(drawingContext, width);
        drawingContext.Pop();

        if (AxisBrush is { } axis)
        {
            DrawLabel(drawingContext, "Network", (NetworkHeight / 2) - 7, axis);
            DrawLabel(drawingContext, "Events", EventTop + (EventHeight / 2) - 7, axis);
            DrawLabel(drawingContext, "Notes", NoteTop + (NoteHeight / 2) - 7, axis);
        }
    }

    /// <summary>Outages as stretches in the notes' manner; everything else as a tick.</summary>
    private void DrawEvents(DrawingContext context, double width)
    {
        foreach (LaneSpan span in Events.Where(each => !each.IsMoment))
        {
            if (LevelOf(span.Hue) is not { } brush)
            {
                continue;
            }

            double left = XOf(span.From, width);
            double right = Math.Max(left + 2, XOf(span.Until, width));

            if (right <= LeftGutter || left >= LeftGutter + width)
            {
                continue;
            }

            Rect box = new(left, EventTop, right - left, EventHeight);
            context.PushOpacity(0.3);
            context.DrawRoundedRectangle(brush, null, box, 3, 3);
            context.Pop();
            context.DrawRectangle(brush, null, new Rect(left, EventTop, 2, EventHeight));
        }

        foreach (LaneSpan span in Events.Where(each => each.IsMoment))
        {
            if (LevelOf(span.Hue) is not { } brush)
            {
                continue;
            }

            double x = XOf(span.From, width);

            if (x < LeftGutter - TickWidth || x > LeftGutter + width + TickWidth)
            {
                continue;
            }

            context.DrawRoundedRectangle(brush, null, new Rect(x - (TickWidth / 2), EventTop, TickWidth, EventHeight), 1, 1);
        }
    }

    private void DrawNotes(DrawingContext context, double width)
    {
        foreach (LaneSpan note in Notes)
        {
            double left = XOf(note.From, width);
            double right = Math.Max(left + 6, XOf(note.Until, width));

            if (right <= LeftGutter || left >= LeftGutter + width || NoteBrush is not { } fill)
            {
                continue;
            }

            Rect box = new(left, NoteTop, right - left, NoteHeight);
            context.PushOpacity(0.28);
            context.DrawRoundedRectangle(fill, null, box, 4, 4);
            context.Pop();
            context.DrawRectangle(fill, null, new Rect(left, NoteTop, 2, NoteHeight));

            if (NoteTextBrush is { } ink && box.Width > 24)
            {
                FormattedText text = Text(note.Label, ink);
                text.MaxTextWidth = Math.Max(1, Math.Min(box.Right, LeftGutter + width) - Math.Max(box.Left, LeftGutter) - 10);
                text.MaxLineCount = 1;
                text.Trimming = TextTrimming.CharacterEllipsis;
                context.DrawText(text, new Point(Math.Max(box.Left, LeftGutter) + 6, NoteTop + ((NoteHeight - text.Height) / 2)));
            }
        }
    }

    private void DrawLabel(DrawingContext context, string label, double y, Brush brush)
    {
        FormattedText text = Text(label, brush);
        context.DrawText(text, new Point(LeftGutter - 8 - text.Width, y));
    }

    private FormattedText Text(string text, Brush brush) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        LabelSize,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
