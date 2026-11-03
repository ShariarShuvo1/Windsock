using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>
/// Gives its child as much room as it asks for, and lets the reader move
/// around inside it.
/// </summary>
/// <remarks>
/// <para>
/// The keyboard moves it too. The view is a Tab stop of its own, ringed while
/// it has the focus: there the arrow keys move the picture, plus and minus
/// zoom about the middle, and 0 or Home fits the whole of it. From anything on
/// the picture (a node), Ctrl with an arrow moves it, plus and minus zoom and
/// Ctrl+0 fits. A node reached from the keyboard is brought into view, since
/// Tab otherwise stopped on nodes the clip had hidden. Only a key press does
/// (<see cref="KeyPress"/>): a click, and the focus WPF gives back to a node
/// when the window is activated again, bring nothing into view, so the view
/// stays where the mouse left it and the picture keeps fitting itself as it
/// grows. Clicking a node half out of sight leaves the view
/// where it was, as it always did.
/// </para>
/// <para>
/// To UI Automation it is a pane with the Scroll pattern, which is what
/// panning is, and the Invoke pattern, which fits the whole picture as a
/// double-click does; the picture inside names itself (the graph's own peer).
/// </para>
/// </remarks>
public sealed class PanZoomHost : Decorator
{
    private const double Smallest = 0.2;
    private const double Step = 60;
    private const double ZoomStep = 1.25;
    private const double Breathing = 24;
    private const double Largest = 3;
    private const double Dust = 0.03;
    private const double DefaultSmallestFit = 0.5;
    private const double LargestFit = 1.5;

    private readonly ScaleTransform _zoom = new(1, 1);
    private readonly TranslateTransform _pan = new();
    private readonly TransformGroup _view = new();

    private const double Framing = 0.28;
    private const double Framed = 0.2;
    private const double Steady = 0.5;
    private Point _grabbed;
    private Point _anchored;
    private bool _anchoring;
    private Size _clipped;
    private bool _dragging;
    private Point _pressed;
    private bool _moved;
    private double _beforeScale;
    private Point _beforePan;
    private bool _canReturn;
    private bool _touched;
    private bool _fitted;
    private Point _wantPan;
    private double _wantScale = 1;
    private Vector _panSpeed;
    private double _scaleSpeed;
    private bool _easing;
    private long _lastFrame;

    /// <summary>How far the corners of the view are rounded.</summary>
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius),
        typeof(double),
        typeof(PanZoomHost),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Room at the edges that a fit leaves clear.</summary>
    public static readonly DependencyProperty InsetProperty = DependencyProperty.Register(
        nameof(Inset),
        typeof(Thickness),
        typeof(PanZoomHost),
        new PropertyMetadata(default(Thickness)));

    /// <summary>
    /// How far a fit may shrink for this view in particular.
    /// </summary>
    public static readonly DependencyProperty SmallestFitProperty = DependencyProperty.Register(
        nameof(SmallestFit),
        typeof(double),
        typeof(PanZoomHost),
        new PropertyMetadata(DefaultSmallestFit));

    static PanZoomHost() => KeyPress.Watch();

    public PanZoomHost()
    {
        ClipToBounds = true;
        Focusable = true;
        SetResourceReference(FocusVisualStyleProperty, "FocusRingStyle");
        AddHandler(RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler(OnBringIntoView));

        _view.Children.Add(_zoom);
        _view.Children.Add(_pan);
    }

    /// <summary>How far the corners of the view are rounded.</summary>
    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    /// <summary>Room at the edges that a fit leaves clear.</summary>
    public Thickness Inset
    {
        get => (Thickness)GetValue(InsetProperty);
        set => SetValue(InsetProperty, value);
    }

    /// <summary>How far a fit may shrink before it stops trying.</summary>
    public double SmallestFit
    {
        get => (double)GetValue(SmallestFitProperty);
        set => SetValue(SmallestFitProperty, value);
    }

    /// <summary>How far in the view is zoomed, where one is life size.</summary>
    public double Zoom => _zoom.ScaleX;

    /// <summary>Whether the reader has moved the view from where it started.</summary>
    public bool IsMoved => _touched;

    /// <summary>Raised when the view is panned, zoomed or fitted.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>
    /// Raised when the reader clicks the canvas itself rather than anything on it.
    /// </summary>
    public event EventHandler? Cleared;

    /// <summary>
    /// Fits the whole picture, or puts back the view the last fit replaced.
    /// </summary>
    public void FitOrReturn()
    {
        if (_canReturn && !_touched)
        {
            Aim(_beforeScale, _beforePan, at: false);

            // Theirs again, so nothing frames it out from under them.
            _touched = true;
            _canReturn = false;
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _beforeScale = _wantScale;
        _beforePan = _wantPan;
        _canReturn = true;

        FitAll();
    }

    /// <summary>
    /// Frames the picture, keeping it large enough to read.
    /// </summary>
    public void Fit() => Frame(Math.Clamp(SmallestFit, Smallest, LargestFit));

    /// <summary>
    /// Frames the whole picture, at whatever size that takes.
    /// </summary>
    public void FitAll() => Frame(Dust);

    private void Frame(double floor)
    {
        if (Child is not { } child || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        Size content = child.DesiredSize;

        if (content.Width <= 0 || content.Height <= 0)
        {
            return;
        }
        Thickness inset = Inset;
        double roomWidth = Math.Max(1, ActualWidth - inset.Left - inset.Right);
        double roomHeight = Math.Max(1, ActualHeight - inset.Top - inset.Bottom);

        double scale;
        Point pan;

        if (child is IViewAnchor { IsFramedOnAnchor: true } held)
        {
            // Room for the farther side either way, so the anchor is the middle.
            Point anchor = held.Anchor;
            double across = 2 * Math.Max(anchor.X, content.Width - anchor.X);
            double down = 2 * Math.Max(anchor.Y, content.Height - anchor.Y);

            scale = Math.Clamp(Math.Min(roomWidth / Math.Max(1, across), roomHeight / Math.Max(1, down)), floor, LargestFit);
            pan = new Point(
                inset.Left + (roomWidth / 2) - (anchor.X * scale),
                inset.Top + (roomHeight / 2) - (anchor.Y * scale));
        }
        else
        {
            scale = Math.Clamp(Math.Min(roomWidth / content.Width, roomHeight / content.Height), floor, LargestFit);
            pan = new Point(
                inset.Left + ((roomWidth - (content.Width * scale)) / 2),
                inset.Top + ((roomHeight - (content.Height * scale)) / 2));
        }
        Aim(scale, pan, at: !_fitted);

        _touched = false;
        _fitted = true;
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Aim(double scale, Point pan, bool at)
    {
        _wantScale = scale;
        _wantPan = pan;

        if (!at)
        {
            Begin();
            return;
        }

        _zoom.ScaleX = scale;
        _zoom.ScaleY = scale;
        _pan.X = pan.X;
        _pan.Y = pan.Y;
        _panSpeed = default;
        _scaleSpeed = 0;

        End();
    }

    private void Begin()
    {
        if (_easing)
        {
            return;
        }

        _easing = true;
        _lastFrame = DateTime.UtcNow.Ticks;
        CompositionTarget.Rendering += OnFrame;
    }

    private void End()
    {
        if (!_easing)
        {
            return;
        }

        _easing = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        long now = DateTime.UtcNow.Ticks;
        double seconds = (now - _lastFrame) / (double)TimeSpan.TicksPerSecond;
        _lastFrame = now;

        if (seconds <= 0 || seconds > 0.5)
        {
            return;
        }

        double omega = 2 / Framing;
        double ratio = omega * seconds;
        double decay = 1 / (1 + ratio + (0.48 * ratio * ratio) + (0.235 * ratio * ratio * ratio));
        double scaleGap = _zoom.ScaleX - _wantScale;
        double scaleStep = (_scaleSpeed + (omega * scaleGap)) * seconds;

        _scaleSpeed = (_scaleSpeed - (omega * scaleStep)) * decay;

        double scale = _wantScale + ((scaleGap + scaleStep) * decay);

        Vector panGap = new(_pan.X - _wantPan.X, _pan.Y - _wantPan.Y);
        Vector panStep = (_panSpeed + (panGap * omega)) * seconds;

        _panSpeed = (_panSpeed - (panStep * omega)) * decay;

        Vector panLeft = (panGap + panStep) * decay;

        bool still = Math.Abs(scale - _wantScale) < 0.0015
            && Math.Abs(_scaleSpeed) < 0.01
            && panLeft.LengthSquared < Framed * Framed
            && _panSpeed.LengthSquared < Steady * Steady;

        if (still)
        {
            _zoom.ScaleX = _wantScale;
            _zoom.ScaleY = _wantScale;
            _pan.X = _wantPan.X;
            _pan.Y = _wantPan.Y;
            _panSpeed = default;
            _scaleSpeed = 0;

            End();
        }
        else
        {
            _zoom.ScaleX = scale;
            _zoom.ScaleY = scale;
            _pan.X = _wantPan.X + panLeft.X;
            _pan.Y = _wantPan.Y + panLeft.Y;
        }

        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool Outgrown(Size content)
    {
        Thickness inset = Inset;
        double roomWidth = ActualWidth - inset.Left - inset.Right;
        double roomHeight = ActualHeight - inset.Top - inset.Bottom;

        if (roomWidth <= 0 || roomHeight <= 0)
        {
            return false;
        }

        return (content.Width * _wantScale) > roomWidth + 1
            || (content.Height * _wantScale) > roomHeight + 1;
    }

    /// <summary>
    /// Takes the room the view has just been given.
    /// </summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (!_touched)
        {
            Dispatcher.BeginInvoke(Fit, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// Paints the surface the reader drags on.
    /// </summary>
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        base.OnRender(drawingContext);

        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is { } child)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        return new Size(
            double.IsInfinity(constraint.Width) ? Child?.DesiredSize.Width ?? 0 : constraint.Width,
            double.IsInfinity(constraint.Height) ? Child?.DesiredSize.Height ?? 0 : constraint.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is not { } child)
        {
            return arrangeSize;
        }

        Size content = child.DesiredSize;

        child.RenderTransform = _view;
        child.Arrange(new Rect(new Point(0, 0), content));

        if (_clipped != arrangeSize)
        {
            _clipped = arrangeSize;

            Clip = Radius > 0
                ? new RectangleGeometry(new Rect(arrangeSize), Radius, Radius)
                : null;
        }
        Point anchor = child is IViewAnchor held
            ? held.Anchor
            : new Point(child.DesiredSize.Width / 2, child.DesiredSize.Height / 2);

        if (_anchoring && _fitted && (_anchored != anchor))
        {
            double acrossBy = (_anchored.X - anchor.X) * _zoom.ScaleX;
            double downBy = (_anchored.Y - anchor.Y) * _zoom.ScaleY;

            _pan.X += acrossBy;
            _pan.Y += downBy;
            _wantPan.X += acrossBy;
            _wantPan.Y += downBy;

            ViewChanged?.Invoke(this, EventArgs.Empty);
        }

        _anchored = anchor;
        _anchoring = true;
        if (!_touched && (!_fitted || Outgrown(content)))
        {
            Dispatcher.BeginInvoke(Fit, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        return arrangeSize;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonDown(e);

        if (e.ClickCount == 2)
        {
            FitAll();
            e.Handled = true;
            return;
        }

        // A press that landed on a node belongs to that node.
        if (Ancestry.Find<ButtonBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        _grabbed = e.GetPosition(this);
        _pressed = _grabbed;
        _moved = false;
        _dragging = true;
        Cursor = Cursors.SizeAll;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);

        if (!_dragging)
        {
            return;
        }

        Point at = e.GetPosition(this);

        Aim(
            _zoom.ScaleX,
            new Point(_pan.X + (at.X - _grabbed.X), _pan.Y + (at.Y - _grabbed.Y)),
            at: true);

        _grabbed = at;

        if (Math.Abs(at.X - _pressed.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(at.Y - _pressed.Y) >= SystemParameters.MinimumVerticalDragDistance)
        {
            _moved = true;
            _touched = true;
        }

        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Cursor = Cursors.Arrow;
        ReleaseMouseCapture();

        if (!_moved)
        {
            Cleared?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Moves the view by the keyboard: see the remarks on the class.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);

        if (e.Handled)
        {
            return;
        }

        bool own = ReferenceEquals(e.OriginalSource, this);
        ModifierKeys held = Keyboard.Modifiers;
        bool plain = held == ModifierKeys.None;
        bool control = held == ModifierKeys.Control;
        bool moves = (own && plain) || control;

        switch (e.Key)
        {
            case Key.Left when moves:
                PanBy(Step, 0);
                break;
            case Key.Right when moves:
                PanBy(-Step, 0);
                break;
            case Key.Up when moves:
                PanBy(0, Step);
                break;
            case Key.Down when moves:
                PanBy(0, -Step);
                break;
            case Key.Add or Key.OemPlus when !held.HasFlag(ModifierKeys.Alt):
                ZoomBy(ZoomStep);
                break;
            case Key.Subtract or Key.OemMinus when !held.HasFlag(ModifierKeys.Alt):
                ZoomBy(1 / ZoomStep);
                break;
            case Key.D0 or Key.NumPad0 when moves:
            case Key.Home when own && plain:
                FitAll();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Moves the picture by so many pixels on screen.</summary>
    public void PanBy(double across, double down)
    {
        Aim(_zoom.ScaleX, new Point(_pan.X + across, _pan.Y + down), at: true);
        _touched = true;
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Zooms about the middle of the view.</summary>
    public void ZoomBy(double factor)
    {
        double was = _zoom.ScaleX;
        double now = Math.Clamp(was * factor, Math.Min(Smallest, _wantScale), Largest);

        if (Math.Abs(now - was) < 0.0001)
        {
            return;
        }

        Point at = new(ActualWidth / 2, ActualHeight / 2);

        Aim(
            now,
            new Point(
                at.X - ((at.X - _pan.X) * (now / was)),
                at.Y - ((at.Y - _pan.Y) * (now / was))),
            at: true);

        _touched = true;
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Brings a node the keyboard reached into view, moving the picture the
    /// least it can: nodes outside the clip used to take the focus unseen.
    /// </summary>
    private void OnBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (ReferenceEquals(e.TargetObject, this)
            || Child is not { } child
            || e.TargetObject is not Visual target
            || !child.IsAncestorOf(target)
            || !KeyPress.IsBeingHandled
            || Mouse.LeftButton == MouseButtonState.Pressed
            || ActualWidth <= 0
            || ActualHeight <= 0)
        {
            return;
        }

        e.Handled = true;

        Rect box = !e.TargetRect.IsEmpty
            ? e.TargetRect
            : target is UIElement element ? new Rect(element.RenderSize) : Rect.Empty;

        if (box.IsEmpty)
        {
            return;
        }

        Rect seen;

        try
        {
            seen = target.TransformToAncestor(this).TransformBounds(box);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        Thickness inset = Inset;
        Rect room = new(
            inset.Left,
            inset.Top,
            Math.Max(1, ActualWidth - inset.Left - inset.Right),
            Math.Max(1, ActualHeight - inset.Top - inset.Bottom));

        double across = Shift(seen.Left, seen.Right, room.Left, room.Right);
        double down = Shift(seen.Top, seen.Bottom, room.Top, room.Bottom);

        if (across != 0 || down != 0)
        {
            PanBy(across, down);
        }
    }

    /// <summary>How far to move a span so it sits inside the room, with a margin.</summary>
    private static double Shift(double start, double end, double roomStart, double roomEnd)
    {
        double margin = Math.Min(Breathing, Math.Max(0, ((roomEnd - roomStart) - (end - start)) / 2));

        if (end - start > roomEnd - roomStart - (2 * margin))
        {
            return ((roomStart + roomEnd) / 2) - ((start + end) / 2);
        }

        if (start < roomStart + margin)
        {
            return roomStart + margin - start;
        }

        return end > roomEnd - margin ? roomEnd - margin - end : 0;
    }

    /// <summary>A pane that scrolls: the view, to UI Automation.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new ViewPeer(this);

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseWheel(e);

        double was = _zoom.ScaleX;
        double floor = Math.Min(Smallest, _wantScale);
        double now = Math.Clamp(was * (e.Delta > 0 ? 1.15 : 1 / 1.15), floor, Largest);

        if (Math.Abs(now - was) < 0.0001)
        {
            return;
        }

        // Anchored on the pointer, so the thing under it stays under it.
        Point at = e.GetPosition(this);

        Aim(
            now,
            new Point(
                at.X - ((at.X - _pan.X) * (now / was)),
                at.Y - ((at.Y - _pan.Y) * (now / was))),
            at: true);

        _touched = true;

        e.Handled = true;
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The view to UI Automation: a pane, named by the page (or "Pan and zoom"),
    /// telling its keys in its help text, and scrolling as the keys pan it.
    /// </summary>
    private sealed class ViewPeer(PanZoomHost owner) : FrameworkElementAutomationPeer(owner), IScrollProvider, IInvokeProvider
    {
        private const string Keys =
            "Arrow keys move the view, plus and minus zoom, 0 fits all of it. Tab goes on to what is on it; from there Control with an arrow key moves the view.";

        private PanZoomHost Host => (PanZoomHost)Owner;

        public bool HorizontallyScrollable => Extent(horizontal: true) > 0;

        public bool VerticallyScrollable => Extent(horizontal: false) > 0;

        public double HorizontalViewSize => ViewSize(horizontal: true);

        public double VerticalViewSize => ViewSize(horizontal: false);

        public double HorizontalScrollPercent => Percent(horizontal: true);

        public double VerticalScrollPercent => Percent(horizontal: false);

        public void Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount) =>
            Host.PanBy(-Amount(horizontalAmount, Host.ActualWidth), -Amount(verticalAmount, Host.ActualHeight));

        public void SetScrollPercent(double horizontalPercent, double verticalPercent)
        {
            double across = horizontalPercent < 0 ? 0 : To(horizontalPercent, horizontal: true);
            double down = verticalPercent < 0 ? 0 : To(verticalPercent, horizontal: false);

            Host.PanBy(across, down);
        }

        /// <summary>Fits the whole picture, as a double-click on it does.</summary>
        public void Invoke() => Host.FitAll();

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface is PatternInterface.Scroll or PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

        protected override string GetClassNameCore() => nameof(PanZoomHost);

        protected override string GetNameCore()
        {
            string given = base.GetNameCore();

            return string.IsNullOrEmpty(given) ? "Pan and zoom" : given;
        }

        protected override string GetHelpTextCore()
        {
            string given = base.GetHelpTextCore();

            return string.IsNullOrEmpty(given) ? Keys : string.Concat(given, " ", Keys);
        }

        private static double Amount(ScrollAmount amount, double page) => amount switch
        {
            ScrollAmount.SmallIncrement => Step,
            ScrollAmount.SmallDecrement => -Step,
            ScrollAmount.LargeIncrement => page * 0.8,
            ScrollAmount.LargeDecrement => -page * 0.8,
            _ => 0,
        };

        /// <summary>How much more of the picture there is than the view shows, on screen.</summary>
        private double Extent(bool horizontal)
        {
            if (Host.Child is not { } child)
            {
                return 0;
            }

            double content = (horizontal ? child.DesiredSize.Width : child.DesiredSize.Height) * Host.Zoom;
            double view = horizontal ? Host.ActualWidth : Host.ActualHeight;

            return Math.Max(0, content - view);
        }

        private double ViewSize(bool horizontal)
        {
            if (Host.Child is not { } child)
            {
                return 100;
            }

            double content = (horizontal ? child.DesiredSize.Width : child.DesiredSize.Height) * Host.Zoom;
            double view = horizontal ? Host.ActualWidth : Host.ActualHeight;

            return content <= 0 ? 100 : Math.Clamp(view / content * 100, 0, 100);
        }

        private double Percent(bool horizontal)
        {
            double extent = Extent(horizontal);

            if (extent <= 0)
            {
                return ScrollPatternIdentifiers.NoScroll;
            }

            double offset = -(horizontal ? Host._pan.X : Host._pan.Y);

            return Math.Clamp(offset / extent * 100, 0, 100);
        }

        /// <summary>How far to pan, on screen, for the view to stand at a percentage.</summary>
        private double To(double percent, bool horizontal)
        {
            double extent = Extent(horizontal);
            double now = -(horizontal ? Host._pan.X : Host._pan.Y);

            return extent <= 0 ? 0 : now - (Math.Clamp(percent, 0, 100) / 100 * extent);
        }
    }
}
