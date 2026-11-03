using System.Windows;
using Windsock.App.ViewModels;
using Windsock.App.Views;
using Windsock.Core.Docking;
using Windsock.Core.Settings;

namespace Windsock.App.Services;

/// <summary>Where one meter should be: which monitor, which taskbar, and how.</summary>
/// <param name="Key">What tells this meter from the others, kept while it is the same meter.</param>
/// <param name="Monitor">The monitor it belongs to.</param>
/// <param name="Bar">The taskbar it docks into or floats over, if that monitor has one.</param>
/// <param name="Docked">Whether it is a child of the taskbar rather than a window of its own.</param>
/// <param name="Floated">Whether it was asked to dock and floats because the bar was not recognised.</param>
internal sealed record MeterTarget(
    string Key,
    FoundMonitor Monitor,
    FoundTaskbar? Bar,
    bool Docked,
    bool Floated);

/// <summary>
/// One meter: its window and view model, and what it last did, on one
/// taskbar or one monitor. Every meter shows the same readings; each lays
/// them out for the bar it is on.
/// </summary>
internal sealed class MeterStation : IDisposable
{
    private MeterTarget _target;

    public MeterStation(MeterTarget target, bool dark, TaskbarSettings settings)
    {
        _target = target;
        Label = MeterWords.DisplayName(target.Monitor.Name);
        Model = new TaskbarMeterViewModel(dark);
        Model.Apply(settings);
        Window = new TaskbarMeterWindow(Model);
        Covered = new HeldSignal(TaskbarMeter.FullScreenHold);
    }

    /// <summary>Where this meter belongs.</summary>
    public MeterTarget Target
    {
        get => _target;

        set
        {
            _target = value;
            Label = MeterWords.DisplayName(value.Monitor.Name);
            Frame = null;
        }
    }

    /// <summary>The monitor's name as the page shows it, worked out once.</summary>
    public string Label { get; private set; }

    /// <summary>The bar as last read, reused while nothing about it has changed.</summary>
    public TaskbarFrame? Frame { get; set; }

    /// <summary>What this meter shows.</summary>
    public TaskbarMeterViewModel Model { get; }

    /// <summary>The window showing it.</summary>
    public TaskbarMeterWindow Window { get; }

    /// <summary>The taskbar window it is in or over, or zero for a monitor without one.</summary>
    public nint Bar => Target.Bar?.Handle ?? 0;

    /// <summary>The taskbar this window was made a child of.</summary>
    public nint Host { get; set; }

    /// <summary>Whether the window has been closed or destroyed under it.</summary>
    public bool IsGone { get; set; }

    /// <summary>Where the window was last put, in the coordinates it was put in.</summary>
    public PixelRect Placed { get; set; }

    /// <summary>Whether the window was last left on screen.</summary>
    public bool Shown { get; set; }

    /// <summary>Whether its size has to be worked out again before it is placed.</summary>
    public bool Stale { get; set; } = true;

    /// <summary>How long it is along its bar, in pixels, as last worked out.</summary>
    public int Length { get; set; }

    /// <summary>How thick it is across a bar-less monitor, in pixels.</summary>
    public int Thickness { get; set; }

    /// <summary>The DPI its size was last worked out at.</summary>
    public int Dpi { get; set; }

    /// <summary>How its readings are laid out on its bar.</summary>
    public MeterArrangement Arrangement { get; set; }

    /// <summary>The edge of its bar, as last seen.</summary>
    public TaskbarEdge? Edge { get; set; }

    /// <summary>Whether it covers buttons it could not avoid.</summary>
    public bool Crowded { get; set; }

    /// <summary>The bar's rectangle when its size was last worked out.</summary>
    public PixelRect SizedFor { get; set; }

    /// <summary>The bar and notification area as last read, to notice a change cheaply.</summary>
    public (PixelRect Bar, PixelRect Tray, PixelRect Band, int Dpi) Seen { get; set; }

    /// <summary>
    /// The bar's buttons as UI Automation last found them, in the bar's own
    /// coordinates.
    /// </summary>
    public IReadOnlyList<TaskbarObstacle> Obstacles { get; set; } = [];

    /// <summary>The patrol tick the bar was last asked about, through UI Automation.</summary>
    public long ProbedAt { get; set; } = long.MinValue;

    /// <summary>The classic task band as Explorer laid it out, before it was narrowed, in the bar's own coordinates.</summary>
    public PixelRect? BandFull { get; set; }

    /// <summary>Where the task band was narrowed to, in the bar's own coordinates, while it is.</summary>
    public PixelRect? BandSet { get; set; }

    /// <summary>How often the band had to be narrowed again; spent, it is left alone.</summary>
    public RetryBudget BandBudget { get; } = new(5, TimeSpan.FromSeconds(10));

    /// <summary>Whether the bar changed shape too soon after the last probe to probe it again.</summary>
    public bool ProbeOwed { get; set; }

    /// <summary>Whether something full screen is in front of its monitor, held against flicker.</summary>
    public HeldSignal Covered { get; }

    /// <summary>Whether it is out of the way of a full-screen app right now.</summary>
    public bool FullScreenHidden { get; set; }

    /// <summary>The window's natural size as last measured, in device-independent pixels.</summary>
    public Size Natural { get; set; }

    /// <summary>How many times in a row its size was checked again after measuring.</summary>
    public int Rechecks { get; set; }

    /// <summary>Its own copy of the hover card's view: a visual can sit in one tooltip only.</summary>
    public FrameworkElement? CardView { get; set; }

    /// <summary>The edge the card was last placed for.</summary>
    public TaskbarEdge? CardEdge { get; set; }

    /// <summary>Where it last worked out it goes on its bar.</summary>
    public DockSpot? Spot { get; set; }

    /// <summary>The bar that spot was worked out on.</summary>
    public TaskbarFrame? SpotFrame { get; set; }

    /// <summary>The length that spot was worked out for.</summary>
    public int SpotLength { get; set; }

    /// <summary>What the Taskbar page is told about this meter.</summary>
    public StationNote Note => new(
        Label,
        Edge,
        Arrangement,
        FullScreenHidden,
        Crowded,
        BandSet is not null,
        Target.Floated);

    /// <inheritdoc />
    public void Dispose()
    {
        bool closed = IsGone;

        IsGone = true;
        TaskbarDock.Detach(Window.Handle);

        if (closed)
        {
            return;
        }

        try
        {
            Window.Close();
        }
        catch (InvalidOperationException)
        {
            // Already on its way out: Explorer destroyed the taskbar, and the
            // window with it, between the last patrol and this one.
        }
    }
}
