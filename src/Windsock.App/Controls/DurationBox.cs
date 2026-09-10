using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Windsock.Core.Formatting;

namespace Windsock.App.Controls;

/// <summary>One entry in the unit half of a <see cref="DurationBox"/>.</summary>
public sealed record TimeUnitOption(TimeUnit Unit, string Label)
{
    /// <summary>Returns the label, so automation announces it rather than the type.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// A duration entry field: a number on the left, its unit on the right.
/// </summary>
public sealed class DurationBox : Control
{
    private static readonly DependencyPropertyKey HasTextPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasText),
            typeof(bool),
            typeof(DurationBox),
            new PropertyMetadata(false));

    public static readonly DependencyProperty HasTextProperty = HasTextPropertyKey.DependencyProperty;

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(TimeSpan?),
            typeof(DurationBox),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnValueChanged));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(
            nameof(Minimum),
            typeof(TimeSpan),
            typeof(DurationBox),
            new PropertyMetadata(TimeSpan.FromMilliseconds(1)));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(
            nameof(Maximum),
            typeof(TimeSpan),
            typeof(DurationBox),
            new PropertyMetadata(TimeSpan.FromHours(24)));

    public static readonly DependencyProperty PlaceholderTextProperty =
        DependencyProperty.Register(
            nameof(PlaceholderText),
            typeof(string),
            typeof(DurationBox),
            new PropertyMetadata("Auto"));

    public static readonly DependencyProperty UnitsProperty =
        DependencyProperty.Register(
            nameof(Units),
            typeof(IReadOnlyList<TimeUnitOption>),
            typeof(DurationBox),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedUnitProperty =
        DependencyProperty.Register(
            nameof(SelectedUnit),
            typeof(TimeUnit),
            typeof(DurationBox),
            new PropertyMetadata(TimeUnit.Milliseconds, OnSelectedUnitChanged));

    private TextBox? _number;
    private Picker? _unit;
    private bool _updating;

    public DurationBox() =>
        Units = [.. TimeUnits.All.Select(unit => new TimeUnitOption(unit, TimeUnits.Label(unit)))];

    /// <summary>The entered duration, or <see langword="null"/> when unset.</summary>
    public TimeSpan? Value
    {
        get => (TimeSpan?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Shortest duration accepted. Shorter entries are clamped up.</summary>
    public TimeSpan Minimum
    {
        get => (TimeSpan)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>Longest duration accepted. Longer entries are clamped down.</summary>
    public TimeSpan Maximum
    {
        get => (TimeSpan)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>Text shown while the field is empty.</summary>
    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>Units offered by the right-hand picker.</summary>
    public IReadOnlyList<TimeUnitOption>? Units
    {
        get => (IReadOnlyList<TimeUnitOption>?)GetValue(UnitsProperty);
        set => SetValue(UnitsProperty, value);
    }

    /// <summary>Unit the entered number is expressed in.</summary>
    public TimeUnit SelectedUnit
    {
        get => (TimeUnit)GetValue(SelectedUnitProperty);
        set => SetValue(SelectedUnitProperty, value);
    }

    /// <summary>
    /// Whether the field currently holds any text. Drives the unit half's
    /// visibility.
    /// </summary>
    public bool HasText
    {
        get => (bool)GetValue(HasTextProperty);
        private set => SetValue(HasTextPropertyKey, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_number is not null)
        {
            _number.TextChanged -= OnNumberTextChanged;
            _number.KeyDown -= OnNumberKeyDown;
            _number.PreviewTextInput -= OnNumberPreviewTextInput;
        }

        _number = GetTemplateChild("PART_Number") as TextBox;
        _unit = GetTemplateChild("PART_Unit") as Picker;

        if (_number is not null)
        {
            _number.TextChanged += OnNumberTextChanged;
            _number.KeyDown += OnNumberKeyDown;
            _number.PreviewTextInput += OnNumberPreviewTextInput;
        }

        Display();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((DurationBox)d).Display();

    private static void OnSelectedUnitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (DurationBox)d;
        if (!box._updating)
        {
            box.Commit();
        }
    }

    private void OnNumberPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Digits and one decimal separator only.
        foreach (char character in e.Text)
        {
            bool isSeparator = character is '.' or ',';
            if (!char.IsAsciiDigit(character) && !isSeparator)
            {
                e.Handled = true;
                return;
            }
        }
    }

    private void OnNumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            Commit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Display();
            e.Handled = true;
        }
    }

    private void OnNumberTextChanged(object sender, TextChangedEventArgs e) =>
        HasText = _number is { Text.Length: > 0 };

    /// <summary>
    /// Commits once focus leaves the control as a whole.
    /// </summary>
    protected override void OnIsKeyboardFocusWithinChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusWithinChanged(e);

        if (e.NewValue is false)
        {
            Commit();
        }
    }

    private void Commit()
    {
        if (_number is null || _updating)
        {
            return;
        }

        string text = _number.Text.Trim();

        if (text.Length == 0)
        {
            Value = null;
            Display();
            return;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double entered)
            || entered <= 0)
        {
            Display();
            return;
        }

        TimeSpan candidate = TimeUnits.ToTimeSpan(entered, SelectedUnit);
        Value = candidate < Minimum ? Minimum : candidate > Maximum ? Maximum : candidate;

        Display();
    }

    private void Display()
    {
        if (_number is null)
        {
            HasText = Value.HasValue;
            return;
        }

        _updating = true;
        try
        {
            if (Value is not { } value)
            {
                _number.Text = string.Empty;
                HasText = false;
                return;
            }

            (double number, TimeUnit unit) = TimeUnits.Split(value);
            _number.Text = number.ToString("0.###", CultureInfo.CurrentCulture);
            SelectedUnit = unit;
            HasText = true;
        }
        finally
        {
            _updating = false;
        }
    }
}
