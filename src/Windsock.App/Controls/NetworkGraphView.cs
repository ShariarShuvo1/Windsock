using System.Globalization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Windsock.App.ViewModels;
using Windsock.App.Services;
using Windsock.Core.Geography;

namespace Windsock.App.Controls;

/// <summary>A node with nothing behind it but the words on it.</summary>
public sealed record GraphNote(string Title, string Body, string Detail = "", ImageSource? Icon = null);

/// <summary>What the pointer is over.</summary>
public sealed class GraphHoverEventArgs(object? item) : EventArgs
{
    /// <summary>The node under the pointer, or null for none.</summary>
    public object? Item { get; } = item;
}

/// <summary>
/// Draws where a process's traffic goes, and the traffic moving as it happens.
/// </summary>
public sealed class NetworkGraphView : Panel, IViewAnchor
{
    private const double Edge = 6;
    private const double HubWidth = 208;
    private const double HubHeight = 62;
    private const double PeerWidth = 202;
    private const double PeerHeight = 38;
    private const double ProcessWidth = 198;
    private const double ProcessHeight = 46;
    private const double HopWidth = 128;
    private const double HopHeight = 26;
    private const double Clear = 30;
    private const double WorldWidth = 4800;
    private const double WorldClear = 7;
    private const double HomeDot = 22;
    private const double ProcessDot = 17;
    private const double PeerDot = 15;
    private const double HopDot = 9;
    private const int Rings = 40;
    private const int Turns = 23;
    private const double Swing = 0.28;
    private const int TidyRings = 6;
    private const double Golden = 2.39996;
    private const double DragThreshold = 3;
    private const double EmptyWidth = 320;
    private const double DotRadius = 3.1;
    private const double LaneOffset = 3.6;
    private const double BusiestRate = 22;
    private const int MostDots = 36;
    private const double DotBudget = 430;
    private const double SlowestFlow = 430;
    private const double FastestFlow = 950;
    private const double BriefestPath = 0.7;
    private const double LongestPath = 2.6;
    private const double Settling = 0.22;
    private const double Leaving = 0.26;
    private const double Seedling = 0.82;
    private const double Arrived = 0.4;
    private const double Resting = 1;
    private const double Aside = 0.32;

    private readonly Dictionary<string, Stream> _streams = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Motion> _motions = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Point> _places = new(StringComparer.Ordinal);

    private readonly Dictionary<long, List<Spot>> _cells = [];
    private readonly Queue<Spot> _queue = new();

    private const double Cell = 260;
    private int _turns;

    private readonly List<(WorldCountry Country, Geometry Shape, Rect Box)> _land = [];

    private readonly Dictionary<(string Label, int Size), FormattedText> _written = [];

    private readonly Dictionary<int, Brush> _shades = [];

    private static readonly Typeface _face = new(
        new FontFamily("Segoe UI"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    private WorldMap? _drawn;
    private Point _drawnFrom;
    private Point _worldOrigin;
    private Point _offset;
    private WorldCountry? _over;
    private readonly List<Spot> _order = [];
    private readonly List<Ghost> _ghosts = [];
    private readonly Dictionary<int, Pen> _pens = [];

    private readonly Dictionary<string, Point> _byHand = new(StringComparer.Ordinal);
    private readonly HashSet<string> _living = new(StringComparer.Ordinal);
    private readonly HashSet<string> _here = new(StringComparer.Ordinal);
    private readonly Dictionary<object, Button> _nodes = [];
    private readonly Dictionary<object, Spot> _byItem = [];
    private readonly List<Spot> _spots = [];
    private readonly HashSet<object> _wanted = [];
    private readonly List<INotifyPropertyChanged> _watched = [];
    private readonly HashSet<UIElement> _shown = [];

    private readonly TextBlock _empty = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Visibility = Visibility.Collapsed,
    };

    private readonly EdgeLayer _edges;
    private Button? _hub;
    private Spot? _middle;
    private long _lastFrame;
    private bool _dirty = true;
    private int _dots = MostDots;
    private Spot? _held;
    private Vector _grabbed;
    private Point _wasAt;
    private bool _dragged;
    private double _spanX = 420;
    private double _spanY = 180;
    private double _left = -210;
    private double _top = -90;

    public static readonly DependencyProperty TreeProperty = DependencyProperty.Register(
        nameof(Tree),
        typeof(GraphBranch),
        typeof(NetworkGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnTreeChanged));

    public static readonly DependencyProperty CaptionProperty = Register(nameof(Caption), "This app");

    public static readonly DependencyProperty SubtitleProperty = Register(nameof(Subtitle), string.Empty);

    public static readonly DependencyProperty IconProperty = Register<ImageSource?>(nameof(Icon), null);

    public static readonly DependencyProperty IsLiveProperty = Register(nameof(IsLive), true);

    public static readonly DependencyProperty HintProperty = Register(nameof(Hint), string.Empty);

    public static readonly DependencyProperty EmptyMessageProperty =
        Register(nameof(EmptyMessage), "Not talking to anything");

    public static readonly DependencyProperty PeerStyleProperty = Register<Style?>(nameof(PeerStyle), null);

    public static readonly DependencyProperty HopStyleProperty = Register<Style?>(nameof(HopStyle), null);

    public static readonly DependencyProperty NodeStyleProperty = Register<Style?>(nameof(NodeStyle), null);

    public static readonly DependencyProperty ProcessStyleProperty =
        Register<Style?>(nameof(ProcessStyle), null);

    /// <summary>Whether the picture is laid out on the earth rather than freely.</summary>
    public static readonly DependencyProperty IsWorldProperty = DependencyProperty.Register(
        nameof(IsWorld),
        typeof(bool),
        typeof(NetworkGraphView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure, OnWorldChanged));

    /// <summary>The outline of the world to draw underneath everything.</summary>
    public static readonly DependencyProperty WorldProperty = DependencyProperty.Register(
        nameof(World),
        typeof(WorldMap),
        typeof(NetworkGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnWorldChanged));

    /// <summary>Where this machine is, which the map is drawn around.</summary>
    public static readonly DependencyProperty HomeProperty = DependencyProperty.Register(
        nameof(Home),
        typeof(WorldPlace?),
        typeof(NetworkGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>What each country has to say for itself, by country code.</summary>
    public static readonly DependencyProperty CountryNotesProperty =
        Register<IReadOnlyDictionary<string, CountryNote>?>(nameof(CountryNotes), null);

    public static readonly DependencyProperty LandBrushProperty = Register<Brush?>(nameof(LandBrush), null);

    public static readonly DependencyProperty CoastBrushProperty = Register<Brush?>(nameof(CoastBrush), null);

    public static readonly DependencyProperty LandLitBrushProperty =
        Register<Brush?>(nameof(LandLitBrush), null);

    public static readonly DependencyProperty LandBusyBrushProperty =
        Register<Brush?>(nameof(LandBusyBrush), null);

    /// <summary>Whether the signals travelling the roads are drawn at all.</summary>
    public static readonly DependencyProperty ShowSignalsProperty = DependencyProperty.Register(
        nameof(ShowSignals),
        typeof(bool),
        typeof(NetworkGraphView),
        new PropertyMetadata(true, OnPaintingChanged));

    /// <summary>Whether the countries on the map are named.</summary>
    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
        nameof(ShowLabels),
        typeof(bool),
        typeof(NetworkGraphView),
        new PropertyMetadata(false, OnPaintingChanged));

    /// <summary>Whether every country is named, not only the ones in play.</summary>
    public static readonly DependencyProperty ShowAllLabelsProperty = DependencyProperty.Register(
        nameof(ShowAllLabels),
        typeof(bool),
        typeof(NetworkGraphView),
        new PropertyMetadata(false, OnPaintingChanged));

    /// <summary>Whether each country is shaded by how much is going there.</summary>
    public static readonly DependencyProperty ShowHeatProperty = DependencyProperty.Register(
        nameof(ShowHeat),
        typeof(bool),
        typeof(NetworkGraphView),
        new PropertyMetadata(false, OnPaintingChanged));

    /// <summary>How far the view holding this is zoomed in.</summary>
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(
        nameof(Scale),
        typeof(double),
        typeof(NetworkGraphView),
        new PropertyMetadata(1d, OnScaleChanged));

    public static readonly DependencyProperty WorldPeerStyleProperty =
        Register<Style?>(nameof(WorldPeerStyle), null);

    public static readonly DependencyProperty WorldHopStyleProperty =
        Register<Style?>(nameof(WorldHopStyle), null);

    public static readonly DependencyProperty WorldProcessStyleProperty =
        Register<Style?>(nameof(WorldProcessStyle), null);

    public static readonly DependencyProperty PopoverStyleProperty =
        Register<Style?>(nameof(PopoverStyle), null);

    public static readonly DependencyProperty PeerTemplateProperty =
        Register<DataTemplate?>(nameof(PeerTemplate), null);

    public static readonly DependencyProperty HopTemplateProperty =
        Register<DataTemplate?>(nameof(HopTemplate), null);

    public static readonly DependencyProperty ProcessTemplateProperty =
        Register<DataTemplate?>(nameof(ProcessTemplate), null);

    public static readonly DependencyProperty NoteTemplateProperty =
        Register<DataTemplate?>(nameof(NoteTemplate), null);

    public static readonly DependencyProperty SendBrushProperty = Register<Brush?>(nameof(SendBrush), null);

    public static readonly DependencyProperty ReceiveBrushProperty = Register<Brush?>(nameof(ReceiveBrush), null);

    public static readonly DependencyProperty TrackBrushProperty = Register<Brush?>(nameof(TrackBrush), null);

    public static readonly DependencyProperty AccentBrushProperty = Register<Brush?>(nameof(AccentBrush), null);

    public static readonly DependencyProperty QuietBrushProperty = Register<Brush?>(nameof(QuietBrush), null);

    /// <summary>The whole picture: the process, and everything beyond it.</summary>
    public GraphBranch? Tree
    {
        get => (GraphBranch?)GetValue(TreeProperty);
        set => SetValue(TreeProperty, value);
    }

    /// <summary>What to call the hub.</summary>
    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <summary>The line under the hub's name.</summary>
    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>The picture on the hub: the program's own icon.</summary>
    public ImageSource? Icon
    {
        get => (ImageSource?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>What clicking the process in the middle will do, if anything.</summary>
    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>Whether the signals are moving.</summary>
    public bool IsLive
    {
        get => (bool)GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    /// <summary>What to say when there is nothing to draw.</summary>
    public string EmptyMessage
    {
        get => (string)GetValue(EmptyMessageProperty);
        set => SetValue(EmptyMessageProperty, value);
    }

    /// <summary>How a peer node looks, including lit, followed and dimmed.</summary>
    public Style? PeerStyle
    {
        get => (Style?)GetValue(PeerStyleProperty);
        set => SetValue(PeerStyleProperty, value);
    }

    /// <summary>How a hop node looks.</summary>
    public Style? HopStyle
    {
        get => (Style?)GetValue(HopStyleProperty);
        set => SetValue(HopStyleProperty, value);
    }

    /// <summary>Whether the picture is laid out on the earth rather than freely.</summary>
    public bool IsWorld
    {
        get => (bool)GetValue(IsWorldProperty);
        set => SetValue(IsWorldProperty, value);
    }

    /// <summary>The outline of the world to draw underneath everything.</summary>
    public WorldMap? World
    {
        get => (WorldMap?)GetValue(WorldProperty);
        set => SetValue(WorldProperty, value);
    }

    /// <summary>Where this machine is, as far as anyone can tell.</summary>
    public WorldPlace? Home
    {
        get => (WorldPlace?)GetValue(HomeProperty);
        set => SetValue(HomeProperty, value);
    }

    /// <summary>What each country has to say for itself, by country code.</summary>
    public IReadOnlyDictionary<string, CountryNote>? CountryNotes
    {
        get => (IReadOnlyDictionary<string, CountryNote>?)GetValue(CountryNotesProperty);
        set => SetValue(CountryNotesProperty, value);
    }

    /// <summary>How the land is filled.</summary>
    public Brush? LandBrush
    {
        get => (Brush?)GetValue(LandBrushProperty);
        set => SetValue(LandBrushProperty, value);
    }

    /// <summary>How coastlines and borders are drawn.</summary>
    public Brush? CoastBrush
    {
        get => (Brush?)GetValue(CoastBrushProperty);
        set => SetValue(CoastBrushProperty, value);
    }

    /// <summary>
    /// How far the view holding this is zoomed in, where one is life size.
    /// </summary>
    public double Scale
    {
        get => (double)GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    /// <summary>How the busiest country is filled, when they are shaded.</summary>
    public Brush? LandBusyBrush
    {
        get => (Brush?)GetValue(LandBusyBrushProperty);
        set => SetValue(LandBusyBrushProperty, value);
    }

    /// <summary>
    /// Whether the signals travelling the roads are drawn.
    /// </summary>
    public bool ShowSignals
    {
        get => (bool)GetValue(ShowSignalsProperty);
        set => SetValue(ShowSignalsProperty, value);
    }

    /// <summary>Whether the countries on the map are named.</summary>
    public bool ShowLabels
    {
        get => (bool)GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    /// <summary>
    /// Whether every country is named, or only the ones with something on them.
    /// </summary>
    public bool ShowAllLabels
    {
        get => (bool)GetValue(ShowAllLabelsProperty);
        set => SetValue(ShowAllLabelsProperty, value);
    }

    /// <summary>Whether each country is shaded by how much is going there.</summary>
    public bool ShowHeat
    {
        get => (bool)GetValue(ShowHeatProperty);
        set => SetValue(ShowHeatProperty, value);
    }

    /// <summary>How the country under the pointer is filled.</summary>
    public Brush? LandLitBrush
    {
        get => (Brush?)GetValue(LandLitBrushProperty);
        set => SetValue(LandLitBrushProperty, value);
    }

    /// <summary>How a far end looks as a dot on the world map.</summary>
    public Style? WorldPeerStyle
    {
        get => (Style?)GetValue(WorldPeerStyleProperty);
        set => SetValue(WorldPeerStyleProperty, value);
    }

    /// <summary>How a waypoint looks as a dot on the world map.</summary>
    public Style? WorldHopStyle
    {
        get => (Style?)GetValue(WorldHopStyleProperty);
        set => SetValue(WorldHopStyleProperty, value);
    }

    /// <summary>How a program, and this machine, look on the world map.</summary>
    public Style? WorldProcessStyle
    {
        get => (Style?)GetValue(WorldProcessStyleProperty);
        set => SetValue(WorldProcessStyleProperty, value);
    }

    /// <summary>How a program's node looks, on a picture that has them.</summary>
    public Style? ProcessStyle
    {
        get => (Style?)GetValue(ProcessStyleProperty);
        set => SetValue(ProcessStyleProperty, value);
    }

    /// <summary>How every other node looks.</summary>
    public Style? NodeStyle
    {
        get => (Style?)GetValue(NodeStyleProperty);
        set => SetValue(NodeStyleProperty, value);
    }

    /// <summary>How the popover on a node looks.</summary>
    public Style? PopoverStyle
    {
        get => (Style?)GetValue(PopoverStyleProperty);
        set => SetValue(PopoverStyleProperty, value);
    }

    /// <summary>What a peer says on the node itself.</summary>
    public DataTemplate? PeerTemplate
    {
        get => (DataTemplate?)GetValue(PeerTemplateProperty);
        set => SetValue(PeerTemplateProperty, value);
    }

    /// <summary>What a hop says on the node itself.</summary>
    public DataTemplate? HopTemplate
    {
        get => (DataTemplate?)GetValue(HopTemplateProperty);
        set => SetValue(HopTemplateProperty, value);
    }

    /// <summary>What a program says on the node itself.</summary>
    public DataTemplate? ProcessTemplate
    {
        get => (DataTemplate?)GetValue(ProcessTemplateProperty);
        set => SetValue(ProcessTemplateProperty, value);
    }

    /// <summary>What the hub says.</summary>
    public DataTemplate? NoteTemplate
    {
        get => (DataTemplate?)GetValue(NoteTemplateProperty);
        set => SetValue(NoteTemplateProperty, value);
    }

    public Brush? SendBrush
    {
        get => (Brush?)GetValue(SendBrushProperty);
        set => SetValue(SendBrushProperty, value);
    }

    public Brush? ReceiveBrush
    {
        get => (Brush?)GetValue(ReceiveBrushProperty);
        set => SetValue(ReceiveBrushProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public Brush? QuietBrush
    {
        get => (Brush?)GetValue(QuietBrushProperty);
        set => SetValue(QuietBrushProperty, value);
    }

    /// <summary>Raised as the pointer, or the focus, moves on and off the nodes.</summary>
    public event EventHandler<GraphHoverEventArgs>? HoverChanged;

    /// <summary>Raised when a node is clicked, or activated from the keyboard.</summary>
    public event EventHandler<GraphHoverEventArgs>? Activated;

    public NetworkGraphView()
    {
        // First in, so it paints behind every node.
        _edges = new EdgeLayer();

        Children.Add(_edges);
        Children.Add(_empty);

        Loaded += (_, _) =>
        {
            _lastFrame = DateTime.UtcNow.Ticks;
            CompositionTarget.Rendering += OnFrame;
        };

        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>
    /// The process itself, which is what the picture is drawn around.
    /// </summary>
    public Point Anchor => _middle is { } hub && hub.Area.Width > 0
        ? Middle(hub.Area)
        : new Point(DesiredSize.Width / 2, DesiredSize.Height / 2);

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(NetworkGraphView),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    private static void OnPaintingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is NetworkGraphView view)
        {
            view._dirty = true;
        }
    }

    private static void OnScaleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is NetworkGraphView view && view.IsWorld)
        {
            view._dirty = true;
        }
    }

    private static void OnWorldChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not NetworkGraphView view)
        {
            return;
        }

        view._places.Clear();
        view._byHand.Clear();
        view._drawn = null;
        view._over = null;
        view.ToolTip = null;

        foreach ((object item, Button node) in view._nodes)
        {
            view.Wear(node, item);
        }

        view.InvalidateMeasure();
    }

    private static void OnTreeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is NetworkGraphView graph)
        {
            graph.Rewatch();
        }
    }

    private void Rewatch()
    {
        foreach (INotifyPropertyChanged item in _watched)
        {
            item.PropertyChanged -= OnItemChanged;
        }

        _watched.Clear();
        Collect(Tree);

        foreach (INotifyPropertyChanged item in _watched)
        {
            item.PropertyChanged += OnItemChanged;
        }

        _dirty = true;
        InvalidateMeasure();
    }

    private void Collect(GraphBranch? branch)
    {
        if (branch is null)
        {
            return;
        }

        if (branch.Item is INotifyPropertyChanged watchable)
        {
            _watched.Add(watchable);
        }

        foreach (GraphBranch child in branch.Children)
        {
            Collect(child);
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => _dirty = true;

    /// <summary>
    /// Reports the size the picture actually needs.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        Plan();

        Size natural = new((Edge * 2) + _spanX, (Edge * 2) + _spanY);

        _edges.Measure(natural);
        _empty.Measure(natural);

        _dirty = true;
        return natural;
    }

    private void Plan()
    {
        Sync();

        _spots.Clear();
        _byItem.Clear();
        _middle = null;

        if (Tree is not { } root)
        {
            _left = -HubWidth / 2;
            _top = -HubHeight / 2;
            _spanX = HubWidth;
            _spanY = HubHeight;
            return;
        }

        _middle = Grow(root, null, 0);

        Anchorage();
        Landscape();
        Position();
        Room();
        Trails();
        Pose();
        foreach (string id in _motions.Keys.Where(key => !_here.Contains(key)).ToArray())
        {
            _motions.Remove(id);
        }
        foreach (string id in _places.Keys.Where(key => !_here.Contains(key)).ToArray())
        {
            _places.Remove(id);
        }
    }

    private void Pose()
    {
        foreach (Spot spot in _spots)
        {
            if (spot.Node is not { } node)
            {
                continue;
            }

            spot.Drift = spot.Motion.Shown - spot.At;
            Dress(node, spot.Drift, spot.Motion.Grown, spot.Branch.IsMuted);
        }
    }

    private void Trails()
    {
        int ends = 0;

        foreach (Spot spot in _spots)
        {
            if (spot.Branch.Item is not PeerNodeViewModel)
            {
                spot.Trail = [];
                continue;
            }

            List<Spot> back = [];

            for (Spot? step = spot; step is { Depth: > 0 }; step = step.Parent)
            {
                back.Add(step);
            }

            back.Reverse();
            spot.Trail = [.. back];
            ends++;
        }
        _dots = (int)Math.Clamp(DotBudget / Math.Max(1, ends), 4, MostDots);
    }

    private Spot Grow(GraphBranch branch, Spot? parent, int depth)
    {
        Spot spot = new(branch, parent, depth)
        {
            Motion = Track(branch.Id, parent, depth),
        };

        if (depth == 0)
        {
            spot.Node = _hub;
            spot.Width = IsWorld ? HomeDot : HubWidth;
            spot.Height = IsWorld ? HomeDot : Tall(_hub, HubWidth, HubHeight);
        }
        else if (branch.Item is { } item)
        {
            Button node = Node(item);

            if (IsWorld)
            {
                double dot = item switch
                {
                    ProcessNodeViewModel => ProcessDot,
                    PeerNodeViewModel => PeerDot,
                    _ => HopDot,
                };

                spot.Node = node;
                spot.Width = dot;
                spot.Height = dot;
                _byItem[item] = spot;
            }
            else
            {
                (double wide, double high) = item switch
                {
                    ProcessNodeViewModel => (ProcessWidth, ProcessHeight),
                    PeerNodeViewModel => (PeerWidth, PeerHeight),
                    _ => (HopWidth, HopHeight),
                };

                spot.Node = node;
                spot.Width = wide;
                spot.Height = Tall(node, wide, high);
                _byItem[item] = spot;
            }
        }

        foreach (GraphBranch child in branch.Children)
        {
            spot.Children.Add(Grow(child, spot, depth + 1));
        }

        _spots.Add(spot);
        return spot;
    }

    private Motion Track(string id, Spot? parent, int depth)
    {
        if (_motions.TryGetValue(id, out Motion? motion))
        {
            return motion;
        }

        motion = new Motion
        {
            Shown = depth == 0 || parent is null ? default : parent.Motion.Shown,
            Grown = depth == 0 ? 1 : 0,
        };

        _motions[id] = motion;
        return motion;
    }

    private void Position()
    {
        foreach (List<Spot> square in _cells.Values)
        {
            square.Clear();
        }

        if (_middle is null)
        {
            return;
        }

        _middle.At = default;
        Occupy(_middle);
        _order.Clear();
        _queue.Clear();
        _queue.Enqueue(_middle);

        while (_queue.Count > 0)
        {
            Spot spot = _queue.Dequeue();

            foreach (Spot child in spot.Children)
            {
                child.Settled = false;
                _order.Add(child);
                _queue.Enqueue(child);
            }
        }
        foreach (Spot spot in _order)
        {
            if (_byHand.TryGetValue(spot.Branch.Id, out Point put))
            {
                spot.At = put;
                spot.Settled = true;
                Occupy(spot);
            }
        }
        foreach (Spot spot in _order)
        {
            if (spot.Settled || !_places.TryGetValue(spot.Branch.Id, out Point kept))
            {
                continue;
            }

            if (Room(spot, kept))
            {
                spot.At = kept;
                spot.Settled = true;
                Occupy(spot);
                continue;
            }

            _places.Remove(spot.Branch.Id);
        }

        // And last the ones with nowhere to be, which can now see all of it.
        foreach (Spot spot in _order)
        {
            if (spot.Settled)
            {
                continue;
            }
            if (IsWorld && Belongs(spot) is { } wanted)
            {
                spot.At = Nearby(spot, wanted);
                _places[spot.Branch.Id] = spot.At;
                Occupy(spot);
                continue;
            }
            spot.At = Find(spot, tidy: true) ?? Find(spot, tidy: false) ?? Away(spot);
            _places[spot.Branch.Id] = spot.At;
            Occupy(spot);
        }
    }

    private Point? Find(Spot spot, bool tidy)
    {
        Point from = spot.Parent?.At ?? default;
        double least = Reach(spot.Parent) + Clear + Reach(spot);
        double heading = Heading(spot);

        int reach = tidy ? TidyRings : Rings;
        int first = Math.Max(0, (spot.Parent?.Ring ?? 0) - 1);

        for (int ring = first; ring < first + reach; ring++)
        {
            double span = least * (1 + (ring * 0.16));

            for (int turn = 0; turn < Turns; turn++)
            {
                double swing = (turn + 1) / 2 * Swing;
                double angle = heading + (turn % 2 == 0 ? swing : -swing);

                Point at = new(
                    from.X + (span * Math.Cos(angle)),
                    from.Y + (span * Math.Sin(angle)));

                if (Room(spot, at) && (!tidy || !Crosses(spot, from, at)))
                {
                    if (spot.Parent is { } parent)
                    {
                        parent.Ring = ring;
                    }

                    return at;
                }
            }
        }

        return null;
    }

    private void Anchorage()
    {
        if (!IsWorld || Home is not { IsKnown: true } home)
        {
            _worldOrigin = default;
            return;
        }

        double scale = WorldWidth / MapProjection.Extent().Width;
        (double x, double y) = MapProjection.Project(home.Latitude, home.Longitude);

        _worldOrigin = new Point(x * scale, -y * scale);
    }

    private void Landscape()
    {
        if (!IsWorld || World is not { } world)
        {
            _land.Clear();
            _drawn = null;
            return;
        }

        if (ReferenceEquals(_drawn, world) && _drawnFrom == _worldOrigin)
        {
            return;
        }

        _land.Clear();

        foreach (WorldCountry country in world.Countries)
        {
            StreamGeometry shape = new();

            using (StreamGeometryContext ink = shape.Open())
            {
                foreach (double[] ring in country.Rings)
                {
                    if (ring.Length < 6)
                    {
                        continue;
                    }

                    ink.BeginFigure(Where(ring[1], ring[0]), isFilled: true, isClosed: true);

                    for (int at = 2; at < ring.Length; at += 2)
                    {
                        ink.LineTo(Where(ring[at + 1], ring[at]), isStroked: true, isSmoothJoin: false);
                    }
                }
            }

            shape.Freeze();
            Point corner = Where(country.North, country.West);
            Point far = Where(country.South, country.East);

            _land.Add((country, shape, new Rect(
                Math.Min(corner.X, far.X),
                Math.Min(corner.Y, far.Y),
                Math.Abs(far.X - corner.X),
                Math.Abs(far.Y - corner.Y))));
        }

        _drawn = world;
        _drawnFrom = _worldOrigin;
        _written.Clear();
    }

    private void Land(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);

        if (_land.Count == 0)
        {
            return;
        }

        Brush? fill = LandBrush;
        Brush? lit = LandLitBrush ?? fill;
        Pen? edge = CoastBrush is { } coast ? Stroke(coast, 1) : null;

        IReadOnlyDictionary<string, CountryNote>? notes = CountryNotes;
        bool shade = ShowHeat && notes is { Count: > 0 } && LandBusyBrush is SolidColorBrush;

        dc.PushTransform(new TranslateTransform(_offset.X, _offset.Y));

        foreach ((WorldCountry country, Geometry shape, _) in _land)
        {
            Brush? paint = fill;

            if (ReferenceEquals(country, _over))
            {
                paint = lit;
            }
            else if (shade
                && notes!.TryGetValue(country.Code, out CountryNote? note)
                && note.Weight > 0)
            {
                paint = Shade(note.Weight);
            }

            dc.DrawGeometry(paint, edge, shape);
        }

        if (ShowLabels && notes is { Count: > 0 })
        {
            Lettering(dc, notes);
        }

        dc.Pop();
    }

    private Brush Shade(double weight)
    {
        int step = (int)Math.Round(Math.Clamp(Math.Sqrt(weight), 0, 1) * 20);

        if (_shades.TryGetValue(step, out Brush? known))
        {
            return known;
        }

        Color plain = LandBrush is SolidColorBrush ground ? ground.Color : Colors.Black;
        Color busy = LandBusyBrush is SolidColorBrush loud ? loud.Color : plain;
        double part = step / 20d;

        SolidColorBrush brush = new(Color.FromArgb(
            (byte)(plain.A + ((busy.A - plain.A) * part)),
            (byte)(plain.R + ((busy.R - plain.R) * part)),
            (byte)(plain.G + ((busy.G - plain.G) * part)),
            (byte)(plain.B + ((busy.B - plain.B) * part))));

        brush.Freeze();
        _shades[step] = brush;

        return brush;
    }

    private void Lettering(DrawingContext dc, IReadOnlyDictionary<string, CountryNote> notes)
    {
        Brush ink = QuietBrush ?? CoastBrush ?? Brushes.Gray;
        bool all = ShowAllLabels;

        double size = 13 * Magnify;
        int bucket = (int)Math.Round(size * 4);

        foreach ((WorldCountry country, _, Rect box) in _land)
        {
            bool wanted = country.Code.Length > 0 && notes.ContainsKey(country.Code);

            if (!wanted && !all)
            {
                continue;
            }

            if (!_written.TryGetValue((country.Label, bucket), out FormattedText? text))
            {
                text = new FormattedText(
                    country.Label,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    _face,
                    size,
                    ink,
                    1.25)
                {
                    TextAlignment = TextAlignment.Center,
                };

                _written[(country.Label, bucket)] = text;
            }
            if (!wanted && (text.Width > box.Width * 0.92 || text.Height > box.Height))
            {
                continue;
            }

            Point at = Where(country.LabelLatitude, country.LabelLongitude);
            dc.DrawText(text, new Point(at.X, at.Y - (text.Height / 2)));
        }
    }

    private Point? Belongs(Spot spot) =>
        spot.Branch.Place is { IsKnown: true } place ? Where(place) : null;

    private Point Where(WorldPlace place) => Where(place.Latitude, place.Longitude);

    private Point Where(double latitude, double longitude)
    {
        (double x, double y) = MapProjection.Project(latitude, longitude);
        double scale = WorldWidth / MapProjection.Extent().Width;

        return new Point((x * scale) - _worldOrigin.X, (-y * scale) - _worldOrigin.Y);
    }

    private (double Latitude, double Longitude) Whereabouts(Point at)
    {
        double scale = WorldWidth / MapProjection.Extent().Width;

        return MapProjection.Unproject(
            (at.X + _worldOrigin.X) / scale,
            -(at.Y + _worldOrigin.Y) / scale);
    }

    private Point Nearby(Spot spot, Point wanted)
    {
        if (Room(spot, wanted))
        {
            return wanted;
        }

        double step = (spot.Width + WorldClear) * 0.62;

        for (int seat = 1; seat < 400; seat++)
        {
            double angle = seat * Golden;
            double span = step * Math.Sqrt(seat);

            Point at = new(
                wanted.X + (span * Math.Cos(angle)),
                wanted.Y + (span * Math.Sin(angle)));

            if (Room(spot, at))
            {
                return at;
            }
        }

        return wanted;
    }

    private Point Away(Spot spot)
    {
        Point from = spot.Parent?.At ?? default;
        double heading = Heading(spot);
        double span = Reach(spot.Parent) + Clear + Reach(spot);

        for (int ring = Rings; ring < Rings * 6; ring++)
        {
            double out_ = span * (1 + (ring * 0.16));

            Point at = new(
                from.X + (out_ * Math.Cos(heading)),
                from.Y + (out_ * Math.Sin(heading)));

            if (Room(spot, at))
            {
                return at;
            }
        }

        return new Point(from.X + (span * 8 * Math.Cos(heading)), from.Y + (span * 8 * Math.Sin(heading)));
    }

    private double Heading(Spot spot)
    {
        if (spot.Parent is not { } parent)
        {
            return 0;
        }

        if (parent.Depth > 0 && parent.Parent is { } older)
        {
            Vector onwards = parent.At - older.At;

            if (onwards.LengthSquared > 1)
            {
                return Math.Atan2(onwards.Y, onwards.X);
            }
        }

        return _turns++ * Golden;
    }

    private static double Reach(Spot? spot) =>
        spot is null ? 0 : Math.Sqrt((spot.Width * spot.Width) + (spot.Height * spot.Height)) / 2;

    private double Gap => IsWorld ? WorldClear : Clear;

    private double Magnify => IsWorld ? Math.Clamp(1 / Math.Max(0.05, Scale), 1, 6) : 1;

    private void Occupy(Spot spot)
    {
        double gap = Gap;
        int left = Square(spot.At.X - (spot.Width / 2) - gap);
        int right = Square(spot.At.X + (spot.Width / 2) + gap);
        int top = Square(spot.At.Y - (spot.Height / 2) - gap);
        int bottom = Square(spot.At.Y + (spot.Height / 2) + gap);

        for (int across = left; across <= right; across++)
        {
            for (int down = top; down <= bottom; down++)
            {
                Squares(across, down).Add(spot);
            }
        }
    }

    private static int Square(double at) => (int)Math.Floor(at / Cell);

    private List<Spot> Squares(int across, int down)
    {
        long key = ((long)across << 32) ^ (uint)down;

        if (!_cells.TryGetValue(key, out List<Spot>? square))
        {
            square = [];
            _cells[key] = square;
        }

        return square;
    }

    private bool Room(Spot spot, Point at)
    {
        double gap = Gap;
        double wide = (spot.Width / 2) + (gap / 2);
        double tall = (spot.Height / 2) + (gap / 2);

        int left = Square(at.X - wide - (Cell / 2));
        int right = Square(at.X + wide + (Cell / 2));
        int top = Square(at.Y - tall - (Cell / 2));
        int bottom = Square(at.Y + tall + (Cell / 2));

        for (int across = left; across <= right; across++)
        {
            for (int down = top; down <= bottom; down++)
            {
                if (!_cells.TryGetValue(((long)across << 32) ^ (uint)down, out List<Spot>? square))
                {
                    continue;
                }

                foreach (Spot other in square)
                {
                    if (Math.Abs(other.At.X - at.X) < wide + (other.Width / 2) + (gap / 2)
                        && Math.Abs(other.At.Y - at.Y) < tall + (other.Height / 2) + (gap / 2))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private bool Crosses(Spot spot, Point from, Point to)
    {
        int left = Square(Math.Min(from.X, to.X) - Cell);
        int right = Square(Math.Max(from.X, to.X) + Cell);
        int top = Square(Math.Min(from.Y, to.Y) - Cell);
        int bottom = Square(Math.Max(from.Y, to.Y) + Cell);

        for (int across = left; across <= right; across++)
        {
            for (int down = top; down <= bottom; down++)
            {
                if (!_cells.TryGetValue(((long)across << 32) ^ (uint)down, out List<Spot>? square))
                {
                    continue;
                }

                if (Blocked(square, spot, from, to))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Blocked(List<Spot> square, Spot spot, Point from, Point to)
    {
        foreach (Spot other in square)
        {
            if (ReferenceEquals(other, spot.Parent))
            {
                continue;
            }

            Rect box = new(
                other.At.X - (other.Width / 2) - (Clear / 4),
                other.At.Y - (other.Height / 2) - (Clear / 4),
                other.Width + (Clear / 2),
                other.Height + (Clear / 2));

            if (Meets(box, from, to))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Meets(Rect box, Point from, Point to)
    {
        double runX = to.X - from.X;
        double runY = to.Y - from.Y;
        double near = 0;
        double far = 1;

        if (!Slab(runX, box.Left - from.X, box.Right - from.X, ref near, ref far))
        {
            return false;
        }

        return Slab(runY, box.Top - from.Y, box.Bottom - from.Y, ref near, ref far);
    }

    private static bool Slab(double run, double low, double high, ref double near, ref double far)
    {
        if (Math.Abs(run) < 0.0001)
        {
            return low <= 0 && high >= 0;
        }

        double one = low / run;
        double two = high / run;

        if (one > two)
        {
            (one, two) = (two, one);
        }

        near = Math.Max(near, one);
        far = Math.Min(far, two);

        return near <= far;
    }

    private void Room()
    {
        double least = _middle?.Height ?? HubHeight;

        _left = -HubWidth / 2;
        _top = -least / 2;

        double right = HubWidth / 2;
        double bottom = least / 2;

        foreach (Spot spot in _spots)
        {
            if (spot.Depth == 0)
            {
                continue;
            }

            _left = Math.Min(_left, spot.At.X - (spot.Width / 2));
            _top = Math.Min(_top, spot.At.Y - (spot.Height / 2));
            right = Math.Max(right, spot.At.X + (spot.Width / 2));
            bottom = Math.Max(bottom, spot.At.Y + (spot.Height / 2));
        }

        if (IsWorld && _land.Count > 0)
        {
            (double wide, double high) = MapProjection.Extent();
            double scale = WorldWidth / wide;

            _left = Math.Min(_left, (-wide / 2 * scale) - _worldOrigin.X);
            _top = Math.Min(_top, (-high / 2 * scale) - _worldOrigin.Y);
            right = Math.Max(right, (wide / 2 * scale) - _worldOrigin.X);
            bottom = Math.Max(bottom, (high / 2 * scale) - _worldOrigin.Y);
        }

        if (_middle is { Children.Count: 0 })
        {
            _empty.Text = EmptyMessage;
            _empty.Measure(new Size(EmptyWidth, double.PositiveInfinity));

            _left = Math.Min(_left, -EmptyWidth / 2);
            right = Math.Max(right, EmptyWidth / 2);
            bottom += 18 + _empty.DesiredSize.Height;
        }

        _spanX = right - _left;
        _spanY = bottom - _top;
    }

    private static double Tall(UIElement? node, double width, double least)
    {
        if (node is null)
        {
            return least;
        }

        node.Measure(new Size(width, double.PositiveInfinity));

        // Rounded up: half a pixel short is still short, and still clipped.
        return Math.Max(least, Math.Ceiling(node.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _shown.Clear();

        _edges.Arrange(new Rect(finalSize));
        _shown.Add(_edges);
        Point middle = new(Edge - _left, Edge - _top);
        _offset = middle;

        foreach (Spot spot in _spots)
        {
            spot.Area = new Rect(
                middle.X + spot.At.X - (spot.Width / 2),
                middle.Y + spot.At.Y - (spot.Height / 2),
                spot.Width,
                spot.Height);

            Place(spot.Node, spot.Area);
        }

        if (_middle is { Children.Count: 0 })
        {
            Nothing(finalSize, middle);
        }
        foreach (Ghost ghost in _ghosts)
        {
            _shown.Add(ghost.Node);

            ghost.Node.Arrange(new Rect(
                middle.X + ghost.At.X - (ghost.Size.Width / 2),
                middle.Y + ghost.At.Y - (ghost.Size.Height / 2),
                ghost.Size.Width,
                ghost.Size.Height));
        }
        foreach (UIElement child in InternalChildren)
        {
            if (_shown.Contains(child))
            {
                continue;
            }

            if (child.Visibility != Visibility.Collapsed)
            {
                child.Visibility = Visibility.Collapsed;
            }

            child.Arrange(default);
        }

        // Drawn from where the nodes ended up, so it happens once they have.
        Wire();
        _edges.Roads(Roads);
        _edges.Traffic(Traffic);

        return finalSize;
    }

    private void Nothing(Size size, Point middle)
    {
        double width = Math.Max(60, Math.Min(size.Width - (Edge * 2), EmptyWidth));

        _empty.Text = EmptyMessage;
        _shown.Add(_empty);

        if (_empty.Visibility != Visibility.Visible)
        {
            _empty.Visibility = Visibility.Visible;
        }

        _empty.Measure(new Size(width, size.Height));

        _empty.Arrange(new Rect(
            middle.X - (width / 2),
            middle.Y + ((_middle?.Height ?? HubHeight) / 2) + 18,
            width,
            _empty.DesiredSize.Height));
    }

    private void Place(Button? node, Rect area)
    {
        if (node is null)
        {
            return;
        }

        if (node.Visibility != Visibility.Visible)
        {
            node.Visibility = Visibility.Visible;
        }

        _shown.Add(node);
        node.Arrange(area);
    }

    private void Sync()
    {
        _hub ??= Create(NodeStyle);

        _hub.Style = IsWorld ? WorldProcessStyle ?? NodeStyle : NodeStyle;
        _hub.ContentTemplate = IsWorld ? null : NoteTemplate;

        Fill(_hub, new GraphNote(Caption, Subtitle, Hint, Icon));

        _wanted.Clear();
        _here.Clear();
        Want(Tree);

        foreach (object gone in _nodes.Keys.Where(key => !_wanted.Contains(key)).ToArray())
        {
            Button node = _nodes[gone];
            _nodes.Remove(gone);
            if (_byItem.TryGetValue(gone, out Spot? was) && was.Area.Width > 0)
            {
                _ghosts.Add(new Ghost
                {
                    Node = node,
                    At = was.At,
                    Size = new Size(was.Width, was.Height),
                    From = was.Drift,
                    Toward = (was.Parent?.At ?? was.At) - was.At,
                });

                continue;
            }

            Children.Remove(node);
        }
        foreach (string id in _byHand.Keys.ToArray())
        {
            if (!_here.Contains(id))
            {
                _byHand.Remove(id);
            }
        }
    }

    private void Want(GraphBranch? branch)
    {
        if (branch is null)
        {
            return;
        }

        if (branch.Item is { } item)
        {
            _wanted.Add(item);
            _ = Node(item);
        }

        _here.Add(branch.Id);

        foreach (GraphBranch child in branch.Children)
        {
            Want(child);
        }
    }

    private Button Node(object item)
    {
        if (_nodes.TryGetValue(item, out Button? node))
        {
            return node;
        }

        node = Create(null);
        Wear(node, item);
        _nodes[item] = node;

        return node;
    }

    private void Wear(Button node, object item)
    {
        node.Style = IsWorld
            ? item switch
            {
                ProcessNodeViewModel => WorldProcessStyle ?? ProcessStyle,
                HopNodeViewModel => WorldHopStyle ?? WorldPeerStyle,
                _ => WorldPeerStyle ?? PeerStyle,
            }
            : item switch
            {
                ProcessNodeViewModel => ProcessStyle ?? PeerStyle,
                PeerNodeViewModel => PeerStyle,
                HopNodeViewModel => HopStyle ?? NodeStyle,
                _ => NodeStyle,
            };
        node.ContentTemplate = IsWorld
            ? null
            : item switch
            {
                ProcessNodeViewModel => ProcessTemplate ?? PeerTemplate,
                PeerNodeViewModel => PeerTemplate,
                HopNodeViewModel => HopTemplate,
                _ => NoteTemplate,
            };

        Fill(node, item);
    }

    private Button Create(Style? style)
    {
        Button node = new()
        {
            Style = style,
            Focusable = true,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup
            {
                Children = { new ScaleTransform(1, 1), new TranslateTransform() },
            },
        };
        ToolTipService.SetShowsToolTipOnKeyboardFocus(node, true);
        ToolTipService.SetInitialShowDelay(node, 240);
        ToolTipService.SetShowDuration(node, 60000);
        ToolTipService.SetBetweenShowDelay(node, 0);

        node.PreviewMouseLeftButtonDown += OnNodePressed;
        node.PreviewMouseMove += OnNodeMoved;
        node.PreviewMouseLeftButtonUp += OnNodeReleased;

        node.Click += (sender, _) =>
        {
            if (_dragged)
            {
                _dragged = false;
                return;
            }

            Activated?.Invoke(this, new GraphHoverEventArgs(Held(sender)));
        };
        node.MouseEnter += (sender, _) => HoverChanged?.Invoke(this, new GraphHoverEventArgs(Held(sender)));
        node.MouseLeave += (_, _) => HoverChanged?.Invoke(this, new GraphHoverEventArgs(null));
        node.GotKeyboardFocus += (sender, _) => HoverChanged?.Invoke(this, new GraphHoverEventArgs(Held(sender)));
        node.LostKeyboardFocus += (_, _) => HoverChanged?.Invoke(this, new GraphHoverEventArgs(null));

        Children.Add(node);
        return node;
    }

    private static object? Held(object? sender) => (sender as Button)?.Content;

    private void OnNodePressed(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (Held(sender) is not { } item
            || !_byItem.TryGetValue(item, out Spot? spot)
            || !spot.Branch.CanMove)
        {
            return;
        }

        _held = spot;
        _dragged = false;
        _wasAt = spot.At;
        _grabbed = e.GetPosition(this) - Anchor;
    }

    private void OnNodeMoved(object sender, MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_held is not { } spot || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Vector moved = (e.GetPosition(this) - Anchor) - _grabbed;

        // A press that wanders a pixel or two is still a press.
        if (!_dragged && moved.Length < DragThreshold)
        {
            return;
        }

        _dragged = true;
        _byHand[spot.Branch.Id] = new Point(_wasAt.X + moved.X, _wasAt.Y + moved.Y);
        InvalidateMeasure();
    }

    private void OnNodeReleased(object sender, MouseButtonEventArgs e) => _held = null;

    /// <summary>
    /// Says which country the pointer is over, on the world map.
    /// </summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);

        if (!IsWorld || World is not { } world)
        {
            Over(null);
            return;
        }

        Point at = e.GetPosition(this);
        (double latitude, double longitude) = Whereabouts(new Point(at.X - _offset.X, at.Y - _offset.Y));

        Over(world.At(longitude, latitude));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Over(null);
    }

    private void Over(WorldCountry? country)
    {
        if (ReferenceEquals(country, _over))
        {
            return;
        }

        _over = country;

        if (country is null)
        {
            ToolTip = null;
        }
        else
        {
            CountryNote? note = CountryNotes is { } notes
                && notes.TryGetValue(country.Code, out CountryNote? said)
                    ? said
                    : null;

            ToolTip = new ToolTip
            {
                Content = new GraphNote(
                    country.Name,
                    note?.Summary ?? "Nothing on this machine is talking to anywhere here",
                    note?.Places ?? string.Empty),
                Style = PopoverStyle,
            };
        }

        _edges.Land(Land);
    }

    private void Fill(Button node, object item)
    {
        if (Equals(node.Content, item))
        {
            return;
        }

        node.Content = item;
        node.DataContext = item;
        AutomationProperties.SetName(node, item is GraphNote note ? note.Title : item.ToString() ?? string.Empty);

        node.ToolTip = new ToolTip { Content = item, Style = PopoverStyle };
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        long now = DateTime.UtcNow.Ticks;
        double seconds = (now - _lastFrame) / (double)TimeSpan.TicksPerSecond;
        _lastFrame = now;
        if (seconds <= 0 || seconds > 0.5)
        {
            seconds = 0;
        }

        bool settling = seconds > 0 && Ease(seconds);
        if (_dirty || settling)
        {
            Wire();
            _edges.Land(Land);
            _edges.Roads(Roads);
        }

        bool flowing = IsLive && ShowSignals && seconds > 0 && Flow(seconds);

        if (_dirty || settling || flowing)
        {
            _edges.Traffic(Traffic);
        }

        _dirty = false;
    }

    private bool Ease(double seconds)
    {
        double fade = 1 - Math.Exp(-seconds / Settling);
        bool moving = false;

        foreach (Spot spot in _spots)
        {
            Motion motion = spot.Motion;

            if (spot.Node is not { } node)
            {
                continue;
            }
            bool dragged = _held is { } held
                && string.Equals(held.Branch.Id, spot.Branch.Id, StringComparison.Ordinal);

            bool stirred;

            if (dragged)
            {
                stirred = motion.Shown != spot.At;
                motion.Shown = spot.At;
                motion.Velocity = default;
            }
            else
            {
                stirred = Glide(motion, spot.At, seconds);
                moving |= stirred;
            }

            if (motion.Grown < 1)
            {
                motion.Grown += (1 - motion.Grown) * fade;

                if (motion.Grown > 0.997)
                {
                    motion.Grown = 1;
                }

                moving = true;
            }

            spot.Drift = motion.Shown - spot.At;
            bool muted = spot.Branch.IsMuted;

            if (stirred || motion.Grown < 1 || muted != motion.Muted)
            {
                Dress(node, spot.Drift, motion.Grown, muted);
                motion.Muted = muted;
            }
        }

        return Depart(seconds) || moving;
    }

    private static bool Glide(Motion motion, Point target, double seconds)
    {
        Vector change = motion.Shown - target;

        if (change.LengthSquared <= Arrived * Arrived
            && motion.Velocity.LengthSquared <= Resting * Resting)
        {
            motion.Shown = target;
            motion.Velocity = default;
            return false;
        }

        double omega = 2 / Settling;
        double ratio = omega * seconds;
        double decay = 1 / (1 + ratio + (0.48 * ratio * ratio) + (0.235 * ratio * ratio * ratio));

        Vector step = (motion.Velocity + (change * omega)) * seconds;

        motion.Velocity = (motion.Velocity - (step * omega)) * decay;
        motion.Shown = target + ((change + step) * decay);

        return true;
    }

    private bool Depart(double seconds)
    {
        if (_ghosts.Count == 0)
        {
            return false;
        }

        for (int index = _ghosts.Count - 1; index >= 0; index--)
        {
            Ghost ghost = _ghosts[index];
            ghost.Gone += seconds / Leaving;

            if (ghost.Gone >= 1)
            {
                Children.Remove(ghost.Node);
                _ghosts.RemoveAt(index);
                continue;
            }
            double left = 1 - ghost.Gone;
            Dress(ghost.Node, ghost.From + (ghost.Toward * ghost.Gone), left * left, muted: false);
        }

        return true;
    }

    private static void Dress(Button node, Vector drift, double grown, bool muted)
    {
        if (node.RenderTransform is not TransformGroup group
            || group.Children.Count != 2
            || group.Children[0] is not ScaleTransform scale
            || group.Children[1] is not TranslateTransform move)
        {
            return;
        }

        double size = Seedling + ((1 - Seedling) * grown);

        scale.ScaleX = size;
        scale.ScaleY = size;
        move.X = drift.X;
        move.Y = drift.Y;
        node.Opacity = muted ? grown * Aside : grown;
    }

    private void Wire()
    {
        foreach (Spot spot in _spots)
        {
            if (spot.Parent is not { } parent || spot.Area.Width <= 0)
            {
                spot.Length = 0;
                continue;
            }

            Rect from = parent.Seen;
            Rect to = spot.Seen;
            Point origin = Rim(from, Middle(to));
            Point target = Rim(to, Middle(from));

            Vector run = target - origin;
            double length = run.Length;

            spot.Origin = origin;
            spot.Target = target;
            spot.Length = length;
            spot.Across = length > 0.001
                ? new Vector(-run.Y / length * LaneOffset, run.X / length * LaneOffset)
                : new Vector(0, LaneOffset);
        }

        foreach (Spot leaf in _spots)
        {
            double journey = 0;

            foreach (Spot step in leaf.Trail)
            {
                journey += step.Length;
            }

            leaf.Journey = journey;
        }
    }

    private bool Flow(double seconds)
    {
        bool moving = false;
        _living.Clear();

        foreach (Spot leaf in _spots)
        {
            if (leaf.Trail.Length == 0
                || leaf.Branch.IsMuted
                || leaf.Branch.Item is not PeerNodeViewModel peer)
            {
                continue;
            }

            _living.Add(leaf.Branch.Id);

            Stream stream = StreamFor(leaf.Branch.Id);
            double outbound = Density(peer.SendRate);
            double inbound = Density(peer.ReceiveRate);

            moving |= Step(stream.Outbound, outbound, ref stream.OutboundCredit, seconds, Pace(leaf, outbound), _dots);
            moving |= Step(stream.Inbound, inbound, ref stream.InboundCredit, seconds, Pace(leaf, inbound), _dots);
        }

        Forget();
        return moving;
    }

    private static double Pace(Spot leaf, double density)
    {
        double pixels = SlowestFlow + (density / BusiestRate * (FastestFlow - SlowestFlow));
        double crossing = Math.Clamp(leaf.Journey / Math.Max(1, pixels), BriefestPath, LongestPath);

        return 1 / crossing;
    }

    private Stream StreamFor(string key)
    {
        if (!_streams.TryGetValue(key, out Stream? stream))
        {
            stream = new Stream();
            _streams[key] = stream;
        }

        return stream;
    }

    private void Forget()
    {
        if (_streams.Count == _living.Count)
        {
            return;
        }

        foreach (string key in _streams.Keys.ToArray())
        {
            if (!_living.Contains(key))
            {
                _streams.Remove(key);
            }
        }
    }

    private static bool Step(
        List<double> dots,
        double density,
        ref double credit,
        double seconds,
        double speed,
        int most)
    {
        for (int index = dots.Count - 1; index >= 0; index--)
        {
            dots[index] += speed * seconds;

            if (dots[index] > 1)
            {
                dots.RemoveAt(index);
            }
        }

        credit += density * seconds;

        while (credit >= 1)
        {
            credit -= 1;
            if (dots.Count < most)
            {
                dots.Add(0);
            }
        }

        return dots.Count > 0 || density > 0;
    }

    private static double Density(double bytesPerSecond) =>
        bytesPerSecond <= 0
            ? 0
            : Math.Clamp(Math.Log10(1 + (bytesPerSecond / 512)) * 4.4, 0.8, BusiestRate);

    private void Roads(DrawingContext dc)
    {
        if (_spots.Count < 2 || TrackBrush is not { } track)
        {
            return;
        }

        foreach (Spot spot in _spots)
        {
            if (spot.Parent is null || spot.Length <= 0)
            {
                continue;
            }

            bool muted = spot.Branch.IsMuted;
            double rate = spot.Branch.SendRate + spot.Branch.ReceiveRate;
            double weight = (muted ? 0.7 : 0.9 + (Density(rate) / BusiestRate * 2.1)) * Magnify;

            Brush stroke = muted
                ? QuietBrush ?? track
                : Lit(spot) ? AccentBrush ?? track : track;

            dc.DrawLine(Stroke(stroke, weight), spot.Origin, spot.Target);
        }
    }

    private void Traffic(DrawingContext dc)
    {
        if (!ShowSignals)
        {
            return;
        }
        double size = DotRadius * Magnify;
        double lane = Magnify;

        foreach (Spot leaf in _spots)
        {
            if (leaf.Trail.Length == 0
                || leaf.Branch.IsMuted
                || leaf.Journey <= 0
                || leaf.Branch.Item is not PeerNodeViewModel
                || !_streams.TryGetValue(leaf.Branch.Id, out Stream? stream))
            {
                continue;
            }

            if (SendBrush is { } send)
            {
                foreach (double progress in stream.Outbound)
                {
                    if (Along(leaf, progress, out Point at, out Vector across))
                    {
                        dc.DrawEllipse(send, null, at + (across * lane), size, size);
                    }
                }
            }

            if (ReceiveBrush is not { } receive)
            {
                continue;
            }

            foreach (double progress in stream.Inbound)
            {
                if (Along(leaf, 1 - progress, out Point at, out Vector across))
                {
                    dc.DrawEllipse(receive, null, at - (across * lane), size, size);
                }
            }
        }
    }

    private static bool Along(Spot leaf, double progress, out Point at, out Vector across)
    {
        double want = Math.Clamp(progress, 0, 1) * leaf.Journey;

        for (int index = 0; index < leaf.Trail.Length; index++)
        {
            Spot step = leaf.Trail[index];
            bool last = index == leaf.Trail.Length - 1;

            if (!last && want > step.Length)
            {
                want -= step.Length;
                continue;
            }

            double local = step.Length > 0.001 ? Math.Clamp(want / step.Length, 0, 1) : 0;

            at = On(step.Origin, step.Target, local);
            across = step.Across;
            return true;
        }

        at = default;
        across = default;
        return false;
    }

    private Pen Stroke(Brush brush, double weight)
    {
        int rounded = (int)Math.Round(weight * 10);
        int key = HashCode.Combine(brush, rounded);

        if (_pens.TryGetValue(key, out Pen? pen))
        {
            return pen;
        }

        pen = new Pen(brush, rounded / 10.0)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        pen.Freeze();
        _pens[key] = pen;

        return pen;
    }

    private static bool Lit(Spot spot) => spot.Branch.Item switch
    {
        ProcessNodeViewModel process => process.IsHighlighted || process.IsSelected,
        PeerNodeViewModel peer => peer.IsHighlighted || peer.IsSelected,
        HopNodeViewModel hop => hop.IsHighlighted || hop.IsSelected,
        _ => false,
    };

    private static Point Middle(Rect box) =>
        new(box.Left + (box.Width / 2), box.Top + (box.Height / 2));

    private static Point Rim(Rect box, Point towards)
    {
        Point centre = Middle(box);
        double run = towards.X - centre.X;
        double rise = towards.Y - centre.Y;

        if (Math.Abs(run) < 0.001 && Math.Abs(rise) < 0.001)
        {
            return centre;
        }
        double across = Math.Abs(run) > 0.001
            ? box.Width / 2 / Math.Abs(run)
            : double.PositiveInfinity;

        double down = Math.Abs(rise) > 0.001
            ? box.Height / 2 / Math.Abs(rise)
            : double.PositiveInfinity;

        double reach = Math.Min(across, down);

        return new Point(centre.X + (run * reach), centre.Y + (rise * reach));
    }

    private static Point On(Point start, Point end, double along) =>
        new(start.X + ((end.X - start.X) * along), start.Y + ((end.Y - start.Y) * along));

    private sealed class Spot(GraphBranch branch, Spot? parent, int depth)
    {
        public GraphBranch Branch { get; } = branch;

        public Spot? Parent { get; } = parent;

        public int Depth { get; } = depth;

        public List<Spot> Children { get; } = [];

        public Button? Node { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        /// <summary>Where it sits, measured from the hub.</summary>
        public Point At { get; set; }

        /// <summary>How far out the last thing hung off this one had to go.</summary>
        public int Ring { get; set; }

        /// <summary>Whether this pass has given it its place yet.</summary>
        public bool Settled { get; set; }

        /// <summary>Where it ended up on the canvas.</summary>
        public Rect Area { get; set; }

        /// <summary>How it is moving, which outlives this pass.</summary>
        public Motion Motion { get; init; } = new();

        /// <summary>How far from its place it is while it settles.</summary>
        public Vector Drift { get; set; }

        /// <summary>Where it is actually drawn, this frame.</summary>
        public Rect Seen => new(Area.X + Drift.X, Area.Y + Drift.Y, Area.Width, Area.Height);

        /// <summary>The nodes from the hub out to this one, itself last.</summary>
        public Spot[] Trail { get; set; } = [];

        /// <summary>How long the whole road to this far end is.</summary>
        public double Journey { get; set; }

        /// <summary>The edge leading here, as it stands this frame.</summary>
        public Point Origin { get; set; }

        public Point Target { get; set; }

        public double Length { get; set; }

        /// <summary>Which way is sideways along that edge.</summary>
        public Vector Across { get; set; }
    }

    private sealed class Motion
    {
        /// <summary>Where it is now, measured from the hub.</summary>
        public Point Shown;

        /// <summary>How fast it is going, so the spring has something to damp.</summary>
        public Vector Velocity;

        /// <summary>How far into arriving it is, from nothing to fully there.</summary>
        public double Grown;

        /// <summary>Whether it was last drawn standing back for another branch.</summary>
        public bool Muted;
    }

    private sealed class Ghost
    {
        public required Button Node { get; init; }

        /// <summary>Where it stood, measured from the hub.</summary>
        public required Point At { get; init; }

        /// <summary>How big it was.</summary>
        public required Size Size { get; init; }

        /// <summary>The offset it wore on top of that, at that moment.</summary>
        public required Vector From { get; init; }

        /// <summary>Towards whatever it hung off, which is where it collapses.</summary>
        public required Vector Toward { get; init; }

        /// <summary>How far through leaving it is.</summary>
        public double Gone { get; set; }
    }

    private sealed class EdgeLayer : FrameworkElement
    {
        private readonly DrawingVisual _land = new();
        private readonly DrawingVisual _roads = new();
        private readonly DrawingVisual _traffic = new();

        public EdgeLayer()
        {
            AddVisualChild(_land);
            AddVisualChild(_roads);
            AddVisualChild(_traffic);
        }

        protected override int VisualChildrenCount => 3;

        /// <summary>Replaces the earth, which changes least of all.</summary>
        public void Land(Action<DrawingContext> draw)
        {
            ArgumentNullException.ThrowIfNull(draw);

            using DrawingContext context = _land.RenderOpen();
            draw(context);
        }

        /// <summary>Replaces the roads, which only change when something moves.</summary>
        public void Roads(Action<DrawingContext> draw)
        {
            ArgumentNullException.ThrowIfNull(draw);

            using DrawingContext context = _roads.RenderOpen();
            draw(context);
        }

        /// <summary>Replaces the traffic, which changes every frame.</summary>
        public void Traffic(Action<DrawingContext> draw)
        {
            ArgumentNullException.ThrowIfNull(draw);

            using DrawingContext context = _traffic.RenderOpen();
            draw(context);
        }

        protected override Visual GetVisualChild(int index) => index switch
        {
            0 => _land,
            1 => _roads,
            _ => _traffic,
        };
    }

    private sealed class Stream
    {
        public readonly List<double> Outbound = [];
        public readonly List<double> Inbound = [];
        public double OutboundCredit;
        public double InboundCredit;
    }
}
