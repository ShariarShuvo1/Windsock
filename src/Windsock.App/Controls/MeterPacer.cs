using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Windsock.Core.Meter;

namespace Windsock.App.Controls;

/// <summary>
/// A windsock on its pole, fluttering harder the more moves: the meter's
/// playful mark, after the app's name.
/// </summary>
/// <remarks>
/// <para>
/// Cheap by design. Every windsock on screen shares one timer at ten frames a
/// second, which runs only while one of them is loaded and visible and stops
/// the moment none is. A frame is one small frozen geometry. With nothing
/// moving the sock hangs still and the timer does no drawing at all.
/// </para>
/// <para>
/// It honours Windows' "Animation effects" setting
/// (SystemParameters.ClientAreaAnimation): with it off, the sock is drawn
/// once, at the angle the rate puts it at, and never moves.
/// </para>
/// </remarks>
public sealed class MeterPacer : FrameworkElement
{
    /// <summary>Bytes a second moving, which sets the pace.</summary>
    public static readonly DependencyProperty SpeedProperty = DependencyProperty.Register(
        nameof(Speed),
        typeof(double),
        typeof(MeterPacer),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The colour of the pole and the sock's stripes.</summary>
    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(
        nameof(Ink),
        typeof(Brush),
        typeof(MeterPacer),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, (owner, _) => ((MeterPacer)owner).ForgetInk()));

    private static readonly List<MeterPacer> Running = [];
    private static DispatcherTimer? _clock;
    private double _phase;

    // Drawn ten times a second: the pen and the fainter ink are made once for
    // each ink, and the two shapes are the same two objects filled afresh,
    // so a frame allocates nothing that outlives it.
    private Pen? _pole;
    private Brush? _faint;
    private readonly StreamGeometry _bright = new();
    private readonly StreamGeometry _dim = new();

    public MeterPacer()
    {
        Loaded += (_, _) => Follow();
        Unloaded += (_, _) => Leave();
        IsVisibleChanged += (_, _) => Follow();
    }

    public double Speed
    {
        get => (double)GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
    }

    public Brush Ink
    {
        get => (Brush)GetValue(InkProperty);
        set => SetValue(InkProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(14, 12);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;

        if (width < 4 || height < 4)
        {
            return;
        }

        double droop = Pace.Droop(Speed);
        double sway = Math.Sin(_phase * 2 * Math.PI) * (1 - droop) * 0.18;

        // The pole up the left edge, the sock off its top: straight out in a
        // gale, hanging down the pole in a calm.
        double poleX = 1.2;
        double angle = (droop * 1.25) + sway;
        double length = width - poleX - 1;
        double mouth = height * 0.42;
        Point hinge = new(poleX, 1.5);

        if (_pole is null || _faint is null)
        {
            _pole = new Pen(Ink, 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            _pole.Freeze();

            Brush faint = Ink.Clone();
            faint.Opacity = 0.45;
            faint.Freeze();
            _faint = faint;
        }

        drawingContext.DrawLine(_pole, new Point(poleX, 0.8), new Point(poleX, height - 0.5));

        // Three stripes, alternating ink and a fainter ink, narrowing to the
        // tail: the first and last in one shape, the middle in the other.
        using (StreamGeometryContext context = _bright.Open())
        {
            Stripe(context, hinge, angle, length, mouth, 0);
            Stripe(context, hinge, angle, length, mouth, 2);
        }

        using (StreamGeometryContext context = _dim.Open())
        {
            Stripe(context, hinge, angle, length, mouth, 1);
        }

        drawingContext.DrawGeometry(Ink, null, _bright);
        drawingContext.DrawGeometry(_faint, null, _dim);
    }

    private static void Stripe(StreamGeometryContext context, Point hinge, double angle, double length, double mouth, int stripe)
    {
        double from = stripe / 3d;
        double to = (stripe + 1) / 3d;

        context.BeginFigure(Along(hinge, angle, length, mouth, from, top: true), isFilled: true, isClosed: true);
        context.LineTo(Along(hinge, angle, length, mouth, to, top: true), isStroked: false, isSmoothJoin: false);
        context.LineTo(Along(hinge, angle, length, mouth, to, top: false), isStroked: false, isSmoothJoin: false);
        context.LineTo(Along(hinge, angle, length, mouth, from, top: false), isStroked: false, isSmoothJoin: false);
    }

    private void ForgetInk()
    {
        _pole = null;
        _faint = null;
    }

    /// <summary>A point on the sock's edge, a share of the way from its mouth to its tail.</summary>
    private static Point Along(Point hinge, double angle, double length, double mouth, double share, bool top)
    {
        double half = mouth * (1 - (share * 0.55)) / 2;
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);
        double x = hinge.X + (cos * length * share);
        double y = hinge.Y + half + (sin * length * share);
        double offset = top ? -half : half;

        // The mouth's width runs across the sock, square to its length.
        return new Point(x - (sin * offset), y + (cos * offset));
    }

    private void Follow()
    {
        if (IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation)
        {
            if (!Running.Contains(this))
            {
                Running.Add(this);
            }

            if (_clock is null)
            {
                _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1 / Pace.Fastest) };
                _clock.Tick += OnTick;
            }

            _clock.Start();
            return;
        }

        Leave();
    }

    private void Leave()
    {
        Running.Remove(this);

        if (Running.Count == 0)
        {
            _clock?.Stop();
        }
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        foreach (MeterPacer pacer in Running)
        {
            double flaps = Pace.FlutterPerSecond(pacer.Speed);

            if (flaps <= 0)
            {
                continue;
            }

            pacer._phase = (pacer._phase + (flaps / Pace.Fastest)) % 1;
            pacer.InvalidateVisual();
        }
    }
}
