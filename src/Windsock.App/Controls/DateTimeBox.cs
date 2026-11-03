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
/// <remarks>
/// The field takes no focus of its own: the button inside it does, so Tab
/// stops once, on something a screen reader can name. What the field is for is
/// <see cref="Label"/>, which the button says with the moment chosen
/// (<see cref="SpokenText"/>); a name set on the field itself would reach
/// nobody, since the field has no automation peer.
/// </remarks>
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

    private static readonly DependencyPropertyKey Uses12HourKey = DependencyProperty.RegisterReadOnly(
        nameof(Uses12Hour),
        typeof(bool),
        typeof(DateTimeBox),
        new PropertyMetadata(true));

    public static readonly DependencyProperty Uses12HourProperty = Uses12HourKey.DependencyProperty;

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label),
        typeof(string),
        typeof(DateTimeBox),
        new PropertyMetadata(string.Empty, OnLabelChanged));

    private static readonly DependencyPropertyKey SpokenTextKey = DependencyProperty.RegisterReadOnly(
        nameof(SpokenText),
        typeof(string),
        typeof(DateTimeBox),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SpokenTextProperty = SpokenTextKey.DependencyProperty;
    private Calendar? _calendar;
    private TextBox? _time;
    private bool _syncing;

    static DateTimeBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(DateTimeBox),
            new FrameworkPropertyMetadata(typeof(DateTimeBox)));

        // The button inside is the one stop: the field itself, focusable as a
        // Control is by default, was a second Tab stop with nothing to say.
        FocusableProperty.OverrideMetadata(typeof(DateTimeBox), new FrameworkPropertyMetadata(false));
        KeyboardNavigation.IsTabStopProperty.OverrideMetadata(typeof(DateTimeBox), new FrameworkPropertyMetadata(false));
    }

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

    /// <summary>What the field is for - "Measure from" - said by a screen reader before the moment.</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>
    /// The field in words for a screen reader: what it is for and the moment
    /// chosen, "Measure from, 5 Oct 2026, 9:24 AM". The button inside is named
    /// with it.
    /// </summary>
    public string SpokenText
    {
        get => (string)GetValue(SpokenTextProperty);
        private set => SetValue(SpokenTextKey, value);
    }

    /// <summary>
    /// Whether the time is written on a 12-hour clock, with the button that
    /// flips the half of the day. Windows' own choice, read each time the
    /// value is shown; on a 24-hour clock the button is not offered.
    /// </summary>
    public bool Uses12Hour
    {
        get => (bool)GetValue(Uses12HourProperty);
        private set => SetValue(Uses12HourKey, value);
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

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DateTimeBox box)
        {
            box.Speak();
        }
    }

    private void Speak() =>
        SpokenText = Label.Length == 0 || DisplayText.Length == 0
            ? Label + DisplayText
            : string.Concat(Label, ", ", DisplayText);

    private void Show(DateTime moment)
    {
        TimeOnly clock = TimeOnly.FromDateTime(moment);
        ClockPatterns patterns = ClockFormat.Current;

        Uses12Hour = !patterns.Is24Hour;
        DisplayText = moment.ToString(string.Concat("d MMM yyyy, ", patterns.ShortTime), patterns.Format);
        Meridiem = Half(clock, patterns.Format);
        Speak();

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
                _time.Text = TimeText(moment);
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
        else if (e.Key == Key.Escape && _time is not null && _time.Text != TimeText(Value))
        {
            // A first Escape puts back a time half typed; a second goes on to close the picker.
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

        bool read = Uses12Hour
            ? ClockTime.TryParseClock(_time.Text, afternoon, out TimeOnly time)
            : ClockTime.TryParse(_time.Text, out time);

        if (read)
        {
            Value = Value.Date + time.ToTimeSpan();
        }
        else
        {
            Show(Value);
        }
    }

    /// <summary>The time of a moment as the time box writes it, on the clock Windows uses.</summary>
    private string TimeText(DateTime moment)
    {
        TimeOnly clock = TimeOnly.FromDateTime(moment);
        return Uses12Hour ? ClockTime.FormatClock(clock) : ClockTime.Format(clock);
    }

    private void Flip()
    {
        Commit();

        TimeOnly clock = TimeOnly.FromDateTime(Value);
        Value = Value.Date + ClockTime.WithMeridiem(clock, !ClockTime.IsAfternoon(clock)).ToTimeSpan();
    }

    /// <summary>The culture's own word for the half of the day, or AM and PM when it has none.</summary>
    private static string Half(TimeOnly clock, DateTimeFormatInfo format)
    {
        string word = ClockTime.IsAfternoon(clock) ? format.PMDesignator : format.AMDesignator;
        return string.IsNullOrWhiteSpace(word) ? ClockTime.Meridiem(clock) : word;
    }

    private static DateTime Truncate(DateTime moment) =>
        new(moment.Year, moment.Month, moment.Day, moment.Hour, moment.Minute, 0, moment.Kind);
}
