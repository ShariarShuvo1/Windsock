using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.App.Services;
using Windsock.Core.Formatting;
using Windsock.Core.Geography;
using Windsock.Core.Networking;
using Windsock.Core.Processes;

namespace Windsock.App.ViewModels;

/// <summary>
/// Backs one process monitor window: what a single process is doing, live.
/// </summary>
public sealed partial class ProcessMonitorViewModel : ObservableObject, IDisposable
{
    private const double ReadoutMaximumFontSize = 30;

    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(5);

    private const int Listed = 4;
    private readonly ProcessUsageMonitor _monitor;
    private readonly IProcessConnectionDetailSource _details;
    private readonly IProcessEndpointSource _endpoints;
    private readonly NetworkRouteCache _routes;
    private readonly HostNameCache _hosts;
    private readonly PlaceFinder _where;
    private readonly Dispatcher _dispatcher;
    private readonly ReadoutSizer _sendSizer = new(ReadoutMaximumFontSize);
    private readonly ReadoutSizer _receiveSizer = new(ReadoutMaximumFontSize);
    private readonly Dictionary<string, ConnectionRowViewModel> _rows = [];

    private readonly HashSet<string> _muted = new(StringComparer.Ordinal);

    private IReadOnlyList<ProcessConnection> _latest = [];
    private readonly Dictionary<string, DateTimeOffset> _closed = [];
    private readonly Dictionary<string, PeerNodeViewModel> _peers = new(StringComparer.Ordinal);

    private Dictionary<EndpointKey, EndpointTraffic> _traffic = [];
    private Dictionary<EndpointKey, EndpointTraffic> _earlier = [];

    private readonly Dictionary<string, HopNodeViewModel> _hops = new(StringComparer.Ordinal);

    private long _sightings;
    private object? _followed;
    private int _pending;
    private DateTimeOffset _measuredAt;
    private int _talking;
    private int _listening;
    private double _send;
    private double _receive;
    private long _sent;
    private long _received;
    private readonly string? _path;
    private bool _reading;
    private bool _missed;
    private IDisposable? _watch;
    private bool _disposed;

    public ProcessMonitorViewModel(
        int processId,
        ProcessIdentity identity,
        ProcessUsageMonitor monitor,
        IProcessConnectionDetailSource details,
        IProcessEndpointSource endpoints,
        NetworkRouteCache routes,
        HostNameCache hosts,
        ProcessIconCache icons,
        IpLocations? places,
        WorldMap? world,
        RateFamily family,
        RateScale scale,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(icons);

        ProcessId = processId;
        _monitor = monitor;
        _details = details;
        _endpoints = endpoints;
        _routes = routes;
        _hosts = hosts;
        _where = new PlaceFinder(places);
        World = world;
        _dispatcher = dispatcher;

        Name = identity.Name;
        Description = string.IsNullOrWhiteSpace(identity.Description) ? identity.Name : identity.Description!;
        _path = identity.FilePath;
        FilePath = identity.FilePath ?? "Path not readable";
        CanReveal = FileReveal.Exists(identity.FilePath);
        Icon = icons.Find(identity.FilePath);
        Families = [.. RateUnits.Families.Select(option => new FamilyOption(option, RateUnits.Name(option)))];
        Scales = [.. RateUnits.Scales.Select(option => new ScaleOption(option, RateUnits.Name(family, option)))];
        SelectedFamily = family;
        SelectedScale = scale;

        Started = identity.StartedAt is { } startedAt
            ? startedAt.ToLocalTime().ToString("d MMM yyyy, h:mm:ss tt", CultureInfo.CurrentCulture)
            : "—";

        Title = string.Format(CultureInfo.CurrentCulture, "{0} — {1}", Name, processId);
        Connections = [];
        Peers = [];

        HostColumn = Column.Text<ConnectionRowViewModel>("Remote", row => row.Host);
        RemotePortColumn = Column.Text<ConnectionRowViewModel>("Remote port", row => row.RemotePortText);
        LocalAddressColumn = Column.Text<ConnectionRowViewModel>("Local address", row => row.LocalAddress);
        LocalPortColumn = Column.Text<ConnectionRowViewModel>("Local port", row => row.LocalPortText);
        StateColumn = Column.Text<ConnectionRowViewModel>("State", row => row.State);
        ProtocolColumn = Column.Text<ConnectionRowViewModel>("Protocol", row => row.Protocol);
        VersionColumn = Column.Text<ConnectionRowViewModel>("IP", row => row.Version);
        OpenedColumn = Column.Text<ConnectionRowViewModel>("Opened", row => row.OpenedText);
        AgeColumn = Column.Text<ConnectionRowViewModel>("Age", row => row.Age);

        Columns =
        [
            HostColumn,
            RemotePortColumn,
            LocalAddressColumn,
            LocalPortColumn,
            StateColumn,
            ProtocolColumn,
            VersionColumn,
            OpenedColumn,
            AgeColumn,
        ];

        foreach (TableColumn column in Columns)
        {
            column.Changed += (_, _) =>
            {
                FilterChanged?.Invoke(this, EventArgs.Empty);
                OnPropertyChanged(nameof(IsCustomised));
            };
        }

        SendValue = "0";
        SendUnit = "B/s";
        ReceiveValue = "0";
        ReceiveUnit = "B/s";
        SendFontSize = ReadoutMaximumFontSize;
        ReceiveFontSize = ReadoutMaximumFontSize;
        SentTotal = "0 B";
        ReceivedTotal = "0 B";
        RateNotice = monitor.RateState == TrafficSourceState.Running
            ? string.Empty
            : monitor.RateDetail ?? string.Empty;
        _watch = _monitor.Watch();

        _monitor.Updated += OnUpdated;
        _hosts.Resolved += OnNamesResolved;
        _routes.RouteChanged += OnRouteFound;
        _endpoints.Watch(processId);

        Refresh();
    }

    /// <summary>The process being watched.</summary>
    public int ProcessId { get; }

    /// <summary>What to call the window.</summary>
    public string Title { get; }

    public string Name { get; }

    public string Description { get; }

    public string FilePath { get; }

    /// <summary>Whether the program is still where it said it was.</summary>
    public bool CanReveal { get; }

    /// <summary>The program's own icon, or null where Windows has none.</summary>
    public ImageSource? Icon { get; }

    /// <summary>Bytes or bits, for this window only.</summary>
    [ObservableProperty]
    public partial RateFamily SelectedFamily { get; set; }

    /// <summary>The magnitude to report in, for this window only.</summary>
    [ObservableProperty]
    public partial RateScale SelectedScale { get; set; }

    /// <summary>The families on offer.</summary>
    public IReadOnlyList<FamilyOption> Families { get; } = [];

    /// <summary>The scales on offer, relabelled as the family changes.</summary>
    public IReadOnlyList<ScaleOption> Scales { get; } = [];

    /// <summary>
    /// Whether a far end is called by its name where one is known.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowNames { get; set; } = true;

    /// <summary>Whether the graph section is showing.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    /// <summary>
    /// Whether the graph has the whole screen to itself.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDetachGraph))]
    public partial bool IsFullScreen { get; set; }

    /// <summary>
    /// Whether the graph may be taken out into a window of its own.
    /// </summary>
    public bool CanDetachGraph => !IsFullScreen;

    /// <summary>How many connections have been switched off the graph.</summary>
    [ObservableProperty]
    public partial int Hidden { get; private set; }

    [RelayCommand]
    private void ToggleConnection(ConnectionRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        row.IsShown = !row.IsShown;

        int hidden = 0;

        foreach (ConnectionRowViewModel each in Connections)
        {
            if (!each.IsShown)
            {
                hidden++;
            }
        }

        Hidden = hidden;
        SocketSummary = Summarise(_talking, _listening, hidden);
        Regroup(_latest);
    }

    [RelayCommand]
    private void ToggleFullScreen()
    {
        IsFullScreen = !IsFullScreen;

        if (IsFullScreen)
        {
            IsExpanded = true;
        }
    }

    public string Started { get; }

    /// <summary>Every socket the process holds open.</summary>
    public ObservableCollection<ConnectionRowViewModel> Connections { get; }

    /// <summary>
    /// The far ends being talked to, busiest first.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<PeerNodeViewModel> Peers { get; private set; } = [];

    /// <summary>
    /// The whole picture: the process, the routers its traffic passes through,
    /// and the far ends at the ends of the branches.
    /// </summary>
    [ObservableProperty]
    public partial GraphBranch? Graph { get; private set; }

    /// <summary>
    /// Whether the drawer holding the switches is open.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    /// <summary>The outline of the earth to draw the map on.</summary>
    public WorldMap? World { get; }

    /// <summary>Whether the world map can be offered at all.</summary>
    public bool HasWorld => World is not null && _where.CanLocate;

    /// <summary>
    /// Whether the picture is drawn on the earth rather than laid out freely.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MapCredit))]
    public partial bool IsWorld { get; set; }

    /// <summary>
    /// Who the map is by, said while it is on screen.
    /// </summary>
    public string MapCredit => IsWorld ? "DB-IP · Natural Earth" : string.Empty;

    /// <summary>Where this machine appears to be.</summary>
    [ObservableProperty]
    public partial WorldPlace? Home { get; private set; }

    /// <summary>What each country has on it, by country code.</summary>
    [ObservableProperty]
    public partial IReadOnlyDictionary<string, CountryNote> CountryNotes { get; private set; } =
        new Dictionary<string, CountryNote>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the signals travelling the roads are drawn.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowSignals { get; set; } = true;

    /// <summary>
    /// Whether the way to each far end is drawn, or only the far end.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowRoutes { get; set; } = true;

    /// <summary>Whether the countries with something on them are named.</summary>
    [ObservableProperty]
    public partial bool ShowLabels { get; set; }

    /// <summary>
    /// Whether every country is named, or only the ones in play.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowAllLabels { get; set; }

    /// <summary>Whether each country is shaded by how much is going there.</summary>
    [ObservableProperty]
    public partial bool ShowHeat { get; set; }

    /// <summary>Whether the reader is following one branch of the picture.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowEverythingCommand))]
    public partial bool IsFollowing { get; private set; }

    /// <summary>What the graph is showing, written out.</summary>
    [ObservableProperty]
    public partial string PeerSummary { get; private set; } = "Not talking to anything";

    /// <summary>What clicking the process in the middle of the graph will do.</summary>
    [ObservableProperty]
    public partial string GraphHint { get; private set; } = string.Empty;

    /// <summary>The table's columns, each owning its own filter.</summary>
    public IReadOnlyList<TableColumn<ConnectionRowViewModel>> Columns { get; }

    public TableColumn<ConnectionRowViewModel> HostColumn { get; }

    public TableColumn<ConnectionRowViewModel> RemotePortColumn { get; }

    public TableColumn<ConnectionRowViewModel> LocalAddressColumn { get; }

    public TableColumn<ConnectionRowViewModel> LocalPortColumn { get; }

    public TableColumn<ConnectionRowViewModel> StateColumn { get; }

    public TableColumn<ConnectionRowViewModel> ProtocolColumn { get; }

    public TableColumn<ConnectionRowViewModel> VersionColumn { get; }

    public TableColumn<ConnectionRowViewModel> OpenedColumn { get; }

    public TableColumn<ConnectionRowViewModel> AgeColumn { get; }

    /// <summary>Raised when a column filter changes and the view needs refreshing.</summary>
    public event EventHandler? FilterChanged;

    /// <summary>Raised when the view should drop its own sort.</summary>
    public event EventHandler? ViewReset;

    /// <summary>Whether a filter or a sort is narrowing what the table shows.</summary>
    public bool IsCustomised => Columns.Any(column => !column.IsDefault) || IsSorted;

    /// <summary>Set by the view when the grid is sorting by something.</summary>
    public bool IsSorted
    {
        get;

        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged(nameof(IsCustomised));
        }
    }

    /// <summary>Whether a row survives every column's filter.</summary>
    public bool Matches(ConnectionRowViewModel row) =>
        Columns.All(column => column.Matches(row));

    [RelayCommand]
    private void ResetView()
    {
        foreach (TableColumn column in Columns)
        {
            column.Reset();
        }

        ViewReset?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(IsCustomised));
    }

    /// <summary>
    /// Whether the window is following the process or holding still.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveLabel))]
    public partial bool IsLive { get; set; } = true;

    /// <summary>
    /// What the button offers to do, which is the opposite of what is
    /// happening.
    /// </summary>
    public string LiveLabel => IsLive ? "Pause" : "Resume";

    [RelayCommand]
    private void ToggleLive() => IsLive = !IsLive;

    [ObservableProperty]
    public partial string SendValue { get; private set; }

    [ObservableProperty]
    public partial string SendUnit { get; private set; }

    [ObservableProperty]
    public partial double SendFontSize { get; private set; }

    [ObservableProperty]
    public partial string ReceiveValue { get; private set; }

    [ObservableProperty]
    public partial string ReceiveUnit { get; private set; }

    [ObservableProperty]
    public partial double ReceiveFontSize { get; private set; }

    [ObservableProperty]
    public partial string SentTotal { get; private set; }

    [ObservableProperty]
    public partial string ReceivedTotal { get; private set; }

    /// <summary>Outbound rate, for the flow drawing.</summary>
    [ObservableProperty]
    public partial double SendRate { get; private set; }

    /// <summary>Inbound rate, for the flow drawing.</summary>
    [ObservableProperty]
    public partial double ReceiveRate { get; private set; }

    /// <summary>How many sockets are open, written out.</summary>
    [ObservableProperty]
    public partial string SocketSummary { get; private set; } = "No open sockets";

    /// <summary>Said when the process has gone, or when rates are unavailable.</summary>
    [ObservableProperty]
    public partial string RateNotice { get; private set; }

    /// <summary>Whether the process is still running.</summary>
    [ObservableProperty]
    public partial bool HasExited { get; private set; }

    private void OnUpdated(object? sender, IReadOnlyList<ProcessUsage> rows)
    {
        if (!IsLive || _disposed)
        {
            return;
        }

        ProcessUsage? mine = null;

        foreach (ProcessUsage row in rows)
        {
            if (row.ProcessId == ProcessId)
            {
                mine = row;
                break;
            }
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Apply(mine)));
    }

    private void OnNamesResolved(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!IsLive)
            {
                _missed = true;
                return;
            }

            Rename();
        }));

    /// <summary>
    /// Acts on a node being clicked in the graph.
    /// </summary>
    public void Choose(object? item)
    {
        object? next = item is PeerNodeViewModel or HopNodeViewModel && !ReferenceEquals(item, _followed)
            ? item
            : null;

        if (ReferenceEquals(next, _followed))
        {
            return;
        }

        _followed = next;
        Redraw();
    }

    [RelayCommand(CanExecute = nameof(IsFollowing))]
    private void ShowEverything()
    {
        _followed = null;
        Redraw();
    }

    /// <summary>Lights everything to do with one peer, from the table's side.</summary>
    public void HighlightPeer(string? key) => Light(key is null ? null : [key]);

    /// <summary>Lights everything to do with one node, from the graph's side.</summary>
    public void HighlightRows(object? item) => Light(item switch
    {
        PeerNodeViewModel peer => peer.Reach,
        HopNodeViewModel hop => hop.Reach,
        _ => null,
    });

    private void Light(IReadOnlyList<string>? reach)
    {
        HashSet<string>? lit = reach is null || reach.Count == 0
            ? null
            : new HashSet<string>(reach, StringComparer.Ordinal);

        foreach (ConnectionRowViewModel row in Connections)
        {
            row.IsHighlighted = lit is not null && lit.Contains(row.PeerKey);
        }

        foreach (PeerNodeViewModel peer in Peers)
        {
            peer.IsHighlighted = lit is not null && lit.Contains(peer.Key);
        }

        foreach (HopNodeViewModel hop in _hops.Values)
        {
            hop.IsHighlighted = lit is not null && Crosses(hop.Reach, lit);
        }
    }

    private static bool Crosses(IReadOnlyList<string> reach, HashSet<string> lit)
    {
        foreach (string key in reach)
        {
            if (lit.Contains(key))
            {
                return true;
            }
        }

        return false;
    }

    private void Regraph()
    {
        List<string> wanted = [];

        foreach (PeerNodeViewModel peer in Peers)
        {
            foreach (string address in peer.Probes)
            {
                wanted.Add(address);
            }
        }

        _routes.Want(this, wanted);
        Redraw();
    }

    private void Redraw()
    {
        if (_disposed)
        {
            return;
        }
        List<PeerNodeViewModel> ordered = [.. Peers];
        ordered.Sort(static (left, right) => left.Seen.CompareTo(right.Seen));

        List<RouteLeaf> leaves = new(ordered.Count);
        List<IReadOnlyList<PathHop>> known = [];

        foreach (PeerNodeViewModel peer in ordered)
        {
            known.Clear();

            int walkable = 0;
            int silent = 0;

            foreach (string address in peer.Probes)
            {
                if (!PathTrace.Walkable(address))
                {
                    continue;
                }

                walkable++;

                if (_routes.Route(address) is not { } route)
                {
                    continue;
                }

                if (route.Hops.Count > 0)
                {
                    known.Add(route.Hops);
                }
                else if (route.State == RouteState.Unreachable)
                {
                    silent++;
                }
            }
            peer.Distance = walkable > 0 && silent == walkable
                ? "The route there is not visible from here"
                : string.Empty;
            leaves.Add(new RouteLeaf(peer.Key, ShowRoutes ? RouteTree.Shared(known) : []));
        }

        RouteNode root = RouteTree.Build(leaves);

        Survey found = _where.Look(ordered, [root]);
        Home = found.Home;
        CountryNotes = found.Notes;

        HashSet<string>? lit = Following(root);
        HashSet<string> seen = new(StringComparer.Ordinal);

        Graph = Wrap(root, lit, seen);
        IsFollowing = lit is not null;

        foreach (string gone in _hops.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            _hops.Remove(gone);
        }
        string places = ordered.Count switch
        {
            0 => string.Empty,
            1 => "Talking to 1 place",
            int many => string.Format(CultureInfo.CurrentCulture, "Talking to {0:N0} places", many),
        };

        PeerSummary = lit is null || places.Length == 0
            ? places
            : string.Concat(places, ", following one branch");

        GraphHint = lit is null ? string.Empty : "Click here to show the whole picture again.";
    }

    private GraphBranch Wrap(RouteNode node, HashSet<string>? lit, HashSet<string> seen)
    {
        List<GraphBranch> children = new(node.Children.Count);

        foreach (RouteNode child in node.Children)
        {
            children.Add(Wrap(child, lit, seen));
        }
        double send = 0;
        double receive = 0;
        bool measured = false;

        foreach (string key in node.Reach)
        {
            if (_peers.TryGetValue(key, out PeerNodeViewModel? beyond))
            {
                send += beyond.SendRate;
                receive += beyond.ReceiveRate;
                measured |= beyond.HasRates;
            }
        }

        bool muted = lit is not null && !lit.Contains(node.Id);
        object? item = node.Id.Length == 0 ? null : Dress(node, muted, seen);
        WorldPlace? place = item switch
        {
            PeerNodeViewModel peer => _where.Find(peer),
            HopNodeViewModel hop => _where.Find(hop.Address),
            _ => null,
        };

        return new GraphBranch(node.Id, item, children)
        {
            SendRate = send,
            ReceiveRate = receive,
            HasRates = measured,
            IsMuted = muted,
            Place = place,
        };
    }

    private object Dress(RouteNode node, bool muted, HashSet<string> seen)
    {
        if (node.LeafKey is { } key && _peers.TryGetValue(key, out PeerNodeViewModel? peer))
        {
            peer.IsSelected = ReferenceEquals(peer, _followed);
            peer.IsMuted = muted;
            peer.Reach = node.Reach;
            if (node.Address is { Length: > 0 })
            {
                peer.Distance = node.Distance == 1
                    ? "1 hop away"
                    : string.Format(CultureInfo.CurrentCulture, "{0:N0} hops away", node.Distance);

                peer.Latency = node.RoundTrip is { } arrival
                    ? string.Concat("Answers in ", Took(arrival))
                    : string.Empty;
            }
            else
            {
                peer.Latency = string.Empty;
            }

            peer.Hint = Prompt(ReferenceEquals(peer, _followed));
            peer.Where = PlaceFinder.Describe(_where.Find(peer), Home, node.RoundTrip);

            return peer;
        }

        if (!_hops.TryGetValue(node.Id, out HopNodeViewModel? hop))
        {
            hop = new HopNodeViewModel(node.Id, node.Address, node.Distance, node.Silences);
            _hops[node.Id] = hop;
        }

        seen.Add(node.Id);

        hop.IsSelected = ReferenceEquals(hop, _followed);
        hop.IsMuted = muted;
        hop.Reach = node.Reach;
        hop.Latency = node.RoundTrip is { } round ? Took(round) : string.Empty;
        hop.Hint = Prompt(ReferenceEquals(hop, _followed));

        hop.Carries = node.Reach.Count switch
        {
            0 => string.Empty,
            1 => "On the way to 1 place",
            int many => string.Format(CultureInfo.CurrentCulture, "On the way to {0:N0} places", many),
        };

        if (hop.Address.Length > 0)
        {
            hop.Name(Named(hop.Address), ShowNames);
            hop.Where = PlaceFinder.Describe(_where.Find(hop.Address), Home, node.RoundTrip);
        }

        return hop;
    }

    private string Prompt(bool followed) => followed
        ? "Click to show the whole picture again"
        : _followed is null ? "Click to follow this branch" : string.Empty;

    private HashSet<string>? Following(RouteNode root)
    {
        if (_followed is null)
        {
            return null;
        }

        List<RouteNode> path = [];
        if (!Path(root, path))
        {
            _followed = null;
            return null;
        }

        HashSet<string> lit = new(StringComparer.Ordinal);

        foreach (RouteNode step in path)
        {
            lit.Add(step.Id);
        }

        Beyond(path[^1], lit);
        return lit;
    }

    private bool Path(RouteNode node, List<RouteNode> path)
    {
        path.Add(node);

        if (Owns(node))
        {
            return true;
        }

        foreach (RouteNode child in node.Children)
        {
            if (Path(child, path))
            {
                return true;
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }

    private static void Beyond(RouteNode node, HashSet<string> lit)
    {
        lit.Add(node.Id);

        foreach (RouteNode child in node.Children)
        {
            Beyond(child, lit);
        }
    }

    private bool Owns(RouteNode node) => _followed switch
    {
        PeerNodeViewModel peer => node.LeafKey is { } key && string.Equals(key, peer.Key, StringComparison.Ordinal),
        HopNodeViewModel hop => string.Equals(node.Id, hop.Id, StringComparison.Ordinal),
        _ => false,
    };

    private void OnRouteFound(object? sender, NetworkRoute route)
    {
        if (_disposed || Interlocked.Exchange(ref _pending, 1) == 1)
        {
            return;
        }

        _dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                Interlocked.Exchange(ref _pending, 0);
                if (!IsLive)
                {
                    _missed = true;
                    return;
                }

                Redraw();
            }));
    }

    private static string Took(TimeSpan round) => round.TotalMilliseconds < 1
        ? "<1 ms"
        : string.Format(CultureInfo.CurrentCulture, "{0:N0} ms", round.TotalMilliseconds);

    private void Apply(ProcessUsage? usage)
    {
        if (_disposed || !IsLive)
        {
            return;
        }

        if (usage is { } row)
        {
            HasExited = false;
            SendRate = row.SendBytesPerSecond;
            ReceiveRate = row.ReceiveBytesPerSecond;

            _send = row.SendBytesPerSecond;
            _receive = row.ReceiveBytesPerSecond;
            _sent = row.BytesSent;
            _received = row.BytesReceived;

            Restate();
        }
        else
        {
            HasExited = true;
            SendRate = 0;
            ReceiveRate = 0;
            _send = 0;
            _receive = 0;
            Restate();
            RateNotice = "This process has exited. The last reading is still shown.";
        }

        Refresh();
    }

    private void Restate()
    {
        (string sendValue, string sendUnit) = RateFormatter.Split(_send, SelectedFamily, SelectedScale);
        (string receiveValue, string receiveUnit) = RateFormatter.Split(_receive, SelectedFamily, SelectedScale);

        SendValue = sendValue;
        SendUnit = sendUnit;
        SendFontSize = _sendSizer.Update(sendValue.Length);
        ReceiveValue = receiveValue;
        ReceiveUnit = receiveUnit;
        ReceiveFontSize = _receiveSizer.Update(receiveValue.Length);
        SentTotal = SizeFormatter.Format(_sent);
        ReceivedTotal = SizeFormatter.Format(_received);

        foreach (PeerNodeViewModel peer in Peers)
        {
            peer.RateText = peer.HasRates
                ? Rates(peer.ReceiveRate, peer.SendRate, SelectedFamily, SelectedScale)
                : peer.Sockets;
        }
    }

    private static string Rates(double receive, double send, RateFamily family, RateScale scale) =>
        string.Format(
            CultureInfo.CurrentCulture,
            "\u2193 {0}   \u2191 {1}",
            RateFormatter.Format(receive, family, scale),
            RateFormatter.Format(send, family, scale));

    partial void OnSelectedFamilyChanged(RateFamily value)
    {
        foreach (ScaleOption option in Scales)
        {
            option.Label = RateUnits.Name(value, option.Scale);
        }

        Restate();
    }

    partial void OnSelectedScaleChanged(RateScale value) => Restate();

    partial void OnIsWorldChanged(bool value) => Redraw();

    partial void OnShowRoutesChanged(bool value) => Redraw();

    partial void OnShowNamesChanged(bool value)
    {
        Rename();
        Refresh();
    }

    [RelayCommand]
    private void Reveal() => FileReveal.Show(_path);

    private string? Named(string address) => ShowNames ? _hosts.Find(address) : null;

    private void Refresh()
    {
        if (_reading || _disposed || HasExited)
        {
            return;
        }

        _reading = true;
        int processId = ProcessId;

        _ = Task.Run(() => _details.Read(processId)).ContinueWith(
            task =>
            {
                _reading = false;

                if (!_disposed && IsLive && task.IsCompletedSuccessfully)
                {
                    Merge(task.Result);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Merge(IReadOnlyList<ProcessConnection> connections)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        HashSet<string> seen = new(StringComparer.Ordinal);
        int listening = 0;
        int talking = 0;

        foreach (ProcessConnection connection in connections)
        {
            seen.Add(connection.Key);

            if (connection.IsListening)
            {
                listening++;
            }
            else
            {
                talking++;
            }

            if (_rows.TryGetValue(connection.Key, out ConnectionRowViewModel? existing))
            {
                existing.Update(connection);
                _closed.Remove(connection.Key);
            }
            else
            {
                ConnectionRowViewModel row = new(connection);
                row.Name(Named(connection.RemoteAddress ?? string.Empty), ShowNames);
                _rows[connection.Key] = row;
                Connections.Add(row);
            }
        }

        for (int i = Connections.Count - 1; i >= 0; i--)
        {
            ConnectionRowViewModel row = Connections[i];

            if (seen.Contains(row.Key))
            {
                continue;
            }

            if (!_closed.TryGetValue(row.Key, out DateTimeOffset went))
            {
                _closed[row.Key] = now;
                row.IsClosed = true;
                continue;
            }

            if (now - went >= Linger)
            {
                Connections.RemoveAt(i);
                _rows.Remove(row.Key);
                _closed.Remove(row.Key);
            }
        }

        _talking = talking;
        _listening = listening;
        _latest = connections;

        SocketSummary = Summarise(talking, listening, Hidden);
        Regroup(connections);
        Rename();
    }

    private void Regroup(IReadOnlyList<ProcessConnection> connections)
    {
        Dictionary<string, Draft> drafts = new(StringComparer.Ordinal);
        HashSet<string> speaking = new(StringComparer.Ordinal);

        _muted.Clear();

        foreach (ProcessConnection connection in connections)
        {
            if (connection.IsListening || connection.RemoteAddress is not { Length: > 0 } address)
            {
                continue;
            }
            if (_rows.TryGetValue(connection.Key, out ConnectionRowViewModel? off) && !off.IsShown)
            {
                _muted.Add(address);
                continue;
            }

            speaking.Add(address);

            (string key, Draft draft) = For(drafts, address);

            draft.Sockets++;
            draft.Addresses.Add(address);
            draft.Ports.Add(connection.RemotePort);

            if (_rows.TryGetValue(connection.Key, out ConnectionRowViewModel? row))
            {
                row.PeerKey = key;
                draft.States.Add(row.State);
            }
        }

        _muted.ExceptWith(speaking);

        Measure(drafts);
        Publish(drafts);
    }

    private (string Key, Draft Draft) For(Dictionary<string, Draft> drafts, string address)
    {
        string? name = Named(address);
        string key = DomainName.Group(name ?? address);

        if (key.Length == 0)
        {
            key = address;
        }

        if (!drafts.TryGetValue(key, out Draft? draft))
        {
            draft = new Draft();
            drafts[key] = draft;
        }

        return (key, draft);
    }

    private void Measure(Dictionary<string, Draft> drafts)
    {
        if (!_endpoints.CanAttribute)
        {
            return;
        }

        _endpoints.CopyEndpoints(ProcessId, _traffic);

        DateTimeOffset now = DateTimeOffset.Now;
        double seconds = (now - _measuredAt).TotalSeconds;
        _measuredAt = now;
        bool usable = seconds is > 0.2 and < 30;

        foreach ((EndpointKey key, EndpointTraffic traffic) in _traffic)
        {
            if (!key.HasAddress)
            {
                continue;
            }

            string address = key.Address();

            if (_muted.Contains(address))
            {
                continue;
            }

            (_, Draft draft) = For(drafts, address);

            draft.Addresses.Add(address);
            draft.Ports.Add(key.Port);
            draft.Sent += traffic.BytesSent;
            draft.Received += traffic.BytesReceived;
            draft.Measured = true;

            if (!usable || !_earlier.TryGetValue(key, out EndpointTraffic before))
            {
                continue;
            }

            draft.Send += Math.Max(0, traffic.BytesSent - before.BytesSent) / seconds;
            draft.Receive += Math.Max(0, traffic.BytesReceived - before.BytesReceived) / seconds;
        }

        (_earlier, _traffic) = (_traffic, _earlier);
    }

    private void Publish(Dictionary<string, Draft> drafts)
    {
        List<PeerNodeViewModel> nodes = new(drafts.Count);

        foreach ((string key, Draft draft) in drafts)
        {
            if (!_peers.TryGetValue(key, out PeerNodeViewModel? peer))
            {
                peer = new PeerNodeViewModel(key, ++_sightings);
                _peers[key] = peer;
            }

            draft.Apply(peer, SelectedFamily, SelectedScale);
            nodes.Add(peer);
        }

        foreach (string gone in _peers.Keys.Where(key => !drafts.ContainsKey(key)).ToArray())
        {
            _peers.Remove(gone);
        }
        nodes.Sort(static (left, right) =>
        {
            int byWeight = right.Weight.CompareTo(left.Weight);
            return byWeight != 0 ? byWeight : string.CompareOrdinal(left.Label, right.Label);
        });

        Peers = nodes;
        Regraph();
    }

    private static string Summarise(int talking, int listening, int hidden)
    {
        if (talking == 0 && listening == 0)
        {
            return "No open sockets";
        }

        string counted = string.Format(
            CultureInfo.CurrentCulture,
            "{0:N0} connected, {1:N0} listening",
            talking,
            listening);
        return hidden == 0
            ? counted
            : string.Format(
                CultureInfo.CurrentCulture,
                "{0}, {1:N0} off the graph",
                counted,
                hidden);
    }

    private void Rename()
    {
        if (_disposed)
        {
            return;
        }

        foreach (ConnectionRowViewModel row in Connections)
        {
            if (row.RemoteAddress.Length > 0)
            {
                row.Name(Named(row.RemoteAddress), ShowNames);
            }
        }

        foreach (HopNodeViewModel hop in _hops.Values)
        {
            if (hop.Address.Length > 0)
            {
                hop.Name(Named(hop.Address), ShowNames);
            }
        }
    }

    partial void OnIsLiveChanged(bool value)
    {
        if (!value)
        {
            return;
        }
        if (_missed)
        {
            _missed = false;
            Rename();
        }

        Refresh();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _monitor.Updated -= OnUpdated;
        _hosts.Resolved -= OnNamesResolved;
        _routes.RouteChanged -= OnRouteFound;

        _watch?.Dispose();
        _watch = null;
        _routes.Release(this);
        _endpoints.Unwatch(ProcessId);
    }

    private sealed class Draft
    {
        public readonly SortedSet<string> Addresses = new(StringComparer.Ordinal);
        public readonly SortedSet<int> Ports = [];
        public readonly SortedSet<string> States = new(StringComparer.Ordinal);

        public double Send;
        public double Receive;
        public long Sent;
        public long Received;
        public int Sockets;
        public bool Measured;

        public void Apply(PeerNodeViewModel peer, RateFamily family, RateScale scale)
        {
            peer.SendRate = Send;
            peer.ReceiveRate = Receive;
            peer.HasRates = Measured;
            peer.Probes = [.. Addresses];
            peer.Weight = Measured ? Send + Receive + (Sockets * 0.001) : Sockets;

            peer.Sockets = Sockets switch
            {
                0 => "No open connection",
                1 => "1 connection",
                int many => string.Format(CultureInfo.CurrentCulture, "{0:N0} connections", many),
            };

            peer.Addresses = Label(Addresses.Count, "Address", "Addresses", Join(Addresses));
            peer.Ports = Label(Ports.Count, "Port", "Ports", Join(Ports.Select(PortNames.Describe)));
            peer.States = string.Join(", ", States);

            peer.Totals = Measured && Sent + Received > 0
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} in and {1} out since this window opened",
                    SizeFormatter.Format(Received),
                    SizeFormatter.Format(Sent))
                : string.Empty;

            peer.RateText = Measured ? Rates(Receive, Send, family, scale) : peer.Sockets;
        }

        private static string Label(int count, string one, string many, string values) => count switch
        {
            0 => string.Empty,
            1 => string.Concat(one, " ", values),
            _ => string.Concat(many, " ", values),
        };

        private static string Join(IEnumerable<string> values)
        {
            List<string> all = [.. values];

            return all.Count <= Listed
                ? string.Join(", ", all)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} and {1:N0} more",
                    string.Join(", ", all.Take(Listed)),
                    all.Count - Listed);
        }
    }
}
