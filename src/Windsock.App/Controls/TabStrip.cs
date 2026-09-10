using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Windsock.App.Controls;

/// <summary>
/// A tab control whose strip stays on one line and scrolls sideways once the
/// tabs no longer fit.
/// </summary>
[TemplatePart(Name = ScrollPart, Type = typeof(ScrollViewer))]
[TemplatePart(Name = ScrollBackPart, Type = typeof(ButtonBase))]
[TemplatePart(Name = ScrollForwardPart, Type = typeof(ButtonBase))]
public sealed class TabStrip : TabControl
{
    private const string ScrollPart = "PART_Scroll";
    private const string ScrollBackPart = "PART_ScrollBack";
    private const string ScrollForwardPart = "PART_ScrollForward";
    private const double WheelStep = 84;
    private const double ButtonStepFraction = 0.8;
    private const double EdgePadding = 12;
    private const double Epsilon = 0.5;

    private static readonly Duration ScrollDuration = new(TimeSpan.FromMilliseconds(220));

    private static readonly DependencyProperty ScrollOffsetProperty =
        DependencyProperty.Register(
            "ScrollOffset",
            typeof(double),
            typeof(TabStrip),
            new PropertyMetadata(0d, OnScrollOffsetChanged));

    private static readonly DependencyPropertyKey CanScrollBackPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(CanScrollBack),
            typeof(bool),
            typeof(TabStrip),
            new PropertyMetadata(false));

    private static readonly DependencyPropertyKey CanScrollForwardPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(CanScrollForward),
            typeof(bool),
            typeof(TabStrip),
            new PropertyMetadata(false));

    private static readonly DependencyPropertyKey IsOverflowingPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsOverflowing),
            typeof(bool),
            typeof(TabStrip),
            new PropertyMetadata(false));

    public static readonly DependencyProperty CanScrollBackProperty =
        CanScrollBackPropertyKey.DependencyProperty;

    public static readonly DependencyProperty CanScrollForwardProperty =
        CanScrollForwardPropertyKey.DependencyProperty;

    public static readonly DependencyProperty IsOverflowingProperty =
        IsOverflowingPropertyKey.DependencyProperty;

    private ScrollViewer? _scroll;
    private ButtonBase? _back;
    private ButtonBase? _forward;
    private double _target;
    private bool _animating;
    private long _token;

    /// <summary>Whether tabs are hidden off the leading edge.</summary>
    public bool CanScrollBack
    {
        get => (bool)GetValue(CanScrollBackProperty);
        private set => SetValue(CanScrollBackPropertyKey, value);
    }

    /// <summary>Whether tabs are hidden off the trailing edge.</summary>
    public bool CanScrollForward
    {
        get => (bool)GetValue(CanScrollForwardProperty);
        private set => SetValue(CanScrollForwardPropertyKey, value);
    }

    /// <summary>Whether the tabs need more width than the strip has.</summary>
    public bool IsOverflowing
    {
        get => (bool)GetValue(IsOverflowingProperty);
        private set => SetValue(IsOverflowingPropertyKey, value);
    }

    private double Position => _animating ? _target : _scroll?.HorizontalOffset ?? 0;

    private double ButtonStep =>
        Math.Max(WheelStep, (_scroll?.ViewportWidth ?? 0) * ButtonStepFraction);

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        Detach();

        _scroll = GetTemplateChild(ScrollPart) as ScrollViewer;
        _back = GetTemplateChild(ScrollBackPart) as ButtonBase;
        _forward = GetTemplateChild(ScrollForwardPart) as ButtonBase;

        if (_scroll is not null)
        {
            _scroll.ScrollChanged += OnScrollChanged;
            _scroll.PreviewMouseWheel += OnScrollWheel;
        }

        if (_back is not null)
        {
            _back.Click += OnScrollBack;
        }

        if (_forward is not null)
        {
            _forward.Click += OnScrollForward;
        }
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ShowSelected));
    }

    private static void OnScrollOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TabStrip)d)._scroll?.ScrollToHorizontalOffset((double)e.NewValue);

    private void Detach()
    {
        if (_scroll is not null)
        {
            _scroll.ScrollChanged -= OnScrollChanged;
            _scroll.PreviewMouseWheel -= OnScrollWheel;
        }

        if (_back is not null)
        {
            _back.Click -= OnScrollBack;
        }

        if (_forward is not null)
        {
            _forward.Click -= OnScrollForward;
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_scroll is null)
        {
            return;
        }

        double limit = Math.Max(0, _scroll.ExtentWidth - _scroll.ViewportWidth);

        IsOverflowing = limit > Epsilon;
        CanScrollBack = IsOverflowing && _scroll.HorizontalOffset > Epsilon;
        CanScrollForward = IsOverflowing && _scroll.HorizontalOffset < limit - Epsilon;
    }

    private void OnScrollWheel(object sender, MouseWheelEventArgs e)
    {
        if (_scroll is null || _scroll.ExtentWidth <= _scroll.ViewportWidth + Epsilon)
        {
            return;
        }

        ScrollTo(Position - (e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * WheelStep));
        e.Handled = true;
    }

    private void OnScrollBack(object sender, RoutedEventArgs e) => ScrollTo(Position - ButtonStep);

    private void OnScrollForward(object sender, RoutedEventArgs e) => ScrollTo(Position + ButtonStep);

    private void ShowSelected()
    {
        if (_scroll is null || SelectedItem is null)
        {
            return;
        }

        if (ItemContainerGenerator.ContainerFromItem(SelectedItem) is not FrameworkElement container
            || !_scroll.IsAncestorOf(container))
        {
            return;
        }
        double left = _scroll.HorizontalOffset + container.TransformToAncestor(_scroll).Transform(default).X;
        double right = left + container.ActualWidth;

        if (left - EdgePadding < Position)
        {
            ScrollTo(left - EdgePadding);
        }
        else if (right + EdgePadding > Position + _scroll.ViewportWidth)
        {
            ScrollTo(right + EdgePadding - _scroll.ViewportWidth);
        }
    }

    private void ScrollTo(double offset)
    {
        if (_scroll is null)
        {
            return;
        }

        double limit = Math.Max(0, _scroll.ExtentWidth - _scroll.ViewportWidth);
        double target = Math.Clamp(offset, 0, limit);

        if (Math.Abs(target - Position) < Epsilon)
        {
            return;
        }

        if (!_animating)
        {
            SetValue(ScrollOffsetProperty, _scroll.HorizontalOffset);
        }

        _target = target;
        _animating = true;

        long token = ++_token;

        DoubleAnimation animation = new(target, ScrollDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        animation.Completed += (_, _) =>
        {
            if (token != _token)
            {
                return;
            }
            SetValue(ScrollOffsetProperty, target);
            BeginAnimation(ScrollOffsetProperty, null);
            _animating = false;
        };

        BeginAnimation(ScrollOffsetProperty, animation);
    }
}
