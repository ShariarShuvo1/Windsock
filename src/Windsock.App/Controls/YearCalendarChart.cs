using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>One day of a <see cref="YearCalendarChart"/>.</summary>
/// <param name="Date">The day.</param>
/// <param name="Shade">How dark: 0 for nothing, 1 to 4 by quartile.</param>
/// <param name="Detail">The day in a sentence.</param>
public sealed record CalendarCell(DateOnly Date, int Shade, string Detail);

/// <summary>
/// Every day of a year in a grid, a column a week, each day shaded by how much
/// it used.
/// </summary>
/// <remarks>
/// Pointing at a day, or moving with the arrow keys once the grid has focus,
/// chooses it, and <see cref="SelectedDetail"/> says what it used (also the
/// help text screen readers read). Left and Right step a week, Up and Down a
/// day, Home and End go to the year's ends.
/// </remarks>
public sealed class YearCalendarChart : FrameworkElement
{
    private const double LeftGutter = 30;
    private const double TopGutter = 18;
    private const double Gap = 2;
    private const double LargestCell = 15;
    private const double SmallestCell = 5;
    private const double LabelSize = 10;
    private static readonly double[] Opacities = [0, 0.22, 0.45, 0.7, 1];

    public static readonly DependencyProperty DaysProperty = Register<IReadOnlyList<CalendarCell>>(nameof(Days), [], OnDaysChanged, measures: true);

    public static readonly DependencyProperty FirstDayOfWeekProperty = Register(nameof(FirstDayOfWeek), DayOfWeek.Monday, null, measures: true);

    public static readonly DependencyProperty ShadeBrushProperty = Register<Brush?>(nameof(ShadeBrush), null);

    public static readonly DependencyProperty EmptyBrushProperty = Register<Brush?>(nameof(EmptyBrush), null);

    public static readonly DependencyProperty AxisBrushProperty = Register<Brush?>(nameof(AxisBrush), null);

    public static readonly DependencyProperty SelectedBrushProperty = Register<Brush?>(nameof(SelectedBrush), null);

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex),
        typeof(int),
        typeof(YearCalendarChart),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedChanged));

    private static readonly DependencyPropertyKey SelectedDetailKey = DependencyProperty.RegisterReadOnly(
        nameof(SelectedDetail), typeof(string), typeof(YearCalendarChart), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SelectedDetailProperty = SelectedDetailKey.DependencyProperty;

    public YearCalendarChart() => Focusable = true;

    /// <summary>The days, in order.</summary>
    public IReadOnlyList<CalendarCell> Days
    {
        get => (IReadOnlyList<CalendarCell>)GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    /// <summary>The day each column starts on.</summary>
    public DayOfWeek FirstDayOfWeek
    {
        get => (DayOfWeek)GetValue(FirstDayOfWeekProperty);
        set => SetValue(FirstDayOfWeekProperty, value);
    }

    public Brush? ShadeBrush
    {
        get => (Brush?)GetValue(ShadeBrushProperty);
        set => SetValue(ShadeBrushProperty, value);
    }

    public Brush? EmptyBrush
    {
        get => (Brush?)GetValue(EmptyBrushProperty);
        set => SetValue(EmptyBrushProperty, value);
    }

    public Brush? AxisBrush
    {
        get => (Brush?)GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    /// <summary>The ring round the chosen day.</summary>
    public Brush? SelectedBrush
    {
        get => (Brush?)GetValue(SelectedBrushProperty);
        set => SetValue(SelectedBrushProperty, value);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>The chosen day in a sentence.</summary>
    public string SelectedDetail => (string)GetValue(SelectedDetailProperty);

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this, Summary);

    /// <summary>What a screen reader is told: the chosen day, else how many days used anything and the busiest.</summary>
    private string Summary()
    {
        if (SelectedDetail.Length > 0)
        {
            return SelectedDetail;
        }

        IReadOnlyList<CalendarCell> days = Days;
        int used = days.Count(day => day.Shade > 0);
        CalendarCell? busiest = days.Where(day => day.Shade == 4).LastOrDefault();

        return used == 0
            ? "No day used anything."
            : string.Format(CultureInfo.CurrentCulture, "{0} days used something.{1}", used, busiest is null ? string.Empty : " One of the busiest: " + busiest.Detail);
    }

    private static DependencyProperty Register<T>(string name, T fallback, PropertyChangedCallback? changed = null, bool measures = false) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(YearCalendarChart),
            new FrameworkPropertyMetadata(
                fallback,
                FrameworkPropertyMetadataOptions.AffectsRender | (measures ? FrameworkPropertyMetadataOptions.AffectsMeasure : 0),
                changed));

    private static void OnDaysChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is YearCalendarChart chart)
        {
            if (chart.SelectedIndex >= chart.Days.Count)
            {
                chart.SelectedIndex = -1;
            }

            chart.Describe();
        }
    }

    private static void OnSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is YearCalendarChart chart)
        {
            chart.Describe();
        }
    }

    private void Describe()
    {
        string detail = SelectedIndex >= 0 && SelectedIndex < Days.Count ? Days[SelectedIndex].Detail : string.Empty;
        SetValue(SelectedDetailKey, detail);
        AutomationProperties.SetHelpText(this, detail);
    }

    private DateOnly FirstColumn => Days.Count == 0
        ? default
        : Days[0].Date.AddDays(-(((int)Days[0].Date.DayOfWeek - (int)FirstDayOfWeek + 7) % 7));

    private int Columns => Days.Count == 0 ? 53 : ((Days[^1].Date.DayNumber - FirstColumn.DayNumber) / 7) + 1;

    private double Cell(double width) =>
        Math.Clamp(((width - LeftGutter) / Math.Max(1, Columns)) - Gap, SmallestCell, LargestCell);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width;
        double cell = Cell(width);
        return new Size(Math.Min(width, LeftGutter + (Columns * (cell + Gap))), TopGutter + (7 * (cell + Gap)));
    }

    private (int Column, int Row) Place(DateOnly date) =>
        ((date.DayNumber - FirstColumn.DayNumber) / 7, ((int)date.DayOfWeek - (int)FirstDayOfWeek + 7) % 7);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        ArgumentNullException.ThrowIfNull(e);

        int count = Days.Count;

        if (count == 0)
        {
            return;
        }

        int index = SelectedIndex < 0 ? count - 1 : SelectedIndex;
        int moved = e.Key switch
        {
            Key.Left => index - 7,
            Key.Right => index + 7,
            Key.Up => index - 1,
            Key.Down => index + 1,
            Key.Home => 0,
            Key.End => count - 1,
            _ => int.MinValue,
        };

        if (moved == int.MinValue)
        {
            return;
        }

        SelectedIndex = Math.Clamp(moved, 0, count - 1);
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);

        if (SelectedIndex < 0 && Days.Count > 0)
        {
            SelectedIndex = Days.Count - 1;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        ArgumentNullException.ThrowIfNull(e);

        Point at = e.GetPosition(this);
        double cell = Cell(ActualWidth);
        int column = (int)Math.Floor((at.X - LeftGutter) / (cell + Gap));
        int row = (int)Math.Floor((at.Y - TopGutter) / (cell + Gap));

        if (column < 0 || row is < 0 or > 6 || Days.Count == 0)
        {
            return;
        }

        int index = (column * 7) + row - (((int)Days[0].Date.DayOfWeek - (int)FirstDayOfWeek + 7) % 7);

        if (index >= 0 && index < Days.Count)
        {
            SelectedIndex = index;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        IReadOnlyList<CalendarCell> days = Days;

        if (days.Count == 0)
        {
            return;
        }

        double cell = Cell(ActualWidth);
        double radius = Math.Min(3, cell / 4);
        int lastMonth = 0;

        for (int index = 0; index < days.Count; index++)
        {
            CalendarCell day = days[index];
            (int column, int row) = Place(day.Date);
            double x = LeftGutter + (column * (cell + Gap));
            double y = TopGutter + (row * (cell + Gap));
            Rect box = new(x, y, cell, cell);

            if (day.Shade <= 0 || ShadeBrush is null)
            {
                if (EmptyBrush is { } empty)
                {
                    drawingContext.DrawRoundedRectangle(empty, null, box, radius, radius);
                }
            }
            else
            {
                drawingContext.PushOpacity(Opacities[Math.Clamp(day.Shade, 0, 4)]);
                drawingContext.DrawRoundedRectangle(ShadeBrush, null, box, radius, radius);
                drawingContext.Pop();
            }

            if (index == SelectedIndex && SelectedBrush is { } ring)
            {
                Pen pen = new(ring, 1.5);
                pen.Freeze();
                drawingContext.DrawRoundedRectangle(null, pen, new Rect(x - 1, y - 1, cell + 2, cell + 2), radius + 1, radius + 1);
            }

            if (day.Date.Month != lastMonth && AxisBrush is { } axis && (day.Date.Day == 1 || index == 0))
            {
                lastMonth = day.Date.Month;
                Label(drawingContext, day.Date.ToString("MMM", CultureInfo.CurrentCulture), x, 1, axis);
            }
        }

        if (AxisBrush is { } labels)
        {
            for (int row = 0; row < 7; row += 2)
            {
                DayOfWeek weekday = (DayOfWeek)(((int)FirstDayOfWeek + row) % 7);
                Label(drawingContext, CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(weekday), 0, TopGutter + (row * (cell + Gap)) + (cell / 2) - 6, labels);
            }
        }
    }

    private void Label(DrawingContext context, string text, double x, double y, Brush brush)
    {
        FormattedText formatted = new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            LabelSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        context.DrawText(formatted, new Point(x, y));
    }
}
