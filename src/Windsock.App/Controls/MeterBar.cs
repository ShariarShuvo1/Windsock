using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>
/// A bar showing how much of something is used.
/// </summary>
/// <remarks>
/// Decorative to a screen reader, deliberately: every bar in the app stands
/// beside the figure it draws ("28%", "1.2 GB of 8 GB"), and the row or item
/// it sits in is named by that figure. A bar that also announced itself would
/// say each value twice. Its peer is kept out of the control and content views
/// (Narrator walks those), so it is there for a tool that asks and silent
/// otherwise.
/// </remarks>
public sealed class MeterBar : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction),
        typeof(double),
        typeof(MeterBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(Brush),
        typeof(MeterBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track),
        typeof(Brush),
        typeof(MeterBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How much of the bar is filled, from nothing to one.</summary>
    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    /// <summary>What the filled part is painted with.</summary>
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>What the rest of the bar is painted with.</summary>
    public Brush? Track
    {
        get => (Brush?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    /// <summary>A peer that says the bar is there, out of the views a screen reader reads.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }
        double radius = Math.Min(height / 2, 4);

        if (Track is { } track)
        {
            drawingContext.DrawRoundedRectangle(track, null, new Rect(0, 0, width, height), radius, radius);
        }

        double filled = Math.Clamp(Fraction, 0, 1) * width;
        if (Fill is not { } fill || filled < 1)
        {
            return;
        }

        drawingContext.DrawRoundedRectangle(
            fill,
            null,
            new Rect(0, 0, Math.Max(filled, radius * 2), height),
            radius,
            radius);
    }

    private sealed class DecorativePeer(MeterBar owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;

        protected override string GetClassNameCore() => nameof(MeterBar);

        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;
    }
}
