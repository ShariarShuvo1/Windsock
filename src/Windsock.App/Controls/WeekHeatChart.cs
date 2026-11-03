using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using Windsock.Core.Formatting;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>One hour of the week in a <see cref="WeekHeatChart"/>.</summary>
/// <param name="Row">The day, 0 being the row the week starts on.</param>
/// <param name="Hour">The hour of the day, 0 to 23.</param>
/// <param name="Level">How busy, 0 to 1 against the busiest hour.</param>
/// <param name="Detail">The hour in a sentence.</param>
public sealed record WeekCell(int Row, int Hour, double Level, string Detail);

/// <summary>
/// When in the week the network is busiest: seven rows of 24 hours, each shaded
/// by its average.
/// </summary>
/// <remarks>
/// Pointing at an hour, or moving with the arrow keys once the grid has focus,
/// chooses it; <see cref="SelectedDetail"/> says what it averaged.
/// </remarks>
public sealed class WeekHeatChart : FrameworkElement
{
    private const double LeftGutter = 38;
    private const double BottomGutter = 18;
    private const double Gap = 2;
    private const double RowHeight = 20;
    private const double LabelSize = 10;

    public static readonly DependencyProperty CellsProperty = Register<IReadOnlyList<WeekCell>>(nameof(Cells), [], OnCellsChanged);

    public static readonly DependencyProperty RowLabelsProperty = Register<IReadOnlyList<string>>(nameof(RowLabels), []);

    public static readonly DependencyProperty ShadeBrushProperty = Register<Brush?>(nameof(ShadeBrush), null);

    public static readonly DependencyProperty EmptyBrushProperty = Register<Brush?>(nameof(EmptyBrush), null);

    public static readonly DependencyProperty AxisBrushProperty = Register<Brush?>(nameof(AxisBrush), null);

    public static readonly DependencyProperty SelectedBrushProperty = Register<Brush?>(nameof(SelectedBrush), null);

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex),
        typeof(int),
        typeof(WeekHeatChart),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedChanged));

    private static readonly DependencyPropertyKey SelectedDetailKey = DependencyProperty.RegisterReadOnly(
        nameof(SelectedDetail), typeof(string), typeof(WeekHeatChart), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SelectedDetailProperty = SelectedDetailKey.DependencyProperty;

    public WeekHeatChart() => Focusable = true;

    /// <summary>The 168 hours, row by row.</summary>
    public IReadOnlyList<WeekCell> Cells
    {
        get => (IReadOnlyList<WeekCell>)GetValue(CellsProperty);
        set => SetValue(CellsProperty, value);
    }

    /// <summary>The seven days' short names, in row order.</summary>
    public IReadOnlyList<string> RowLabels
    {
        get => (IReadOnlyList<string>)GetValue(RowLabelsProperty);
        set => SetValue(RowLabelsProperty, value);
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

    /// <summary>The chosen hour in a sentence.</summary>
    public string SelectedDetail => (string)GetValue(SelectedDetailProperty);

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this, Summary);

    /// <summary>What a screen reader is told: the chosen hour, else the busiest.</summary>
    private string Summary()
    {
        if (SelectedDetail.Length > 0)
        {
            return SelectedDetail;
        }

        WeekCell? busiest = Cells.MaxBy(cell => cell.Level);
        return busiest is null || busiest.Level <= 0 ? "Nothing recorded." : "The busiest hour: " + busiest.Detail;
    }

    private static DependencyProperty Register<T>(string name, T fallback, PropertyChangedCallback? changed = null) =>
        DependencyProperty.Register(name, typeof(T), typeof(WeekHeatChart), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender, changed));

    private static void OnCellsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WeekHeatChart chart)
        {
            if (chart.SelectedIndex >= chart.Cells.Count)
            {
                chart.SelectedIndex = -1;
            }

            chart.Describe();
        }
    }

    private static void OnSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WeekHeatChart chart)
        {
            chart.Describe();
        }
    }

    private void Describe()
    {
        string detail = SelectedIndex >= 0 && SelectedIndex < Cells.Count ? Cells[SelectedIndex].Detail : string.Empty;
        SetValue(SelectedDetailKey, detail);
        AutomationProperties.SetHelpText(this, detail);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, (7 * (RowHeight + Gap)) + BottomGutter);

    private int IndexOf(int row, int hour)
    {
        for (int index = 0; index < Cells.Count; index++)
        {
            if (Cells[index].Row == row && Cells[index].Hour == hour)
            {
                return index;
            }
        }

        return -1;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        ArgumentNullException.ThrowIfNull(e);

        if (Cells.Count == 0)
        {
            return;
        }

        WeekCell at = SelectedIndex >= 0 && SelectedIndex < Cells.Count ? Cells[SelectedIndex] : Cells[0];
        (int row, int hour) = e.Key switch
        {
            Key.Left => (at.Row, at.Hour - 1),
            Key.Right => (at.Row, at.Hour + 1),
            Key.Up => (at.Row - 1, at.Hour),
            Key.Down => (at.Row + 1, at.Hour),
            Key.Home => (at.Row, 0),
            Key.End => (at.Row, 23),
            _ => (int.MinValue, 0),
        };

        if (row == int.MinValue)
        {
            return;
        }

        int index = IndexOf(Math.Clamp(row, 0, 6), Math.Clamp(hour, 0, 23));

        if (index >= 0)
        {
            SelectedIndex = index;
        }

        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);

        if (SelectedIndex < 0 && Cells.Count > 0)
        {
            SelectedIndex = 0;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        ArgumentNullException.ThrowIfNull(e);

        Point at = e.GetPosition(this);
        double width = (ActualWidth - LeftGutter) / 24;
        int hour = (int)Math.Floor((at.X - LeftGutter) / width);
        int row = (int)Math.Floor(at.Y / (RowHeight + Gap));

        if (hour is >= 0 and < 24 && row is >= 0 and < 7)
        {
            int index = IndexOf(row, hour);

            if (index >= 0)
            {
                SelectedIndex = index;
            }
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

        double width = ActualWidth;
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, ActualHeight));

        double cellWidth = (width - LeftGutter) / 24;

        if (cellWidth <= Gap)
        {
            return;
        }

        for (int index = 0; index < Cells.Count; index++)
        {
            WeekCell cell = Cells[index];
            Rect box = new(LeftGutter + (cell.Hour * cellWidth), cell.Row * (RowHeight + Gap), cellWidth - Gap, RowHeight);

            if (EmptyBrush is { } empty)
            {
                drawingContext.DrawRoundedRectangle(empty, null, box, 3, 3);
            }

            if (cell.Level > 0 && ShadeBrush is { } shade)
            {
                drawingContext.PushOpacity(0.12 + (0.88 * Math.Clamp(cell.Level, 0, 1)));
                drawingContext.DrawRoundedRectangle(shade, null, box, 3, 3);
                drawingContext.Pop();
            }

            if (index == SelectedIndex && SelectedBrush is { } ring)
            {
                Pen pen = new(ring, 1.5);
                pen.Freeze();
                box.Inflate(1, 1);
                drawingContext.DrawRoundedRectangle(null, pen, box, 4, 4);
            }
        }

        if (AxisBrush is not { } axis)
        {
            return;
        }

        for (int row = 0; row < Math.Min(7, RowLabels.Count); row++)
        {
            Label(drawingContext, RowLabels[row], 0, (row * (RowHeight + Gap)) + 3, axis);
        }

        foreach (int hour in new[] { 0, 6, 12, 18 })
        {
            string text = ClockFormat.Current.Hour(new DateTime(2000, 1, 1, hour, 0, 0));
            Label(drawingContext, text, LeftGutter + (hour * cellWidth), (7 * (RowHeight + Gap)) + 2, axis);
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
