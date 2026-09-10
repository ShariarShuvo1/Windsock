using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Windsock.Core.Formatting;
using Calendar = System.Windows.Controls.Calendar;

namespace Windsock.App.Controls;

/// <summary>
/// Picks a moment: a day from a calendar and a time down to the minute.
/// </summary>
[TemplatePart(Name = CalendarPart, Type = typeof(Calendar))]
[TemplatePart(Name = TimePart, Type = typeof(TextBox))]
[TemplatePart(Name = MeridiemPart, Type = typeof(System.Windows.Controls.Primitives.ButtonBase))]
[TemplatePart(Name = NowPart, Type = typeof(System.Windows.Controls.Primitives.ButtonBase))]
[TemplatePart(Name = DonePart, Type = typeof(System.Windows.Controls.Primitives.ButtonBase))]
public sealed class DateTimeBox : Control
{
    private const string CalendarPart = "PART_Calendar";
    private const string TimePart = "PART_Time";
    private const string MeridiemPart = "PART_Meridiem";
    private const string NowPart = "PART_Now";
    private const string DonePart = "PART_Done";

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(DateTime),
        typeof(DateTimeBox),
        new FrameworkPropertyMetadata(
            default(DateTime),
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValueChanged));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen),
        typeof(bool),
        typeof(DateTimeBox),
        new PropertyMetadata(false, OnIsOpenChanged));

    private static readonly DependencyPropertyKey DisplayTextKey = DependencyProperty.RegisterReadOnly(
        nameof(DisplayText),
        typeof(string),
        typeof(DateTimeBox),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DisplayTextProperty = DisplayTextKey.DependencyProperty;

    private static readonly DependencyPropertyKey MeridiemKey = DependencyProperty.RegisterReadOnly(
        nameof(Meridiem),
        typeof(string),
        typeof(DateTimeBox),
        new PropertyMetadata("AM"));

    public static readonly DependencyProperty MeridiemProperty = MeridiemKey.DependencyProperty;
    private Calendar? _calendar;
    private TextBox? _time;
    private bool _syncing;

    static DateTimeBox() =>
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(DateTimeBox),
            new FrameworkPropertyMetadata(typeof(DateTimeBox)));

    /// <summary>The moment being picked, to the minute.</summary>
    public DateTime Value
    {
        get => (DateTime)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Whether the picker is showing.</summary>
    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>The moment written out, for the closed field.</summary>
    public string DisplayText
    {
        get => (string)GetValue(DisplayTextProperty);
        private set => SetValue(DisplayTextKey, value);
    }

    /// <summary>Which half of the day the time is in, for the button.</summary>
    public string Meridiem
    {
        get => (string)GetValue(MeridiemProperty);
        private set => SetValue(MeridiemKey, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_calendar is not null)
        {
            _calendar.SelectedDatesChanged -= OnDayPicked;
        }

        if (_time is not null)
        {
            _time.LostFocus -= OnTimeCommitted;
            _time.KeyDown -= OnTimeKey;
        }

        _calendar = GetTemplateChild(CalendarPart) as Calendar;
        _time = GetTemplateChild(TimePart) as TextBox;

        if (_calendar is not null)
        {
            _calendar.SelectedDatesChanged += OnDayPicked;
        }

        if (_time is not null)
        {
            _time.LostFocus += OnTimeCommitted;
            _time.KeyDown += OnTimeKey;
        }

        if (GetTemplateChild(MeridiemPart) is System.Windows.Controls.Primitives.ButtonBase half)
        {
            half.Click += (_, _) => Flip();
        }

        if (GetTemplateChild(NowPart) is System.Windows.Controls.Primitives.ButtonBase now)
        {
            now.Click += (_, _) => Value = Truncate(DateTime.Now);
        }

        if (GetTemplateChild(DonePart) is System.Windows.Controls.Primitives.ButtonBase done)
        {
            done.Click += (_, _) => IsOpen = false;
        }

        Show(Value);
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DateTimeBox box && !(bool)e.NewValue)
        {
            box.Commit();
        }
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DateTimeBox box)
        {
            box.Show((DateTime)e.NewValue);
        }
    }

    private void Show(DateTime moment)
    {
        TimeOnly clock = TimeOnly.FromDateTime(moment);

        DisplayText = string.Format(
            CultureInfo.CurrentCulture,
            "{0:d MMM yyyy}, {1} {2}",
            moment,
            ClockTime.FormatClock(clock),
            ClockTime.Meridiem(clock));

        Meridiem = ClockTime.Meridiem(clock);

        if (_syncing)
        {
            return;
        }

        _syncing = true;

        try
        {
            if (_calendar is not null)
            {
                _calendar.SelectedDate = moment.Date;
                _calendar.DisplayDate = moment.Date;
            }

            if (_time is not null)
            {
                _time.Text = ClockTime.FormatClock(clock);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnDayPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _calendar?.SelectedDate is not { } day)
        {
            return;
        }

        // The day changes; the time stays as it was.
        Value = day.Date + Value.TimeOfDay;
    }

    private void OnTimeKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            Commit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Show(Value);
            e.Handled = true;
        }
    }

    private void OnTimeCommitted(object sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        if (_syncing || _time is null)
        {
            return;
        }

        bool afternoon = ClockTime.IsAfternoon(TimeOnly.FromDateTime(Value));

        if (ClockTime.TryParseClock(_time.Text, afternoon, out TimeOnly time))
        {
            Value = Value.Date + time.ToTimeSpan();
        }
        else
        {
            Show(Value);
        }
    }

    private void Flip()
    {
        Commit();

        TimeOnly clock = TimeOnly.FromDateTime(Value);
        Value = Value.Date + ClockTime.WithMeridiem(clock, !ClockTime.IsAfternoon(clock)).ToTimeSpan();
    }

    private static DateTime Truncate(DateTime moment) =>
        new(moment.Year, moment.Month, moment.Day, moment.Hour, moment.Minute, 0, moment.Kind);
}
