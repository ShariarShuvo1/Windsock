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
/// Everything this machine is doing on the network, in one picture.
/// </summary>
public sealed partial class NetworkMapViewModel : ObservableObject, IDisposable
{
    private const int Listed = 4;
    private const int Shown = 8;
    private readonly ProcessUsageMonitor _monitor;
    private readonly IProcessConnectionDetailSource _details;
    private readonly IProcessEndpointSource _endpoints;
    private readonly NetworkRouteCache _routes;
    private readonly HostNameCache _hosts;
    private readonly ProcessIconCache _icons;
    private readonly PlaceFinder _where;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, Program> _programs = [];
    private readonly List<Program> _ordered = [];
    private HashSet<int>? _only;

    private readonly Dictionary<string, PeerNodeViewModel> _abroad = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HopNodeViewModel> _waypoints = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Together> _together = new(StringComparer.Ordinal);

    private IReadOnlyList<ProcessConnection> _sockets = [];

    private LoopbackEnds _ends = new([]);

    private object? _followed;
    private long _sightings;
    private int _pending;
    private bool _reading;
    private IDisposable? _watch;
    private bool _disposed;

    public NetworkMapViewModel(
        ProcessUsageMonitor monitor,
        IProcessConnectionDetailSource details,
        IProcessEndpointSource endpoints,
        NetworkRouteCache routes,
        HostNameCache hosts,
        ProcessIconCache icons,
        IpLocations? places,
        WorldMap? world,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(icons);

        _monitor = monitor;
        _details = details;
        _endpoints = endpoints;
        _routes = routes;
        _hosts = hosts;
        _icons = icons;
        _where = new PlaceFinder(places);
        World = world;
        _dispatcher = dispatcher;

        Caption = Environment.MachineName;
        _watch = _monitor.Watch();

        _monitor.Updated += OnUpdated;
        _hosts.Resolved += OnNamesResolved;
        _routes.RouteChanged += OnRouteFound;

        Refresh();
    }

    /// <summary>What to call the middle of the map.</summary>
    public string Caption { get; }

    private bool _missed;

    /// <summary>The whole picture: this machine, and everything beyond it.</summary>
    [ObservableProperty]
    public partial GraphBranch? Graph { get; private set; }

    /// <summary>What the map is showing, in one line.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>The line under the machine's name, on the node itself.</summary>
    [ObservableProperty]
    public partial string Subtitle { get; private set; } = "Nothing is talking yet";

    /// <summary>What clicking the machine in the middle will do.</summary>
    [ObservableProperty]
    public partial string Hint { get; private set; } = string.Empty;

    /// <summary>Whether the reader is following one branch of the map.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowEverythingCommand))]
    public partial bool IsFollowing { get; private set; }

    /// <summary>
    /// Whether the drawer holding the switches is open.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    /// <summary>
    /// Whether the map has the whole window to itself.
    /// </summary>
    [ObservableProperty]
    public partial bool IsFullScreen { get; set; }

    [RelayCommand]
    private void ToggleFullScreen() => IsFullScreen = !IsFullScreen;

    /// <summary>Whether the picture keeps up with the machine.</summary>
    [ObservableProperty]
    public partial bool IsLive { get; set; } = true;

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

    /// <summary>Whether a far end is called by its name where one is known.</summary>
    [ObservableProperty]
    public partial bool ShowNames { get; set; } = true;

    /// <summary>
    /// Whether programs talking to this machine itself are drawn.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowLoopback { get; set; } = true;

    /// <summary>
    /// Whether the picture is drawn on the earth rather than laid out freely.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MapCredit))]
    public partial bool IsWorld { get; set; }

    /// <summary>The outline of the earth to draw the map on.</summary>
    public WorldMap? World { get; }

    /// <summary>Whether the world map can be offered at all.</summary>
    public bool HasWorld => World is not null && _where.CanLocate;

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

    /// <summary>Said when nothing can be broken down by far end.</summary>
    public string RateNotice => _endpoints.CanAttribute
        ? string.Empty
        : "Rates are per program. Run Windsock as administrator to see them per site.";

    /// <summary>
    /// Acts on a node being clicked.
    /// </summary>
    public void Choose(object? item)
    {
        object? next = item is ProcessNodeViewModel or PeerNodeViewModel or HopNodeViewModel
            && !ReferenceEquals(item, _followed)
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

    /// <summary>
    /// Lights everything that has to do with the node under the pointer.
    /// </summary>
    public void Highlight(object? item)
    {
        Program? only = item is ProcessNodeViewModel process
            ? _programs.GetValueOrDefault(process.ProcessId)
            : null;

        IReadOnlyList<string>? reach = item switch
        {
            ProcessNodeViewModel node => node.Reach,
            PeerNodeViewModel peer => peer.Reach,
            HopNodeViewModel hop => hop.Reach,
            _ => null,
        };

        HashSet<string>? lit = reach is null || reach.Count == 0
            ? null
            : new HashSet<string>(reach, StringComparer.Ordinal);
        foreach (PeerNodeViewModel peer in _abroad.Values)
        {
            peer.IsHighlighted = lit is not null && only is null && lit.Contains(peer.Key);
        }

        foreach (HopNodeViewModel hop in _waypoints.Values)
        {
            hop.IsHighlighted = lit is not null && only is null && Crosses(hop.Reach, lit);
        }

        foreach (Program program in _ordered)
        {
            bool inside = only is null || ReferenceEquals(program, only);

            program.Node.IsHighlighted = lit is not null && inside && Crosses(program.Node.Reach, lit);

            foreach (PeerNodeViewModel peer in program.Peers.Values)
            {
                peer.IsHighlighted = lit is not null && inside && lit.Contains(peer.Key);
            }

            foreach (HopNodeViewModel hop in program.Hops.Values)
            {
                hop.IsHighlighted = lit is not null && inside && Crosses(hop.Reach, lit);
            }
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

    private void OnUpdated(object? sender, IReadOnlyList<ProcessUsage> usage) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Apply(usage)));

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

    private void Refresh()
    {
        if (_reading || _disposed)
        {
            return;
        }

        _reading = true;

        _ = Task.Run(_details.ReadAll).ContinueWith(
            read =>
            {
                _reading = false;

                if (_disposed || !IsLive || !read.IsCompletedSuccessfully)
                {
                    return;
                }

                _sockets = read.Result;
                Regroup();
                Regraph();
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Apply(IReadOnlyList<ProcessUsage> usage)
    {
        if (_disposed || !IsLive)
        {
            return;
        }

        HashSet<int> here = [];

        foreach (ProcessUsage each in usage)
        {
            if (each.Connections == 0)
            {
                continue;
            }

            here.Add(each.ProcessId);

            if (!_programs.TryGetValue(each.ProcessId, out Program? program))
            {
                program = new Program(each.ProcessId, each.Name, ++_sightings)
                {
                    MeasuredAt = DateTimeOffset.Now,
                };
                _endpoints.Watch(each.ProcessId);

                _programs[each.ProcessId] = program;
                _ordered.Add(program);
            }
            program.Node.FilePath = each.Identity.FilePath ?? string.Empty;
            program.Node.Icon ??= _icons.Find(each.Identity.FilePath);

            program.Node.SendRate = each.SendBytesPerSecond;
            program.Node.ReceiveRate = each.ReceiveBytesPerSecond;
            program.Node.Sockets = Count(each.Connections, "connection", "connections");
            program.Node.RateText = Rates(each.ReceiveBytesPerSecond, each.SendBytesPerSecond);
        }

        for (int which = _ordered.Count - 1; which >= 0; which--)
        {
            Program program = _ordered[which];

            if (here.Contains(program.ProcessId))
            {
                continue;
            }

            _endpoints.Unwatch(program.ProcessId);
            _programs.Remove(program.ProcessId);
            _ordered.RemoveAt(which);
        }

        Refresh();
    }

    private void Regroup()
    {
        foreach (Program program in _ordered)
        {
            program.Drafts.Clear();
        }

        _ends = new LoopbackEnds(_sockets);

        foreach (ProcessConnection socket in _sockets)
        {
            if (socket.IsListening
                || socket.RemoteAddress is not { Length: > 0 } address
                || !_programs.TryGetValue(socket.ProcessId, out Program? program))
            {
                continue;
            }

            bool here = LoopbackEnds.IsLoopback(address);

            if (here && !ShowLoopback)
            {
                continue;
            }

            Draft draft = here
                ? Near(program, address, _ends.Owner(socket.Protocol, socket.IsIpv6, socket.RemotePort))
                : For(program, address);

            draft.Sockets++;
            draft.Addresses.Add(address);
            draft.Ports.Add(socket.RemotePort);
        }

        foreach (Program program in _ordered)
        {
            Measure(program);
            Publish(program);
        }
    }

    private void Measure(Program program)
    {
        if (!_endpoints.CanAttribute)
        {
            return;
        }

        _endpoints.CopyEndpoints(program.ProcessId, program.Traffic);

        DateTimeOffset now = DateTimeOffset.Now;
        double seconds = (now - program.MeasuredAt).TotalSeconds;
        program.MeasuredAt = now;
        bool usable = seconds is > 0.2 and < 30;

        foreach ((EndpointKey key, EndpointTraffic traffic) in program.Traffic)
        {
            if (!key.HasAddress)
            {
                continue;
            }

            string address = key.Address();
            bool here = LoopbackEnds.IsLoopback(address);

            if (here && !ShowLoopback)
            {
                continue;
            }
            Draft draft = here
                ? Near(program, address, _ends.Owner(key.IsIpv6, key.Port))
                : For(program, address);

            draft.Addresses.Add(address);
            draft.Ports.Add(key.Port);
            draft.Measured = true;

            if (!usable || !program.Earlier.TryGetValue(key, out EndpointTraffic before))
            {
                continue;
            }

            draft.Send += Math.Max(0, traffic.BytesSent - before.BytesSent) / seconds;
            draft.Receive += Math.Max(0, traffic.BytesReceived - before.BytesReceived) / seconds;
        }

        (program.Earlier, program.Traffic) = (program.Traffic, program.Earlier);
    }

    private Draft For(Program program, string address)
    {
        string? name = ShowNames ? _hosts.Find(address) : null;
        string key = DomainName.Group(name ?? address);

        if (key.Length == 0)
        {
            key = address;
        }

        if (!program.Drafts.TryGetValue(key, out Draft? draft))
        {
            draft = new Draft();
            program.Drafts[key] = draft;
        }

        return draft;
    }

    private Draft Near(Program program, string address, int? far)
    {
        string key = far is { } who
            ? string.Create(CultureInfo.InvariantCulture, $"local:{who}")
            : address;

        if (!program.Drafts.TryGetValue(key, out Draft? draft))
        {
            draft = new Draft { IsLocal = true };
            program.Drafts[key] = draft;
        }
        draft.Name = far is { } pid ? Called(pid) : address;

        return draft;
    }

    private string Called(int processId) =>
        _programs.TryGetValue(processId, out Program? program)
            ? program.Node.Name
            : string.Create(CultureInfo.InvariantCulture, $"process {processId}");

    private void Publish(Program program)
    {
        foreach ((string key, Draft draft) in program.Drafts)
        {
            if (!program.Peers.TryGetValue(key, out PeerNodeViewModel? peer))
            {
                peer = new PeerNodeViewModel(key, ++_sightings);
                program.Peers[key] = peer;
            }

            draft.Apply(peer);
        }

        foreach (string gone in program.Peers.Keys.Where(key => !program.Drafts.ContainsKey(key)).ToArray())
        {
            program.Peers.Remove(gone);
        }
    }

    private void Regraph()
    {
        List<string> wanted = [];

        foreach (Program program in _ordered)
        {
            foreach (PeerNodeViewModel peer in program.Peers.Values)
            {
                foreach (string address in peer.Probes)
                {
                    wanted.Add(address);
                }
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

        if (IsWorld)
        {
            Chart();
            return;
        }

        List<GraphBranch> branches = new(_ordered.Count);
        List<(Program Program, RouteNode Root)> built = new(_ordered.Count);
        int places = 0;

        foreach (Program program in _ordered)
        {
            if (!Drawn(program))
            {
                continue;
            }
            if (!ShowLoopback && program.Peers.Count == 0)
            {
                continue;
            }

            built.Add((program, Grow(program)));
            places += program.Peers.Count;
        }

        Locate(built);

        HashSet<string>? lit = _followed is null ? null : Following(built);
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach ((Program program, RouteNode root) in built)
        {
            branches.Add(Trunk(program, root, lit, seen));
        }

        foreach (Program program in _ordered)
        {
            foreach (string id in program.Hops.Keys.Where(key => !seen.Contains(key)).ToArray())
            {
                program.Hops.Remove(id);
            }
        }

        Graph = new GraphBranch(string.Empty, null, branches);
        IsFollowing = lit is not null;

        Subtitle = built.Count == 0
            ? Narrowed ? "Nothing the table is showing" : "Nothing is talking yet"
            : string.Concat(
                Narrowed
                    ? string.Concat(Count(built.Count, "program", "programs"), " the table is showing")
                    : Count(built.Count, "program", "programs"),
                ", ",
                Count(places, "place", "places"));

        Summary = built.Count == 0
            ? Narrowed
                ? "Nothing the table is showing has anything to draw. Clear the search and the filters above, or turn off Follow the table."
                : ShowLoopback
                    ? "Nothing on this machine has a connection open"
                    : "Nothing on this machine is talking past it"
            : lit is null
                ? string.Concat(
                    ShowLoopback ? "Everything on this machine: " : "Leaving this machine: ",
                    Subtitle)
                : string.Concat("Following one branch of ", Subtitle);

        Hint = lit is null ? string.Empty : "Click here to show the whole map again.";
    }

    private void Chart()
    {
        Gather();

        List<Together> ordered = [.. _together.Values];
        ordered.Sort(static (left, right) => left.Seen.CompareTo(right.Seen));

        List<RouteLeaf> leaves = new(ordered.Count);
        List<IReadOnlyList<PathHop>> known = [];

        foreach (Together end in ordered)
        {
            known.Clear();
            bool walkable = false;
            bool silent = true;

            foreach (string address in end.Addresses)
            {
                if (!PathTrace.Walkable(address))
                {
                    continue;
                }

                walkable = true;

                if (_routes.Route(address) is not { } route)
                {
                    silent = false;
                    continue;
                }

                if (route.Hops.Count > 0)
                {
                    known.Add(route.Hops);
                    silent = false;
                }
                else if (route.State != RouteState.Unreachable)
                {
                    silent = false;
                }
            }

            end.Unreachable = walkable && silent;
            leaves.Add(new RouteLeaf(end.Key, ShowRoutes ? RouteTree.Shared(known) : []));
        }

        RouteNode root = RouteTree.Build(leaves);

        Survey found = _where.Look(Abroad(ordered), [root]);
        Home = found.Home;
        CountryNotes = found.Notes;

        HashSet<string>? lit = _followed is null ? null : Trace(root);
        HashSet<string> seen = new(StringComparer.Ordinal);

        List<GraphBranch> branches = new(root.Children.Count);

        foreach (RouteNode child in root.Children)
        {
            branches.Add(Chart(child, lit, seen));
        }

        foreach (string id in _waypoints.Keys.Where(key => !seen.Contains(key)).ToArray())
        {
            _waypoints.Remove(id);
        }

        foreach (string key in _abroad.Keys.Where(key => !_together.ContainsKey(key)).ToArray())
        {
            _abroad.Remove(key);
        }

        Graph = new GraphBranch(string.Empty, null, branches);
        IsFollowing = lit is not null;

        Subtitle = ordered.Count == 0
            ? Narrowed ? "Nothing the table is showing" : "Nothing is leaving this machine"
            : string.Concat(
                Programs(),
                ", ",
                Count(ordered.Count, "place", "places"));
        string abroad = Count(ordered.Count, "place", "places");
        string where = CountryNotes.Count == 0
            ? abroad
            : string.Format(
                CultureInfo.CurrentCulture,
                "{0} in {1}",
                abroad,
                Count(CountryNotes.Count, "country", "countries"));

        Summary = ordered.Count == 0
            ? Narrowed
                ? "Nothing the table is showing is talking past this machine. Clear the search and the filters above, or turn off Follow the table."
                : "Nothing on this machine is talking past it"
            : lit is null
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}, from {1}",
                    where,
                    Programs())
                : string.Concat("Following one branch of ", Subtitle);

        Hint = lit is null ? string.Empty : "Click here to show the whole map again.";
    }

    private void Gather()
    {
        _together.Clear();

        foreach (Program program in _ordered)
        {
            if (!Drawn(program))
            {
                continue;
            }

            foreach (PeerNodeViewModel peer in program.Peers.Values)
            {
                if (peer.IsLocal)
                {
                    continue;
                }
                string key = peer.Key;
                string? city = null;

                if (ByCity && _where.Find(peer) is { IsKnown: true } place)
                {
                    city = Named(place);
                    key = string.Concat("city:", city);
                }

                if (!_together.TryGetValue(key, out Together? end))
                {
                    end = new Together(city ?? peer.Key, peer.Seen);
                    _together[key] = end;
                }

                end.Add(program.Node.Name, program.Node.Icon, peer);
            }
        }
    }

    private static string Named(WorldPlace place) =>
        place.City.Length > 0 && place.Country.Length > 0
            ? string.Concat(place.City, ", ", place.Country)
            : place.City.Length > 0 ? place.City : place.Country;

    private IEnumerable<PeerNodeViewModel> Abroad(List<Together> ordered)
    {
        foreach (Together end in ordered)
        {
            yield return Node(end);
        }
    }

    private PeerNodeViewModel Node(Together end)
    {
        if (!_abroad.TryGetValue(end.Key, out PeerNodeViewModel? peer))
        {
            peer = new PeerNodeViewModel(end.Key, end.Seen);
            _abroad[end.Key] = peer;
        }

        end.Apply(peer);
        return peer;
    }

    private GraphBranch Chart(RouteNode node, HashSet<string>? lit, HashSet<string> seen)
    {
        List<GraphBranch> children = new(node.Children.Count);

        foreach (RouteNode child in node.Children)
        {
            children.Add(Chart(child, lit, seen));
        }
        double send = 0;
        double receive = 0;
        bool measured = false;

        foreach (string key in node.Reach)
        {
            if (_together.TryGetValue(key, out Together? beyond))
            {
                send += beyond.Send;
                receive += beyond.Receive;
                measured |= beyond.Measured;
            }
        }

        bool muted = lit is not null && !lit.Contains(node.Id);
        object item = Mark(node, muted, seen);

        return new GraphBranch(node.Id, item, children)
        {
            SendRate = send,
            ReceiveRate = receive,
            HasRates = measured,
            IsMuted = muted,
            Place = item switch
            {
                PeerNodeViewModel peer => _where.Find(peer),
                HopNodeViewModel hop => _where.Find(hop.Address),
                _ => null,
            },
        };
    }

    private object Mark(RouteNode node, bool muted, HashSet<string> seen)
    {
        if (node.LeafKey is { } key && _together.TryGetValue(key, out Together? end))
        {
            PeerNodeViewModel peer = Node(end);

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
                peer.Distance = end.Unreachable ? "The route there is not visible from here" : string.Empty;
                peer.Latency = string.Empty;
            }

            peer.Hint = Prompt(ReferenceEquals(peer, _followed));
            peer.Where = PlaceFinder.Describe(_where.Find(peer), Home, node.RoundTrip);

            return peer;
        }

        if (!_waypoints.TryGetValue(node.Id, out HopNodeViewModel? hop))
        {
            hop = new HopNodeViewModel(node.Id, node.Address, node.Distance, node.Silences);
            _waypoints[node.Id] = hop;
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
            hop.Name(ShowNames ? _hosts.Find(hop.Address) : null, ShowNames);
            hop.Where = PlaceFinder.Describe(_where.Find(hop.Address), Home, node.RoundTrip);
        }

        return hop;
    }

    private HashSet<string>? Trace(RouteNode root)
    {
        List<RouteNode> path = [];

        if (!Walk(root, path))
        {
            _followed = null;
            return null;
        }

        HashSet<string> lit = new(StringComparer.Ordinal) { string.Empty };

        foreach (RouteNode step in path)
        {
            lit.Add(step.Id);
        }

        Onward(path[^1], lit);
        return lit;
    }

    private bool Walk(RouteNode node, List<RouteNode> path)
    {
        path.Add(node);

        if (Chosen(node))
        {
            return true;
        }

        foreach (RouteNode child in node.Children)
        {
            if (Walk(child, path))
            {
                return true;
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }

    private static void Onward(RouteNode node, HashSet<string> lit)
    {
        lit.Add(node.Id);

        foreach (RouteNode child in node.Children)
        {
            Onward(child, lit);
        }
    }

    private bool Chosen(RouteNode node) => _followed switch
    {
        PeerNodeViewModel peer => node.LeafKey is { } key
            && _abroad.TryGetValue(key, out PeerNodeViewModel? mine)
            && ReferenceEquals(mine, peer),
        HopNodeViewModel hop => string.Equals(node.Id, hop.Id, StringComparison.Ordinal),
        _ => false,
    };

    private void Locate(List<(Program Program, RouteNode Root)> built)
    {
        Survey found = _where.Look(
            built.SelectMany(each => each.Program.Peers.Values),
            built.Select(each => each.Root));

        Home = found.Home;
        CountryNotes = found.Notes;
    }

    /// <summary>
    /// Whether every server in one city is drawn as a single dot.
    /// </summary>
    [ObservableProperty]
    public partial bool ByCity { get; set; }

    /// <summary>
    /// Whether the picture shows only what the table above it is showing.
    /// </summary>
    [ObservableProperty]
    public partial bool FollowTable { get; set; } = true;

    partial void OnFollowTableChanged(bool value) => Redraw();

    /// <summary>
    /// Tells the map which programs the table is showing.
    /// </summary>
    public void Follow(IReadOnlyCollection<int>? processes)
    {
        if (processes is null)
        {
            if (_only is null)
            {
                return;
            }

            _only = null;
        }
        else
        {
            HashSet<int> now = [.. processes];
            if (_only is not null && _only.SetEquals(now))
            {
                return;
            }

            _only = now;
        }

        if (FollowTable)
        {
            Redraw();
        }
    }

    private bool Narrowed => FollowTable && _only is not null;

    private string Programs()
    {
        int drawn = 0;

        foreach (Program each in _ordered)
        {
            if (Drawn(each))
            {
                drawn++;
            }
        }

        return Narrowed
            ? string.Concat(Count(drawn, "program", "programs"), " the table is showing")
            : Count(drawn, "program", "programs");
    }

    private bool Drawn(Program program) =>
        !FollowTable || _only is null || _only.Contains(program.ProcessId);

    partial void OnByCityChanged(bool value)
    {
        _abroad.Clear();
        Redraw();
    }

    partial void OnIsWorldChanged(bool value) => Redraw();

    partial void OnShowRoutesChanged(bool value) => Redraw();

    private RouteNode Grow(Program program)
    {
        List<PeerNodeViewModel> ordered = [.. program.Peers.Values];
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
            peer.Distance = peer.IsLocal
                ? "On this machine - nothing leaves the network card"
                : walkable > 0 && silent == walkable
                    ? "The route there is not visible from here"
                    : string.Empty;

            leaves.Add(new RouteLeaf(peer.Key, ShowRoutes ? RouteTree.Shared(known) : []));
        }

        return RouteTree.Build(leaves);
    }

    private GraphBranch Trunk(Program program, RouteNode root, HashSet<string>? lit, HashSet<string> seen)
    {
        List<GraphBranch> children = new(root.Children.Count);

        foreach (RouteNode child in root.Children)
        {
            children.Add(Wrap(program, child, lit, seen));
        }

        bool muted = lit is not null && !lit.Contains(program.Id);

        program.Node.IsSelected = ReferenceEquals(program.Node, _followed);
        program.Node.IsMuted = muted;
        program.Node.Reach = [.. program.Peers.Keys];
        program.Node.Places = Count(program.Peers.Count, "place", "places");
        program.Node.Hint = Prompt(ReferenceEquals(program.Node, _followed));

        return new GraphBranch(program.Id, program.Node, children)
        {
            SendRate = program.Node.SendRate,
            ReceiveRate = program.Node.ReceiveRate,
            HasRates = true,
            IsMuted = muted,
        };
    }

    private GraphBranch Wrap(Program program, RouteNode node, HashSet<string>? lit, HashSet<string> seen)
    {
        List<GraphBranch> children = new(node.Children.Count);

        foreach (RouteNode child in node.Children)
        {
            children.Add(Wrap(program, child, lit, seen));
        }

        double send = 0;
        double receive = 0;
        bool measured = false;

        foreach (string key in node.Reach)
        {
            if (program.Peers.TryGetValue(key, out PeerNodeViewModel? beyond))
            {
                send += beyond.SendRate;
                receive += beyond.ReceiveRate;
                measured |= beyond.HasRates;
            }
        }
        string id = program.Id + node.Id;
        bool muted = lit is not null && !lit.Contains(id);

        object item = Dress(program, node, muted, seen);
        WorldPlace? place = item switch
        {
            PeerNodeViewModel peer => _where.Find(peer),
            HopNodeViewModel hop => _where.Find(hop.Address),
            _ => null,
        };

        return new GraphBranch(id, item, children)
        {
            SendRate = send,
            ReceiveRate = receive,
            HasRates = measured,
            IsMuted = muted,
            Place = place,
        };
    }

    private object Dress(Program program, RouteNode node, bool muted, HashSet<string> seen)
    {
        if (node.LeafKey is { } key && program.Peers.TryGetValue(key, out PeerNodeViewModel? peer))
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
            return peer;
        }

        string id = program.Id + node.Id;

        if (!program.Hops.TryGetValue(id, out HopNodeViewModel? hop))
        {
            hop = new HopNodeViewModel(id, node.Address, node.Distance, node.Silences);
            program.Hops[id] = hop;
        }

        seen.Add(id);

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
            hop.Name(ShowNames ? _hosts.Find(hop.Address) : null, ShowNames);
        }

        return hop;
    }

    private string Prompt(bool followed) => followed
        ? "Click to show the whole map again"
        : _followed is null ? "Click to follow this branch" : string.Empty;

    private HashSet<string>? Following(List<(Program Program, RouteNode Root)> built)
    {
        foreach ((Program program, RouteNode root) in built)
        {
            HashSet<string> lit = new(StringComparer.Ordinal) { program.Id };

            // The whole of a program, when the program itself is the subject.
            if (ReferenceEquals(program.Node, _followed))
            {
                foreach (RouteNode child in root.Children)
                {
                    Beyond(program, child, lit);
                }

                return lit;
            }

            List<RouteNode> path = [];

            if (!Path(program, root, path))
            {
                continue;
            }

            foreach (RouteNode step in path)
            {
                lit.Add(program.Id + step.Id);
            }

            Beyond(program, path[^1], lit);
            return lit;
        }
        _followed = null;
        return null;
    }

    private bool Path(Program program, RouteNode node, List<RouteNode> path)
    {
        path.Add(node);

        if (Owns(program, node))
        {
            return true;
        }

        foreach (RouteNode child in node.Children)
        {
            if (Path(program, child, path))
            {
                return true;
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }

    private static void Beyond(Program program, RouteNode node, HashSet<string> lit)
    {
        lit.Add(program.Id + node.Id);

        foreach (RouteNode child in node.Children)
        {
            Beyond(program, child, lit);
        }
    }

    private bool Owns(Program program, RouteNode node) => _followed switch
    {
        PeerNodeViewModel peer => node.LeafKey is { } key
            && program.Peers.TryGetValue(key, out PeerNodeViewModel? mine)
            && ReferenceEquals(mine, peer),
        HopNodeViewModel hop => string.Equals(program.Id + node.Id, hop.Id, StringComparison.Ordinal),
        _ => false,
    };

    private void Rename()
    {
        if (_disposed)
        {
            return;
        }

        Regroup();
        Regraph();
    }

    partial void OnShowNamesChanged(bool value) => Rename();

    partial void OnShowLoopbackChanged(bool value) => Rename();

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

    private static string Rates(double receive, double send) => string.Format(
        CultureInfo.CurrentCulture,
        "{0} in, {1} out",
        RateFormatter.Format(receive, RateFamily.Bytes, RateScale.Auto),
        RateFormatter.Format(send, RateFamily.Bytes, RateScale.Auto));

    private static string Took(TimeSpan round) => round.TotalMilliseconds < 1
        ? "<1 ms"
        : string.Format(CultureInfo.CurrentCulture, "{0:N0} ms", round.TotalMilliseconds);

    private static string Count(int many, string one, string more) => many switch
    {
        0 => string.Empty,
        1 => string.Concat("1 ", one),
        _ => string.Format(CultureInfo.CurrentCulture, "{0:N0} {1}", many, more),
    };

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

        foreach (Program program in _ordered)
        {
            _endpoints.Unwatch(program.ProcessId);
        }

        _programs.Clear();
        _ordered.Clear();
    }

    private sealed class Program
    {
        public Program(int processId, string name, long seen)
        {
            ProcessId = processId;
            Id = string.Create(CultureInfo.InvariantCulture, $"p{processId}");
            Node = new ProcessNodeViewModel(processId, name, seen);
        }

        public int ProcessId { get; }

        /// <summary>What every node of this program's branch is filed under.</summary>
        public string Id { get; }

        public ProcessNodeViewModel Node { get; }

        public Dictionary<string, PeerNodeViewModel> Peers { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, HopNodeViewModel> Hops { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Draft> Drafts { get; } = new(StringComparer.Ordinal);

        public Dictionary<EndpointKey, EndpointTraffic> Traffic { get; set; } = [];

        public Dictionary<EndpointKey, EndpointTraffic> Earlier { get; set; } = [];

        public DateTimeOffset MeasuredAt { get; set; }
    }

    private sealed class Together
    {
        public Together(string key, long seen)
        {
            Key = key;
            Seen = seen;
        }

        /// <summary>What identifies the place across readings.</summary>
        public string Key { get; }

        /// <summary>When the first program to reach it started doing so.</summary>
        public long Seen { get; private set; }

        public SortedSet<string> Addresses { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The far ends folded into this one, and what each is moving.
        /// </summary>
        public Dictionary<string, double> Sites { get; } = new(StringComparer.Ordinal);

        public SortedSet<int> Ports { get; } = [];

        /// <summary>The programs reaching it, in the order they were seen.</summary>
        public List<GraphReacher> Programs { get; } = [];

        public double Send { get; private set; }

        public double Receive { get; private set; }

        public bool Measured { get; private set; }

        /// <summary>Whether every route to it came back silent.</summary>
        public bool Unreachable { get; set; }

        /// <summary>Folds one program's conversation with this place into it.</summary>
        public void Add(string program, ImageSource? icon, PeerNodeViewModel peer)
        {
            ArgumentNullException.ThrowIfNull(peer);

            Sites.TryGetValue(peer.Label, out double already);
            Sites[peer.Label] = already + peer.SendRate + peer.ReceiveRate;

            Send += peer.SendRate;
            Receive += peer.ReceiveRate;
            Measured |= peer.HasRates;
            Seen = Math.Min(Seen, peer.Seen);

            foreach (string address in peer.Probes)
            {
                Addresses.Add(address);
            }

            if (!Programs.Any(each => string.Equals(each.Name, program, StringComparison.Ordinal)))
            {
                Programs.Add(new GraphReacher(program, icon));
            }
        }

        /// <summary>Puts everything gathered onto the node that draws it.</summary>
        public void Apply(PeerNodeViewModel peer)
        {
            ArgumentNullException.ThrowIfNull(peer);

            peer.Label = Key;
            peer.SendRate = Send;
            peer.ReceiveRate = Receive;
            peer.HasRates = Measured;
            peer.Probes = [.. Addresses];
            peer.IsLocal = false;
            peer.Weight = Measured ? Send + Receive : Programs.Count;

            peer.RateText = Measured
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} in, {1} out",
                    RateFormatter.Format(Receive, RateFamily.Bytes, RateScale.Auto),
                    RateFormatter.Format(Send, RateFamily.Bytes, RateScale.Auto))
                : Say(Programs.Count, "program", "programs");

            peer.Addresses = Addresses.Count switch
            {
                0 => string.Empty,
                1 => string.Concat("Address ", Addresses.Min),
                _ => string.Concat("Addresses ", Join(Addresses)),
            };
            peer.Reachers = Programs.Count <= Shown
                ? [.. Programs]
                : [.. Programs.Take(Shown), new GraphReacher(
                    string.Format(CultureInfo.CurrentCulture, "and {0:N0} more", Programs.Count - Shown),
                    null)];

            peer.States = Programs.Count switch
            {
                0 => string.Empty,
                1 => string.Concat("Reached by ", Programs[0].Name),
                _ => string.Concat("Reached by ", Join(Programs.Select(each => each.Name))),
            };
            peer.Sockets = string.Empty;
            peer.Ports = string.Empty;
            peer.Sites = Sites.Count > 1
                ? string.Concat(
                    Say(Sites.Count, "server", "servers"),
                    ": ",
                    Join(Sites.OrderByDescending(each => each.Value).Select(each => each.Key)))
                : string.Empty;
        }

        private static string Say(int many, string one, string more) => many switch
        {
            0 => string.Empty,
            1 => string.Concat("1 ", one),
            _ => string.Format(CultureInfo.CurrentCulture, "{0:N0} {1}", many, more),
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

    private sealed class Draft
    {
        public SortedSet<string> Addresses { get; } = new(StringComparer.Ordinal);

        public SortedSet<int> Ports { get; } = [];

        public double Send { get; set; }

        public double Receive { get; set; }

        public int Sockets { get; set; }

        public bool Measured { get; set; }

        /// <summary>Whether this far end is on this machine.</summary>
        public bool IsLocal { get; init; }

        /// <summary>What to call it, where that is not simply its key.</summary>
        public string? Name { get; set; }

        /// <summary>Puts everything gathered onto the node that draws it.</summary>
        public void Apply(PeerNodeViewModel peer)
        {
            string sockets = Say(Sockets, "connection", "connections");

            peer.IsLocal = IsLocal;
            peer.Label = Name is { Length: > 0 } named ? named : peer.Key;
            peer.SendRate = Send;
            peer.ReceiveRate = Receive;
            peer.HasRates = Measured;
            peer.Probes = [.. Addresses];
            peer.Sockets = sockets;
            peer.Weight = Measured ? Send + Receive : Sockets;
            peer.Addresses = Line(Addresses.Count, "Address", "Addresses", Join(Addresses));
            peer.Ports = Line(Ports.Count, "Port", "Ports", Join(Ports.Select(PortNames.Describe)));

            peer.RateText = Measured
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} in, {1} out",
                    RateFormatter.Format(Receive, RateFamily.Bytes, RateScale.Auto),
                    RateFormatter.Format(Send, RateFamily.Bytes, RateScale.Auto))
                : sockets;
        }

        private static string Say(int many, string one, string more) => many switch
        {
            0 => string.Empty,
            1 => string.Concat("1 ", one),
            _ => string.Format(CultureInfo.CurrentCulture, "{0:N0} {1}", many, more),
        };

        private static string Line(int count, string one, string many, string values) => count switch
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
