using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windsock.App.ViewModels;
using Windsock.App.Views;
using Windsock.Core.Connectivity;
using Windsock.Core.Docking;
using Windsock.Core.Formatting;
using Windsock.Core.Meter;
using Windsock.Core.Hardware;
using Windsock.Core.Networking;
using Windsock.Core.Settings;

namespace Windsock.App.Services;

/// <summary>A monitor, as the Taskbar page offers it for the meter.</summary>
/// <param name="Name">The name Windows gives it, which is what the settings keep.</param>
/// <param name="Label">How the page names it.</param>
/// <param name="HasTaskbar">Whether it shows a taskbar to dock into.</param>
public sealed record DisplayChoice(string Name, string Label, bool HasTaskbar);

/// <summary>
/// Owns the meters that sit in the Windows taskbars: which taskbars have one,
/// where each sits, and what they all say.
/// </summary>
/// <remarks>
/// One window and one view model per taskbar (or, floating, per monitor),
/// sharing one set of readings. Where each goes is decided by the pure
/// geometry in <c>Windsock.Core.Docking</c>; this class only reads the
/// windows Explorer makes and moves its own. It hears about changes as they
/// happen (<see cref="TaskbarWatch"/>) and patrols for what nothing announces,
/// cheaply: a steady tick is a few rectangle reads per meter and nothing else.
/// </remarks>
public sealed partial class TaskbarMeter : IDisposable
{
    /// <summary>How long a full-screen app must stay in front before the meter steps aside, and gone before it returns.</summary>
    internal static readonly TimeSpan FullScreenHold = TimeSpan.FromMilliseconds(400);

    private static readonly TimeSpan Patrol = TimeSpan.FromMilliseconds(300);

    /// <summary>Every fifth tick, about 1.5 s: count the taskbars.</summary>
    private const int CensusTicks = 5;

    /// <summary>Every tenth tick, about 3 s: ask about remote sessions and auto-hide.</summary>
    private const int ShellTicks = 10;

    /// <summary>
    /// Every hundredth tick, about 30 s: look at a Windows 11 bar's buttons
    /// again, for apps that came or went without moving anything a window
    /// marks.
    /// </summary>
    private const long ProbeTicks = 100;

    private readonly ThroughputMonitor _network;
    private readonly SystemMonitor _machine;
    private readonly ConnectionMonitor _connection;
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<TaskbarMeter> _logger;
    private readonly DispatcherTimer _patrol;
    private readonly TaskbarWatch _watch = new();
    private readonly TaskbarProbe _probe;
    private readonly MeterSources _sources;
    private readonly MeterCardViewModel _card;
    private readonly DispatcherTimer _singleClick;
    private MeterStation? _cardOpenOn;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<MeterStation> _stations = [];
    private List<FoundMonitor> _monitors = [];
    private PixelRect[] _bounds = [];
    private List<FoundTaskbar> _bars = [];
    private IReadOnlyList<DisplayChoice> _displays = [];
    private (nint Main, int Count, bool Modern) _census;
    private IDisposable? _machineWatch;
    private SystemSnapshot _snapshot = SystemSnapshot.Empty;
    private MeterPlacement _placement;
    private MeterSituation _situation = MeterSituation.Off;
    private StationNote[] _notes = [];
    private string? _missing;
    private string? _barless;
    private string? _replacement;
    private nint _frontWindow;
    private nint _frontMonitor;
    private PixelRect _frontRect;
    private bool _frontCovers;
    private long _tick;
    private bool _dark;
    private bool _autoHide;
    private bool _remote;
    private bool _started;
    private bool _surveyed;
    private bool _sizing;
    private bool _listening;
    private bool _pending;
    private bool _surveyQueued;
    private bool _stationQueued;
    private bool _frontQueued;
    private bool _disposed;
    private double _download;
    private double _upload;
    private ConnectionState _state;

    public TaskbarMeter(
        ThroughputMonitor network,
        SystemMonitor machine,
        ConnectionMonitor connection,
        WindsockSettings settings,
        ISettingsStore store,
        MeterCardViewModel card,
        IServiceProvider services,
        Dispatcher dispatcher,
        ILogger<TaskbarMeter> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _card = card;
        _sources = new MeterSources(connection, settings, services, dispatcher, logger);
        _sources.Changed += (_, _) => Queue();

        _network = network;
        _machine = machine;
        _connection = connection;
        _settings = settings;

        // A single click waits to see whether a second follows, but only
        // when a double click does something: otherwise it acts at once.
        _singleClick = new DispatcherTimer(DispatcherPriority.Input, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(PInvoke.GetDoubleClickTime()),
        };
        _singleClick.Tick += (_, _) =>
        {
            _singleClick.Stop();
            Act(_settings.Taskbar.Click);
        };
        _store = store;
        _dispatcher = dispatcher;
        _logger = logger;

        _patrol = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Patrol };
        _patrol.Tick += (_, _) => Guard(OnPatrol);

        _probe = new TaskbarProbe(dispatcher, logger);
        _probe.Answered += OnProbed;

        _watch.Changed += (_, _) => QueueSurvey();
        _watch.ThemeChanged += (_, _) => _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Apply));
        _watch.Moved += OnBarMoved;
        _watch.FrontChanged += (_, _) => QueueFront();
    }

    /// <summary>
    /// Raised when a click, double click or middle click on the meter asks
    /// for something other than nothing: what the person chose it to do.
    /// </summary>
    public event EventHandler<MeterClick>? ActionRequested;

    /// <summary>Raised when the meter is right-clicked, with the window to open the menu over.</summary>
    public event EventHandler<FrameworkElement>? MenuRequested;

    /// <summary>Raised after the settings were applied, whatever they changed.</summary>
    public event EventHandler? Applied;

    /// <summary>Raised when <see cref="Situation"/> changes, or a floating meter is moved.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Raised when monitors come, go or change.</summary>
    public event EventHandler? DisplaysChanged;

    /// <summary>What the meters are doing, and why.</summary>
    public MeterSituation Situation => _situation;

    /// <summary>Every monitor, the main one first, for choosing where the meter goes.</summary>
    public IReadOnlyList<DisplayChoice> Displays
    {
        get
        {
            if (!_surveyed)
            {
                try
                {
                    List<FoundMonitor> monitors = Taskbars.Monitors();
                    _displays = Choices(monitors, Taskbars.Find(monitors));
                }
#pragma warning disable CA1031 // Listing monitors for a picker must not take the page down.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogBuildFailed(ex);
                }
            }

            return _displays;
        }
    }

    /// <summary>
    /// Makes what is on screen match what the settings say.
    /// </summary>
    public void Apply()
    {
        if (_disposed)
        {
            return;
        }

        TaskbarSettings settings = _settings.Taskbar;

        if (!settings.Enabled)
        {
            Teardown();
            Report();
            Applied?.Invoke(this, EventArgs.Empty);
            return;
        }

        Guard(() =>
        {
            bool dark = TaskbarTheme.IsDark();

            if (_stations.Count > 0 && (_dark != dark || _placement != settings.Placement))
            {
                RemoveAll();
            }

            _dark = dark;
            _placement = settings.Placement;

            Start();
            Listen(settings);
            _sources.Want(Shown(settings), Feeds(settings));

            foreach (MeterStation station in _stations)
            {
                station.Model.Apply(settings);
                station.Window.CanDrag = !station.Target.Docked && !settings.Locked;
                station.Stale = true;
            }

            if (!_surveyed)
            {
                Refresh();
            }

            Reconcile();
        });

        Applied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Asks for what a click on the meter could ask for, from somewhere that is
    /// not the meter: the Taskbar page's own button for the compact window.
    /// </summary>
    public void Request(MeterClick action) => Act(action);

    /// <summary>The words "↓ 12.3 KB/s  ↑ 456 B/s", for copying.</summary>
    public string SpeedsText()
    {
        TaskbarSettings settings = _settings.Taskbar;

        return string.Concat(
            "↓ ",
            RateFormatter.Format(_network.Latest.DownloadBytesPerSecond, settings.Family, settings.Scale),
            "  ↑ ",
            RateFormatter.Format(_network.Latest.UploadBytesPerSecond, settings.Family, settings.Scale));
    }

    /// <summary>Every reading a meter shows, rotations included.</summary>
    private static List<MeterField> Shown(TaskbarSettings settings)
    {
        List<MeterField> fields = [];

        for (int each = 0; each < settings.Rows * settings.Columns && each < settings.Slots.Count; each++)
        {
            foreach (MeterField field in settings.Slots[each].Fields())
            {
                if (field != MeterField.Empty && !fields.Contains(field))
                {
                    fields.Add(field);
                }
            }
        }

        return fields;
    }

    /// <summary>Every JSON place's feed.</summary>
    private static List<MeterFeed> Feeds(TaskbarSettings settings)
    {
        List<MeterFeed> feeds = [];

        for (int each = 0; each < settings.Rows * settings.Columns && each < settings.Slots.Count; each++)
        {
            if (settings.Slots[each] is { Field: MeterField.Feed, Feed: { } feed })
            {
                feeds.Add(feed);
            }
        }

        return feeds;
    }

    /// <summary>Takes the meters off the taskbars and stops everything behind them.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Teardown();
        _singleClick.Stop();
        _sources.Dispose();
        _probe.Answered -= OnProbed;
        _probe.Dispose();
        _watch.Dispose();
    }

    private void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _watch.Start();
        _patrol.Start();
    }

    private void Teardown()
    {
        RemoveAll();
        Listen(null);
        _sources.Stop();
        _card.Close();
        _cardOpenOn = null;
        _patrol.Stop();
        _watch.Stop();
        _started = false;
        _surveyed = false;
        _snapshot = SystemSnapshot.Empty;
    }

    /// <summary>
    /// Runs part of the meter, and if it fails, takes the meter down rather
    /// than the app: the house rule for something that reaches into another
    /// process's windows.
    /// </summary>
    private void Guard(Action work)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            work();
        }
#pragma warning disable CA1031 // There is no useful narrower set: this guards a
        // whole subsystem, and every outcome is the same - no meter, app fine.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBuildFailed(ex);
            Teardown();
            Report(failed: true);
        }
    }

    // ---------------------------------------------------------------- finding

    /// <summary>Reads the monitors and taskbars again, and what the shell says about them.</summary>
    private void Refresh()
    {
        _monitors = Taskbars.Monitors();
        _bounds = [.. _monitors.Select(monitor => monitor.Bounds)];
        _bars = Taskbars.Find(_monitors);
        _census = Taskbars.Census();
        _autoHide = Taskbars.AutoHide();
        _remote = ShellState.InRemoteSession();
        _replacement = _bars.Exists(bar => bar.Kind == TaskbarKind.Classic) ? Taskbars.Replacement() : null;
        _watch.Follow(_bars.Count > 0 ? Taskbars.ThreadOf(_bars[0].Handle) : 0);
        _surveyed = true;

        IReadOnlyList<DisplayChoice> displays = Choices(_monitors, _bars);

        if (!displays.SequenceEqual(_displays))
        {
            _displays = displays;
            DisplaysChanged?.Invoke(this, EventArgs.Empty);
        }

        foreach (MeterStation station in _stations)
        {
            station.Frame = null;
        }
    }

    private static List<DisplayChoice> Choices(List<FoundMonitor> monitors, List<FoundTaskbar> bars) =>
    [
        .. monitors.Select(monitor => new DisplayChoice(
            monitor.Name,
            MeterWords.DisplayLabel(monitor.Name, monitor.Bounds.Width, monitor.Bounds.Height, monitor.Primary),
            bars.Exists(bar => bar.Monitor.Handle == monitor.Handle))),
    ];

    /// <summary>
    /// Makes the set of meters match the settings: one for each taskbar or
    /// monitor asked for, built, kept or taken away.
    /// </summary>
    private void Reconcile()
    {
        TaskbarSettings settings = _settings.Taskbar;
        List<MeterTarget> targets = Decide(settings);

        for (int each = _stations.Count - 1; each >= 0; each--)
        {
            MeterStation station = _stations[each];

            if (station.IsGone || !targets.Exists(target => target.Key == station.Target.Key))
            {
                Remove(station);
            }
        }

        foreach (MeterTarget target in targets)
        {
            MeterStation? known = _stations.Find(station => station.Target.Key == target.Key);

            if (known is not null)
            {
                known.Target = target;
                continue;
            }

            Build(target, settings);
        }

        string? preface = Preface(settings);

        foreach (MeterStation station in _stations)
        {
            station.Model.Preface = preface;
            Station(station, force: true);
        }

        Report();
    }

    /// <summary>Where meters should be, by the settings and what was found.</summary>
    private List<MeterTarget> Decide(TaskbarSettings settings)
    {
        List<MeterTarget> targets = [];

        _missing = null;
        _barless = null;

        if ((_remote && settings.Remote == MeterRemote.Hide) || _monitors.Count == 0)
        {
            return targets;
        }

        FoundMonitor main = _monitors.Find(monitor => monitor.Primary) is { Name.Length: > 0 } primary
            ? primary
            : _monitors[0];

        List<FoundMonitor> chosen = settings.Monitors switch
        {
            MeterMonitors.Every => _monitors,
            MeterMonitors.Chosen => Chosen(settings.ChosenMonitor, main),
            _ => [main],
        };

        bool docked = settings.Placement == MeterPlacement.Docked;
        FoundTaskbar? mainBar = _bars.Find(bar => bar.Primary);

        foreach (FoundMonitor monitor in chosen)
        {
            FoundTaskbar? bar = _bars.Find(each => each.Monitor.Handle == monitor.Handle);

            if (!docked)
            {
                Add(targets, new MeterTarget("float:" + monitor.Name, monitor, bar, Docked: false, Floated: false));
                continue;
            }

            if (bar is null)
            {
                // Every taskbar means every taskbar there is; a monitor picked
                // on its own, with none, gets the main one's meter instead.
                if (settings.Monitors == MeterMonitors.Chosen && mainBar is not null)
                {
                    _barless = MeterWords.DisplayName(monitor.Name);
                    Add(targets, Docking(mainBar));
                }

                continue;
            }

            Add(targets, Docking(bar));
        }

        return targets;
    }

    private List<FoundMonitor> Chosen(string? name, FoundMonitor main)
    {
        FoundMonitor? found = _monitors.Find(monitor =>
            string.Equals(monitor.Name, name, StringComparison.OrdinalIgnoreCase)) is { Name.Length: > 0 } match
            ? match
            : null;

        if (found is { } monitor)
        {
            return [monitor];
        }

        _missing = MeterWords.DisplayName(name);
        return [main];
    }

    /// <summary>
    /// A docked meter on a bar it recognises; a floating one over a bar whose
    /// shape it does not, which still beats no meter at all.
    /// </summary>
    private static MeterTarget Docking(FoundTaskbar bar)
    {
        bool known = Taskbars.Rect(bar.Handle) is { } rect && TaskbarGeometry.IsRecognised(rect, bar.Monitor.Bounds);

        return known
            ? new MeterTarget("bar:" + bar.Handle, bar.Monitor, bar, Docked: true, Floated: false)
            : new MeterTarget("float:" + bar.Monitor.Name, bar.Monitor, bar, Docked: false, Floated: true);
    }

    private static void Add(List<MeterTarget> targets, MeterTarget target)
    {
        if (!targets.Exists(each => each.Key == target.Key))
        {
            targets.Add(target);
        }
    }

    private void Build(MeterTarget target, TaskbarSettings settings)
    {
        MeterStation station = new(target, _dark, settings);

        station.Model.Resized += OnModelResized;
        station.Model.Update(Reading());
        station.Window.Pressed += OnPressed;
        station.Window.MenuRequested += OnMenu;
        station.Window.CardOpening += OnCardOpening;
        station.Window.CardClosed += OnCardClosed;
        station.Window.Dropped += OnDropped;
        station.Window.ScaleChanged += OnScaleChanged;
        station.Window.Closed += OnWindowClosed;
        station.Window.CanDrag = !target.Docked && !settings.Locked;
        station.Window.Show();

        nint handle = station.Window.Handle;

        if (target.Docked && target.Bar is { } bar)
        {
            if (!TaskbarDock.Attach(handle, bar.Handle))
            {
                LogDockFailed();
                Forget(station);
                return;
            }

            station.Host = bar.Handle;
            TaskbarDock.SyncDpi(handle);
            LogDocked(station.Label, bar.Kind, bar.Primary);
        }

        TaskbarDock.Aside(handle, topmost: !target.Docked);
        _stations.Add(station);

        if (target.Bar is { Kind: TaskbarKind.Modern } modern)
        {
            station.ProbedAt = _tick;
            _probe.Ask(modern.Handle);
        }
    }

    private void Remove(MeterStation station)
    {
        _stations.Remove(station);
        RestoreBand(station);
        Forget(station);
    }

    private void RemoveAll()
    {
        for (int each = _stations.Count - 1; each >= 0; each--)
        {
            Remove(_stations[each]);
        }

        _notes = [];
    }

    private void Forget(MeterStation station)
    {
        station.Model.Resized -= OnModelResized;
        station.Window.Pressed -= OnPressed;
        station.Window.MenuRequested -= OnMenu;
        station.Window.CardOpening -= OnCardOpening;
        station.Window.CardClosed -= OnCardClosed;
        station.Window.Dropped -= OnDropped;

        if (ReferenceEquals(_cardOpenOn, station))
        {
            _card.Close();
            _cardOpenOn = null;
        }

        station.Window.ScaleChanged -= OnScaleChanged;
        station.Window.Closed -= OnWindowClosed;
        station.Dispose();
    }

    // -------------------------------------------------------------- placing

    /// <summary>Puts one meter where it belongs now, if it has moved.</summary>
    private void Station(MeterStation station, bool force = false)
    {
        if (station.IsGone)
        {
            QueueSurvey();
            return;
        }

        if (station.Window.IsDragging)
        {
            return;
        }

        TaskbarSettings settings = _settings.Taskbar;
        MeterTarget target = station.Target;
        nint handle = station.Window.Handle;
        TaskbarFrame? frame = null;

        if (target.Bar is { } bar)
        {
            frame = Frame(station, bar, out bool changed);

            if (frame is null || (target.Docked && !TaskbarDock.Alive(station.Host)))
            {
                QueueSurvey();
                return;
            }

            force |= changed;
        }

        int dpi = frame?.Dpi ?? target.Monitor.Dpi;
        TaskbarEdge? edge = frame?.Edge;

        if (station.CardView is null || station.CardEdge != edge)
        {
            station.CardView ??= new MeterCardView { DataContext = _card };
            station.CardEdge = edge;
            station.Window.Card(station.CardView, edge);
        }

        if (dpi != station.Dpi)
        {
            station.Dpi = dpi;
            station.Stale = true;
        }

        if (station.Stale)
        {
            Size(station, frame, settings, dpi);
            force = true;
        }

        PixelRect rect;
        bool following = true;

        if (frame is not null)
        {
            // Worked out again only when the bar or the meter changed: the
            // obstacles are walked through an interface, which allocates.
            bool placed = force || station.Spot is null || !ReferenceEquals(station.SpotFrame, frame) || station.SpotLength != station.Length;

            if (placed)
            {
                station.Spot = DockGeometry.Place(frame, station.Length);
                station.SpotFrame = frame;
                station.SpotLength = station.Length;
            }

            DockSpot spot = station.Spot!.Value;

            station.Edge = frame.Edge;

            if (target.Docked)
            {
                rect = spot.On(frame.Bar, target.Bar?.Mirrored == true);

                if (placed)
                {
                    MakeRoom(station, frame, spot.Screen);
                }
            }
            else
            {
                (rect, following) = Floating(station, settings, spot.Screen);
            }

            station.Crowded = spot.Crowded || station.BandBudget.IsSpent;
        }
        else
        {
            station.Edge = null;
            station.Crowded = false;

            PixelRect corner = DockGeometry.Corner(target.Monitor.Work, station.Length, station.Thickness, dpi);

            (rect, following) = Floating(station, settings, corner);
        }

        bool visible = !station.FullScreenHidden
            && (target.Docked || !following || FloatingPlacement.IsShown(rect, target.Monitor.Bounds));

        if (!visible)
        {
            if (station.Shown)
            {
                TaskbarDock.Hide(handle);
                station.Shown = false;
            }

            return;
        }

        bool buried = target.Docked && !TaskbarDock.IsInFront(handle, station.Host);

        if (force || buried || !station.Shown || rect != station.Placed)
        {
            TaskbarDock.Place(handle, rect, floating: !target.Docked);
            station.Placed = rect;
            station.Shown = true;
        }
    }

    /// <summary>
    /// Where a floating meter goes: the spot it was dragged to on its
    /// monitor, while that spot is still on a monitor; its own spot over the
    /// taskbar otherwise, which then follows an auto-hiding bar.
    /// </summary>
    private (PixelRect Rect, bool Following) Floating(MeterStation station, TaskbarSettings settings, PixelRect home)
    {
        if (settings.SpotOn(station.Target.Monitor.Name) is { } spot
            && FloatingPlacement.Restore(spot, home.Width, home.Height, _bounds) is { } kept)
        {
            return (kept, false);
        }

        return (home, true);
    }

    /// <summary>
    /// The bar as it is now, built afresh only when something about it moved:
    /// most ticks read three rectangles and a DPI and allocate nothing.
    /// </summary>
    private TaskbarFrame? Frame(MeterStation station, FoundTaskbar bar, out bool changed)
    {
        changed = false;

        if (Taskbars.Rect(bar.Handle) is not { } rect)
        {
            return null;
        }

        PixelRect tray = bar.Tray != 0 ? Taskbars.Rect(bar.Tray) ?? default : default;
        PixelRect band = bar.Band != 0 ? Taskbars.Rect(bar.Band) ?? default : default;
        int dpi = Taskbars.Dpi(bar.Handle);
        (PixelRect Bar, PixelRect Tray, PixelRect Band, int Dpi) seen = (rect, tray, band, dpi);

        if (seen == station.Seen && station.Frame is { } known)
        {
            return known;
        }

        (PixelRect Bar, PixelRect Tray, PixelRect Band, int Dpi) before = station.Seen;

        changed = true;
        station.Seen = seen;

        // A Windows 11 bar that changed shape, or whose notification area
        // moved within it, may have moved its buttons too.
        bool reshaped = before.Bar.Width != rect.Width || before.Bar.Height != rect.Height
            || before.Tray.RelativeTo(before.Bar) != tray.RelativeTo(rect)
            || before.Dpi != dpi;

        if (bar.Kind == TaskbarKind.Modern && reshaped)
        {
            // Asked too recently to ask again at once; the patrol sends it
            // once the gap has passed, so a second change soon after the
            // first is not left with the first one's buttons.
            if (_tick - station.ProbedAt > CensusTicks)
            {
                station.ProbedAt = _tick;
                station.ProbeOwed = false;
                _probe.Ask(bar.Handle);
            }
            else
            {
                station.ProbeOwed = true;
            }
        }

        TaskbarFrame frame = new()
        {
            Bar = rect,
            Monitor = bar.Monitor.Bounds,
            WorkArea = bar.Monitor.Work,
            Dpi = dpi,
            IsPrimary = bar.Primary,
            Kind = bar.Kind,
            AutoHide = _autoHide,
            Tray = tray.IsEmpty ? null : tray,
            TaskBand = band.IsEmpty ? null : band,
            Obstacles = [.. station.Obstacles.Select(each => each with { Rect = each.Rect.Offset(rect.Left, rect.Top) })],
        };

        station.Frame = frame;
        return frame;
    }

    /// <summary>
    /// Works out how the readings are laid out on this bar and how long the
    /// meter is: stacked down a side, on one line on a thin bar, shrunk to fit
    /// where even that is too wide.
    /// </summary>
    private void Size(MeterStation station, TaskbarFrame? frame, TaskbarSettings settings, int dpi)
    {
        int across = frame is null ? MeterSizing.Bare(dpi) : DockGeometry.Across(frame);

        _sizing = true;

        try
        {
            TaskbarMeterWindow window = station.Window;

            window.Rescale(window.DrawnDpi > 0 ? dpi / window.DrawnDpi : 1);

            MeterFit fit = MeterSizing.Fit(window, station.Model, settings, frame?.Edge, across, dpi);

            station.Arrangement = fit.Arrangement;
            station.Natural = fit.Natural;
            station.Thickness = fit.Across;
            station.Length = fit.Length;
            station.Stale = false;
        }
        finally
        {
            _sizing = false;
        }

        Recheck(station);
    }

    /// <summary>
    /// Measures the meter again once the dispatcher has caught up, and sizes
    /// it again if that came out different.
    /// </summary>
    /// <remarks>
    /// Measured straight after new places appear, the readings come out short
    /// by each mark's width: the picture's binding lands a dispatcher turn
    /// later (seen offscreen: 72.3 wide at first, 83.3 once the dispatcher had
    /// run). Left alone the meter would stay too narrow, its figures under
    /// their units, until something else made it measure again. Loaded runs
    /// after data binding and rendering, so by then everything has landed.
    /// </remarks>
    private void Recheck(MeterStation station)
    {
        if (station.Rechecks >= 3)
        {
            station.Rechecks = 0;
            return;
        }

        station.Rechecks++;
        _dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Guard(() =>
        {
            if (station.IsGone || !_stations.Contains(station) || station.Stale)
            {
                return;
            }

            Size now = station.Window.Natural();

            if (Math.Abs(now.Width - station.Natural.Width) < 0.5 && Math.Abs(now.Height - station.Natural.Height) < 0.5)
            {
                station.Rechecks = 0;
                return;
            }

            station.Stale = true;
            Station(station, force: true);
        })));
    }

    /// <summary>
    /// On a classic taskbar, narrows the task band so its buttons lay
    /// themselves out clear of the meter, the way TrafficMonitor does.
    /// </summary>
    /// <remarks>
    /// Explorer lays the band out again whenever it likes - a notification
    /// icon arriving is enough - and then it reaches the notification area
    /// again. That is seen as the band no longer being where it was put, and
    /// it is narrowed afresh from its new extent. When the meter goes, the
    /// band is put back; if Windsock is killed first, Explorer's next layout
    /// puts it back by itself.
    /// <para>
    /// Both rectangles are kept in the bar's own coordinates, so an
    /// auto-hiding bar sliding does not read as Explorer moving the band. If
    /// Explorer puts the band back more than five times in ten seconds, it is
    /// answering every narrowing, and the two would fight for good: the band is
    /// put back, left alone, and the bar is reported as crowded.
    /// </para>
    /// </remarks>
    private void MakeRoom(MeterStation station, TaskbarFrame frame, PixelRect meter)
    {
        nint band = station.Target.Bar?.Band ?? 0;

        if (frame.Kind != TaskbarKind.Classic || band == 0 || frame.TaskBand is not { } current || station.BandBudget.IsSpent)
        {
            return;
        }

        PixelRect here = current.RelativeTo(frame.Bar);

        if (station.BandSet is { } set && set != here)
        {
            if (!station.BandBudget.Try(_clock.Elapsed))
            {
                RestoreBand(station, frame.Bar);
                LogBandGivenUp(station.Label);
                return;
            }

            station.BandFull = here;
            station.BandSet = null;
        }

        station.BandFull ??= here;

        PixelRect full = station.BandFull.Value.Offset(frame.Bar.Left, frame.Bar.Top);

        if (DockGeometry.Narrowed(frame, full, meter) is { } narrowed)
        {
            PixelRect wanted = narrowed.RelativeTo(frame.Bar);

            if (wanted == here || TaskbarDock.MoveBand(band, narrowed))
            {
                station.BandSet = wanted;
            }

            return;
        }

        RestoreBand(station, frame.Bar);
    }

    /// <summary>
    /// Puts a narrowed task band back as Explorer had it, against wherever the
    /// bar is now, unless Explorer has already laid it out again.
    /// </summary>
    private static void RestoreBand(MeterStation station, PixelRect? bar = null)
    {
        nint band = station.Target.Bar?.Band ?? 0;

        if (station.BandSet is { } set
            && station.BandFull is { } full
            && (bar ?? Taskbars.Rect(station.Bar)) is { } where
            && Taskbars.Rect(band) is { } now
            && now.RelativeTo(where) == set)
        {
            _ = TaskbarDock.MoveBand(band, full.Offset(where.Left, where.Top));
        }

        station.BandSet = null;
        station.BandFull = null;
    }

    // ------------------------------------------------------------- patrolling

    private void OnPatrol()
    {
        _tick++;

        if (_tick % CensusTicks == 0 && Taskbars.Census() != _census)
        {
            Refresh();
            Reconcile();
            return;
        }

        if (_tick % ShellTicks == 0
            && (ShellState.InRemoteSession() != _remote || Taskbars.AutoHide() != _autoHide))
        {
            Refresh();
            Reconcile();
            return;
        }

        CheckFront();

        foreach (MeterStation station in _stations)
        {
            Station(station);

            if (station.Target.Bar is { Kind: TaskbarKind.Modern } bar
                && (_tick - station.ProbedAt >= ProbeTicks || (station.ProbeOwed && _tick - station.ProbedAt > CensusTicks)))
            {
                station.ProbedAt = _tick;
                station.ProbeOwed = false;
                _probe.Ask(bar.Handle);
            }
        }

        Report();
    }

    /// <summary>
    /// Whether something full screen is in front of each meter's monitor,
    /// held for a moment either way so an Alt+Tab does not blink the meter.
    /// </summary>
    private void CheckFront()
    {
        TimeSpan now = _clock.Elapsed;
        bool wanted = _settings.Taskbar.FullScreen == MeterFullScreen.Hide;
        (nint Window, nint Monitor)? front = wanted ? ShellState.Front() : null;

        if (front is { } seen)
        {
            Judge(seen.Window, seen.Monitor);
        }

        foreach (MeterStation station in _stations)
        {
            bool covered = front is { } here && here.Monitor == station.Target.Monitor.Handle && _frontCovers;

            if (station.Covered.Update(covered, now))
            {
                station.FullScreenHidden = station.Covered.Value;
                station.Frame = null;
            }
        }
    }

    /// <summary>
    /// Decides once whether the window in front is full screen, and again
    /// only when it or its rectangle changes.
    /// </summary>
    private void Judge(nint window, nint monitor)
    {
        PixelRect rect = Taskbars.Rect(window) ?? default;

        if (window == _frontWindow && monitor == _frontMonitor && rect == _frontRect)
        {
            return;
        }

        _frontWindow = window;
        _frontMonitor = monitor;
        _frontRect = rect;
        _frontCovers = false;

        FoundMonitor? home = _monitors.Find(each => each.Handle == monitor) is { Name.Length: > 0 } found ? found : null;

        if (home is not { } screen || rect.Intersect(screen.Bounds).Area * 10 < screen.Bounds.Area * 9)
        {
            return;
        }

        // Nearly or wholly covering its monitor: worth the closer look, and
        // worth asking the shell when the window falls a little short.
        bool busy = !rect.Contains(screen.Bounds) && ShellState.ShellBusy();

        _frontCovers = ShellState.Describe(window) is { } described
            && FullScreenCheck.Covers(described, screen.Bounds, busy);
    }

    // ---------------------------------------------------------------- events

    private void OnProbed(object? sender, ProbeAnswer answer) => Guard(() =>
    {
        foreach (MeterStation station in _stations)
        {
            if (station.Bar == answer.Bar && !station.Obstacles.SequenceEqual(answer.Obstacles))
            {
                station.Obstacles = answer.Obstacles;
                station.Frame = null;
                Station(station, force: true);
            }
        }

        Report();
    });

    private void OnBarMoved(object? sender, nint window)
    {
        foreach (MeterStation station in _stations)
        {
            if (station.Bar == window)
            {
                QueueStations();
                return;
            }
        }
    }

    private void OnModelResized(object? sender, EventArgs e)
    {
        if (_sizing)
        {
            return;
        }

        foreach (MeterStation station in _stations)
        {
            if (ReferenceEquals(station.Model, sender))
            {
                station.Stale = true;
            }
        }

        QueueStations();
    }

    private void OnScaleChanged(object? sender, EventArgs e)
    {
        foreach (MeterStation station in _stations)
        {
            if (ReferenceEquals(station.Window, sender))
            {
                station.Stale = true;

                // A meter with no bar takes its DPI from the monitor's, read at
                // the last survey; a new scale means reading it again.
                if (station.Target.Bar is null)
                {
                    QueueSurvey();
                }
            }
        }

        QueueStations();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        foreach (MeterStation station in _stations)
        {
            if (ReferenceEquals(station.Window, sender))
            {
                station.IsGone = true;
            }
        }

        QueueSurvey();
    }

    /// <summary>
    /// A press on the meter: the middle button's choice at once; the left
    /// button's single click at once, unless a double click does something,
    /// in which case the single click waits out the double-click time first.
    /// </summary>
    private void OnPressed(object? sender, MeterPress press)
    {
        TaskbarSettings settings = _settings.Taskbar;

        if (press.Button == MouseButton.Middle)
        {
            Act(settings.MiddleClick);
            return;
        }

        switch (MeterPresses.Decide(press.Count, settings.DoubleClick))
        {
            case PressStep.Click:
                Act(settings.Click);
                break;

            case PressStep.DoubleClick:
                _singleClick.Stop();
                Act(settings.DoubleClick);
                break;

            case PressStep.Wait:
                _singleClick.Stop();
                _singleClick.Start();
                break;
        }
    }

    private void Act(MeterClick action)
    {
        if (action != MeterClick.Nothing)
        {
            ActionRequested?.Invoke(this, action);
        }
    }

    private void OnMenu(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement window)
        {
            MenuRequested?.Invoke(this, window);
        }
    }

    private void OnCardOpening(object? sender, EventArgs e) => Guard(() =>
    {
        MeterStation? station = _stations.Find(each => ReferenceEquals(each.Window, sender));

        if (station is null)
        {
            return;
        }

        _cardOpenOn = station;
        _card.Open(station.Model.Tooltip);
    });

    private void OnCardClosed(object? sender, EventArgs e) => Guard(() =>
    {
        if (_cardOpenOn is { } station && ReferenceEquals(station.Window, sender))
        {
            _cardOpenOn = null;
            _card.Close();
        }
    });

    /// <summary>
    /// Remembers where a floating meter was dragged to, or forgets it when it
    /// was dropped back on its own spot.
    /// </summary>
    private void OnDropped(object? sender, PixelRect dropped) => Guard(() =>
    {
        MeterStation? station = _stations.Find(each => ReferenceEquals(each.Window, sender));

        if (station is null || station.Target.Docked)
        {
            return;
        }

        TaskbarSettings settings = _settings.Taskbar;
        string monitor = station.Target.Monitor.Name;
        PixelRect home = station.Target.Bar is not null && station.Spot is { } spot
            ? spot.Screen
            : DockGeometry.Corner(station.Target.Monitor.Work, station.Length, station.Thickness, station.Dpi);

        if (FloatingPlacement.IsHome(dropped, home, station.Dpi))
        {
            settings.Forget(monitor);
        }
        else
        {
            settings.Remember(monitor, dropped.Left, dropped.Top);
        }

        _store.Save(_settings);
        station.Placed = dropped;
        Station(station, force: true);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    });

    private void QueueSurvey()
    {
        if (_surveyQueued || _disposed)
        {
            return;
        }

        _surveyQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _surveyQueued = false;

            if (_settings.Taskbar.Enabled && _started)
            {
                Guard(() =>
                {
                    Refresh();
                    Reconcile();
                });
            }
        }));
    }

    private void QueueStations()
    {
        if (_stationQueued || _disposed)
        {
            return;
        }

        _stationQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            _stationQueued = false;

            Guard(() =>
            {
                foreach (MeterStation station in _stations)
                {
                    Station(station);
                }

                Report();
            });
        }));
    }

    private void QueueFront()
    {
        if (_frontQueued || _disposed || !_started)
        {
            return;
        }

        _frontQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _frontQueued = false;

            Guard(() =>
            {
                CheckFront();

                foreach (MeterStation station in _stations)
                {
                    Station(station);
                }

                Report();
            });
        }));
    }

    // -------------------------------------------------------------- readings

    private void Listen(TaskbarSettings? settings)
    {
        bool wanted = settings is not null;

        if (wanted != _listening)
        {
            _listening = wanted;

            if (wanted)
            {
                _network.SampleTaken += OnNetworkSample;
                _connection.StateChanged += OnConnectionChanged;
                _download = _network.Latest.DownloadBytesPerSecond;
                _upload = _network.Latest.UploadBytesPerSecond;
                _state = _connection.State;
            }
            else
            {
                _network.SampleTaken -= OnNetworkSample;
                _connection.StateChanged -= OnConnectionChanged;
                _state = default;
            }
        }

        bool machine = settings is not null && NeedsMachine(settings);

        if (machine == (_machineWatch is not null))
        {
            return;
        }

        if (machine)
        {
            _machine.Sampled += OnMachineSample;
            _machineWatch = _machine.Watch();
            _snapshot = _machine.Latest;
            return;
        }

        _machine.Sampled -= OnMachineSample;
        _machineWatch?.Dispose();
        _machineWatch = null;
        _snapshot = SystemSnapshot.Empty;
    }

    private static bool NeedsMachine(TaskbarSettings settings)
    {
        for (int row = 0; row < settings.Rows; row++)
        {
            for (int column = 0; column < settings.Columns; column++)
            {
                if (MeterReadout.NeedsMachine(settings.Slot(row, column)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void OnNetworkSample(object? sender, ThroughputSample sample)
    {
        _download = sample.DownloadBytesPerSecond;
        _upload = sample.UploadBytesPerSecond;
        Queue();
    }

    private void OnConnectionChanged(object? sender, ConnectionState state)
    {
        _state = state;
        Queue();
    }

    private void OnMachineSample(object? sender, SystemSnapshot snapshot)
    {
        _snapshot = snapshot;
        Queue();
    }

    private void Queue()
    {
        if (_pending)
        {
            return;
        }

        _pending = true;

        // Drawn every reading, from whatever a settings file put in the places:
        // inside Guard, so a reading that cannot be drawn takes the meter down
        // and says so, never the app.
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Guard(Draw)));
    }

    private void Draw()
    {
        _pending = false;

        MeterReading reading = Reading();

        foreach (MeterStation station in _stations)
        {
            station.Model.Update(reading);
        }

        if (_cardOpenOn is { } open)
        {
            _card.Update(open.Model.Tooltip);
        }
    }

    private MeterReading Reading() =>
        new(_download, _upload, _snapshot) { Connection = _state, Extras = _sources.Extras };

    // --------------------------------------------------------------- telling

    private string? Preface(TaskbarSettings settings) =>
        _remote && settings.Remote == MeterRemote.Name ? MeterWords.RemotePreface(Environment.MachineName) : null;

    /// <summary>Works out what to tell the Taskbar page, and tells it if anything changed.</summary>
    private void Report(bool failed = false)
    {
        TaskbarSettings settings = _settings.Taskbar;

        if (!failed && !Changed(settings))
        {
            return;
        }

        _notes = [.. _stations.Select(station => station.Note)];

        MeterState state = (settings.Enabled, failed) switch
        {
            (false, _) => MeterState.Off,
            (true, true) => MeterState.Unavailable,
            _ when _remote && settings.Remote == MeterRemote.Hide => MeterState.RemoteHidden,
            _ when _stations.Count == 0 => MeterState.Unavailable,
            _ when _stations.Exists(station => station.Target.Docked) => MeterState.Docked,
            _ => MeterState.Floating,
        };

        if (state == MeterState.Unavailable && !failed && settings.Placement == MeterPlacement.Docked)
        {
            LogNoTaskbar();
        }

        _situation = new MeterSituation
        {
            State = state,
            Stations = _notes,
            RemoteMachine = _remote ? Environment.MachineName : null,
            MissingDisplay = _missing,
            BarlessDisplay = _barless,
            Replacement = _replacement,
        };

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Whether anything the page is told has changed, checked without
    /// building anything: this runs on every patrol.
    /// </summary>
    private bool Changed(TaskbarSettings settings)
    {
        MeterSituation known = _situation;

        if (!settings.Enabled)
        {
            return known.State != MeterState.Off;
        }

        if (known.State == MeterState.Off
            || _notes.Length != _stations.Count
            || known.MissingDisplay != _missing
            || known.BarlessDisplay != _barless
            || known.Replacement != _replacement
            || (known.RemoteMachine is not null) != _remote
            || (known.State == MeterState.RemoteHidden) != (_remote && settings.Remote == MeterRemote.Hide))
        {
            return true;
        }

        for (int each = 0; each < _notes.Length; each++)
        {
            if (_notes[each] != _stations[each].Note)
            {
                return true;
            }
        }

        bool docked = _stations.Exists(station => station.Target.Docked);

        return _stations.Count > 0 && (known.State == MeterState.Docked) != docked;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Taskbar meter has no taskbar to dock into")]
    private partial void LogNoTaskbar();

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Taskbar meter could not be docked into the taskbar")]
    private partial void LogDockFailed();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Taskbar meter docked on {Display} ({Kind} taskbar, main: {Primary})")]
    private partial void LogDocked(string display, TaskbarKind kind, bool primary);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Taskbar meter failed; carrying on without it")]
    private partial void LogBuildFailed(Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Explorer kept putting the task band back on {Display}; it is left as Explorer has it")]
    private partial void LogBandGivenUp(string display);
}
