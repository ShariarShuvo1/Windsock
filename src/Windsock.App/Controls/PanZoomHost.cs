using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>
/// Gives its child as much room as it asks for, and lets the reader move
/// around inside it.
/// </summary>
public sealed class PanZoomHost : Decorator
{
    private const double Smallest = 0.2;
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

    public PanZoomHost()
    {
        ClipToBounds = true;
        Focusable = false;

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

        double scale = Math.Min(roomWidth / content.Width, roomHeight / content.Height);
        scale = Math.Clamp(scale, floor, LargestFit);
        Point pan = new(
            inset.Left + ((roomWidth - (content.Width * scale)) / 2),
            inset.Top + ((roomHeight - (content.Height * scale)) / 2));
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
}
