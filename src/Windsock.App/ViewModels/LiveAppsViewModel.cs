using System.Collections.ObjectModel;
#if !STORE
using System.Diagnostics;
#endif
using System.Globalization;
#if !STORE
using System.Windows;
#endif
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.App.Services;
using Windsock.Core.Apps;
using Windsock.Core.Processes;
using Windsock.Core.Settings;
#if !STORE
using Windsock.Core.Startup;
#endif

namespace Windsock.App.ViewModels;

/// <summary>One choice in the Apps page's view switch.</summary>
public sealed record LiveViewOption(bool Processes, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Backs the live half of the Apps page: which apps are on the network right
/// now and how much they are moving, with the network map above them - or, in
/// the full edition run as administrator, the same for each process.
/// </summary>
/// <remarks>
/// <para>
/// Apps are the unit because apps are what Windows counts: every process
/// running one program, or one package (<see cref="RunningApps"/>). Without a
/// kernel trace the rates are Windows' own count for each app over the last few
/// minutes, which belongs to the row whole, so nothing is shared out between
/// processes that nobody measured apart.
/// </para>
/// <para>
/// The process view needs each process measured, so it is offered only while
/// the trace is running. The choice is remembered, and set aside - not changed -
/// whenever the trace is not.
/// </para>
/// </remarks>
public sealed partial class LiveAppsViewModel : ObservableObject, IDisposable
{
    private const string DefaultSortKey = nameof(ProcessRowViewModel.TotalBytesPerSecond);

    private readonly ProcessUsageMonitor _monitor;
    private readonly RecentAppFigures _figures;
    private readonly AppNames _names;
    private readonly ProcessIconCache _icons;
    private readonly AppIconCache _appIcons;
    private readonly Dispatcher _dispatcher;
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _store;
    private readonly HashSet<int> _showing = [];
    private readonly Dictionary<int, ProcessRowViewModel> _processRows = [];
    private readonly Dictionary<string, ProcessRowViewModel> _groupRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProcessRowViewModel> _appRows = new(RunningApps.KeyComparer);
    private readonly Dictionary<string, List<ProcessUsage>> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expanded = [];
    private readonly List<ProcessRowViewModel> _desired = [];
    private readonly List<ProcessRowViewModel> _members = [];
    private IReadOnlyList<ProcessUsage> _latest = [];
    private string? _pinnedRow;
    private string? _pinnedTop;
    private string _sortKey = DefaultSortKey;
    private bool _sortDescending = true;
    private readonly HashSet<string> _runningApps = new(RunningApps.KeyComparer);
    private int _matched;
    private int _total;
    private bool _running;
    private bool _viewApplied;
    private IDisposable? _watch;

    public LiveAppsViewModel(
        ProcessUsageMonitor monitor,
        RecentAppFigures figures,
        AppNames names,
        ProcessIconCache icons,
        AppIconCache appIcons,
        WindsockSettings settings,
        ISettingsStore store,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _monitor = monitor;
        _figures = figures;
        _names = names;
        _icons = icons;
        _appIcons = appIcons;
        _settings = settings;
        _store = store;
        _dispatcher = dispatcher;
        IsMapOpen = settings.ProcessMapOpen;

        NameColumn = Column.Text<ProcessRowViewModel>("App", row => row.DisplayName);
        DescriptionColumn = Column.Text<ProcessRowViewModel>("Description", row => row.Description, visible: false);
        ProcessIdColumn = Column.Identifier<ProcessRowViewModel>("PID", row => row.ProcessIdText);
        SendColumn = Column.Number<ProcessRowViewModel>("Send", ColumnKind.Rate, row => row.SendBytesPerSecond);
        ReceiveColumn = Column.Number<ProcessRowViewModel>("Receive", ColumnKind.Rate, row => row.ReceiveBytesPerSecond);
        TotalColumn = Column.Number<ProcessRowViewModel>("Total", ColumnKind.Rate, row => row.TotalBytesPerSecond);
        ConnectionColumn = Column.Number<ProcessRowViewModel>("Sockets", ColumnKind.Count, row => row.Connections);
        TcpColumn = Column.Number<ProcessRowViewModel>("TCP", ColumnKind.Count, row => row.TcpConnections, visible: false);
        UdpColumn = Column.Number<ProcessRowViewModel>("UDP", ColumnKind.Count, row => row.UdpEndpoints, visible: false);
        PacketColumn = Column.Number<ProcessRowViewModel>("Packets/s", ColumnKind.Count, row => row.PacketsPerSecond, visible: false);
        SentColumn = Column.Number<ProcessRowViewModel>("Sent", ColumnKind.Volume, row => row.BytesSent, visible: false);
        ReceivedColumn = Column.Number<ProcessRowViewModel>("Received", ColumnKind.Volume, row => row.BytesReceived, visible: false);
        TransferredColumn = Column.Number<ProcessRowViewModel>("Transferred", ColumnKind.Volume, row => row.BytesTransferred, visible: false);
        UptimeColumn = Column.Number<ProcessRowViewModel>("Uptime", ColumnKind.Duration, row => row.UptimeSeconds, visible: false);
        PathColumn = Column.Text<ProcessRowViewModel>("Path", row => row.FilePath, visible: false);

        // Declaration order, which is the order the grid holds them in.
        Columns =
        [
            NameColumn,
            DescriptionColumn,
            ProcessIdColumn,
            SendColumn,
            ReceiveColumn,
            TotalColumn,
            ConnectionColumn,
            TcpColumn,
            UdpColumn,
            PacketColumn,
            SentColumn,
            ReceivedColumn,
            TransferredColumn,
            UptimeColumn,
            PathColumn,
        ];

        foreach (TableColumn column in Columns)
        {
            column.Changed += (_, _) => Rebuild();
        }

        // After the columns: the choice switches views, and a view reaches for
        // them. Set first, a remembered "Processes" threw as the window opened.
        ShowProcesses = settings.AppsShowProcesses;
        ApplyView();
    }

    /// <summary>The table, in the order it is shown.</summary>
    public ObservableCollection<ProcessRowViewModel> Rows { get; } = [];

    /// <summary>Every column, in the order the grid declares them.</summary>
    public IReadOnlyList<TableColumn<ProcessRowViewModel>> Columns { get; }

    public TableColumn<ProcessRowViewModel> NameColumn { get; }

    public TableColumn<ProcessRowViewModel> DescriptionColumn { get; }

    public TableColumn<ProcessRowViewModel> ProcessIdColumn { get; }

    public TableColumn<ProcessRowViewModel> SendColumn { get; }

    public TableColumn<ProcessRowViewModel> ReceiveColumn { get; }

    public TableColumn<ProcessRowViewModel> TotalColumn { get; }

    public TableColumn<ProcessRowViewModel> ConnectionColumn { get; }

    public TableColumn<ProcessRowViewModel> TcpColumn { get; }

    public TableColumn<ProcessRowViewModel> UdpColumn { get; }

    public TableColumn<ProcessRowViewModel> PacketColumn { get; }

    public TableColumn<ProcessRowViewModel> SentColumn { get; }

    public TableColumn<ProcessRowViewModel> ReceivedColumn { get; }

    public TableColumn<ProcessRowViewModel> TransferredColumn { get; }

    public TableColumn<ProcessRowViewModel> UptimeColumn { get; }

    public TableColumn<ProcessRowViewModel> PathColumn { get; }

    /// <summary>The property the table is sorted by.</summary>
    public string SortKey => _sortKey;

    /// <summary>Whether the sort runs from largest to smallest.</summary>
    public bool SortDescending => _sortDescending;

    /// <summary>Free text matched against the name, description, path and id of every row.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Whether to hide rows that are not moving anything right now.</summary>
    [ObservableProperty]
    public partial bool ActiveOnly { get; set; }

    /// <summary>In the process view, whether processes sharing a name are collected under one row.</summary>
    [ObservableProperty]
    public partial bool IsGrouped { get; set; } = true;

    /// <summary>
    /// Whether processes were asked for, rather than apps. Remembered; shown
    /// only while <see cref="CanShowProcesses"/>.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowProcesses { get; set; }

    /// <summary>Whether the table and the map show processes: asked for, and measured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChosenView))]
    public partial bool IsProcessView { get; private set; }

    /// <summary>The two views the switch offers.</summary>
    public IReadOnlyList<LiveViewOption> Views { get; } =
    [
        new LiveViewOption(false, "Apps"),
        new LiveViewOption(true, "Processes"),
    ];

    /// <summary>
    /// What the switch shows: the view on screen, which is apps whenever
    /// processes cannot be measured, whatever was chosen before.
    /// </summary>
    public bool ChosenView
    {
        get => IsProcessView;
        set
        {
            if (value != IsProcessView && (!value || CanShowProcesses))
            {
                ShowProcesses = value;
            }
        }
    }

    /// <summary>Whether each process is measured, so the process view can be offered.</summary>
    public bool CanShowProcesses => RatesAvailable;

    /// <summary>
    /// Whether this edition has a process view at all. The Store edition takes
    /// no kernel trace, so it has apps only and no switch.
    /// </summary>
#if STORE
    public static bool IsViewSwitchOffered => false;
#else
    public static bool IsViewSwitchOffered => true;
#endif

    /// <summary>What the switch says when the process view is out of reach.</summary>
    public string ViewSwitchHint => CanShowProcesses
        ? "Show each process instead of each app. Processes are measured each second while Windsock runs as administrator."
        : "Processes can be shown one by one only while each is measured, which needs Windsock to run as administrator.";

    /// <summary>
    /// Whether the table is following the machine or holding still.
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

    partial void OnIsLiveChanged(bool value)
    {
        if (Map is { } map)
        {
            map.IsLive = value;
        }
    }

    /// <summary>Whether per-process rates are being collected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowProcesses))]
    [NotifyPropertyChangedFor(nameof(ViewSwitchHint))]
    public partial bool RatesAvailable { get; set; }

    /// <summary>
    /// Whether, with no live rates, the rate columns carry Windows' count for
    /// each app over the last few minutes instead.
    /// </summary>
    [ObservableProperty]
    public partial bool RecentRates { get; set; }

    /// <summary>What the rates are, or why there are none; null when each is measured and nothing needs saying.</summary>
    [ObservableProperty]
    public partial string? RateNotice { get; set; }

    /// <summary>The longer version of the notice, shown on hover.</summary>
    [ObservableProperty]
    public partial string? RateDetail { get; set; }

    /// <summary>Whether restarting with administrator rights would help.</summary>
    [ObservableProperty]
    public partial bool CanElevate { get; set; }

    /// <summary>How many rows the table is showing out of how many it holds.</summary>
    [ObservableProperty]
    public partial string CountText { get; set; } = string.Empty;

    /// <summary>Whether the table has no rows to show.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary>What to say when the table is empty, which depends on why.</summary>
    [ObservableProperty]
    public partial string EmptyText { get; set; } = string.Empty;

    /// <summary>What the search box is called, which follows the view.</summary>
    public string SearchName => IsProcessView ? "Search processes" : "Search apps";

    /// <summary>
    /// Whether anything about the view differs from how it started.
    /// </summary>
    public bool IsCustomised =>
        !string.IsNullOrWhiteSpace(SearchText)
        || ActiveOnly
        || (IsProcessView && !IsGrouped)
        || Columns.Any(column => column.IsOffered && !column.IsDefault)
        || _sortKey != DefaultSortKey
        || !_sortDescending;

    /// <summary>Raised after the view is put back to its defaults.</summary>
    public event EventHandler? ViewReset;

    /// <summary>Begins listening for updates.</summary>
    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _watch ??= _monitor.Watch();

        _monitor.Updated += OnUpdated;
        _monitor.RateStateChanged += OnRateStateChanged;
        _figures.Changed += OnFiguresChanged;
        ReadRateState();
        Apply(_monitor.Latest);
    }

    /// <summary>
    /// Stops listening, and lets go of the collection this page asked for.
    /// </summary>
    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _monitor.Updated -= OnUpdated;
        _monitor.RateStateChanged -= OnRateStateChanged;
        _figures.Changed -= OnFiguresChanged;

        _watch?.Dispose();
        _watch = null;
    }

    /// <summary>
    /// Lets go of the collection and of the map, when the window this page
    /// belongs to closes.
    /// </summary>
    public void Dispose()
    {
        Stop();
        Map?.Dispose();
    }

    /// <summary>Sorts by a column, reversing the direction if it is already the one.</summary>
    public void SortBy(string key)
    {
        if (_sortKey == key)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortKey = key;
            _sortDescending = key is not (nameof(ProcessRowViewModel.Name)
                or nameof(ProcessRowViewModel.DisplayName)
                or nameof(ProcessRowViewModel.Description)
                or nameof(ProcessRowViewModel.FilePath));
        }

        Rebuild();
    }

    /// <summary>Raised when a process row asks to be watched in its own window.</summary>
    public event EventHandler<int>? WatchRequested;

    /// <summary>Raised when an app row asks to be watched in its own window, with the app's key.</summary>
    public event EventHandler<string>? WatchAppRequested;

    /// <summary>
    /// Whether the map of the whole machine is open above the table. Open to
    /// begin with, since the map is the head of the page, and remembered once
    /// folded: an open map traces routes.
    /// </summary>
    [ObservableProperty]
    public partial bool IsMapOpen { get; set; } = true;

    partial void OnIsMapOpenChanged(bool value)
    {
        if (_settings is null || _settings.ProcessMapOpen == value)
        {
            return;
        }

        _settings.ProcessMapOpen = value;
        _store?.Save(_settings);
    }

    /// <summary>
    /// The map itself, while it is open.
    /// </summary>
    [ObservableProperty]
    public partial NetworkMapViewModel? Map { get; set; }

    partial void OnMapChanged(NetworkMapViewModel? value)
    {
        // A map opened while the table is paused opens paused, and draws what
        // the table lists: apps, or processes.
        if (value is not null)
        {
            value.IsLive = IsLive;
            value.GroupByApp = !IsProcessView;
        }

        Steer();
    }

    partial void OnShowProcessesChanged(bool value)
    {
        if (_settings is not null && _settings.AppsShowProcesses != value)
        {
            _settings.AppsShowProcesses = value;
            _store?.Save(_settings);
        }

        ApplyView();
    }

    /// <summary>Works out which view is on screen, and puts the table and the map in it.</summary>
    private void ApplyView()
    {
        bool processes = ShowProcesses && RatesAvailable;

        // Packets are counted only by the trace; without it the column has
        // nothing but dashes to show. A process id means nothing beside an app.
        PacketColumn.IsOffered = RatesAvailable;
        ProcessIdColumn.IsOffered = processes;
        NameColumn.Title = processes ? "Process" : "App";

        // A sort by a column no longer offered would order the table by
        // something nobody can see; it goes back to the default.
        if ((_sortKey == nameof(ProcessRowViewModel.ProcessId) && !ProcessIdColumn.IsOffered)
            || (_sortKey == nameof(ProcessRowViewModel.PacketsPerSecond) && !PacketColumn.IsOffered))
        {
            _sortKey = DefaultSortKey;
            _sortDescending = true;
            ViewReset?.Invoke(this, EventArgs.Empty);
        }

        if (processes == IsProcessView && _viewApplied)
        {
            return;
        }

        _viewApplied = true;
        IsProcessView = processes;
        _expanded.Clear();

        _pinnedRow = null;
        _pinnedTop = null;

        OnPropertyChanged(nameof(SearchName));

        if (Map is { } map)
        {
            map.GroupByApp = !processes;
        }

        Rebuild();
    }

    [RelayCommand]
    private void Watch(ProcessRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.IsApp)
        {
            WatchAppRequested?.Invoke(this, row.Key);
        }
        else if (row.Kind != RowKind.Group)
        {
            WatchRequested?.Invoke(this, row.ProcessId);
        }
    }

    [RelayCommand]
    private void ToggleGroup(ProcessRowViewModel? row)
    {
        if (row is not { IsExpandable: true })
        {
            return;
        }

        if (_expanded.Remove(row.Key))
        {
            Rebuild();
            return;
        }

        _expanded.RemoveWhere(name => !string.Equals(name, _pinnedTop, StringComparison.Ordinal));
        _expanded.Add(row.Key);

        Rebuild();
    }

#if !STORE
    [RelayCommand]
    private static void RestartElevated()
    {
        string? path = Environment.ProcessPath;

        if (path is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = SingleInstance.ReplaceSwitch,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;
        }

        Application.Current?.Shutdown();
    }
#endif

    [RelayCommand]
    private void ResetView()
    {
        SearchText = string.Empty;
        ActiveOnly = false;
        IsGrouped = true;
        _sortKey = DefaultSortKey;
        _sortDescending = true;
        _expanded.Clear();

        foreach (TableColumn column in Columns)
        {
            column.Reset();
        }

        Rebuild();
        ViewReset?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSearchTextChanged(string value) => Rebuild();

    partial void OnActiveOnlyChanged(bool value) => Rebuild();

    partial void OnIsGroupedChanged(bool value) => Rebuild();

    private void OnRateStateChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            ReadRateState();
            Rebuild();
        });

    private void OnFiguresChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (!_running)
            {
                return;
            }

            ReadRateState();
            Rebuild();
        });

    private void OnUpdated(object? sender, IReadOnlyList<ProcessUsage> rows)
    {
        if (!IsLive)
        {
            return;
        }

        _dispatcher.BeginInvoke(() => Apply(rows));
    }

    private void ReadRateState()
    {
        RatesAvailable = _monitor.RateState == TrafficSourceState.Running;
        RecentRates = !RatesAvailable && _figures.IsKnown;
        CanElevate = _monitor.RateState == TrafficSourceState.RequiresElevation;

        // The trace may have started or stopped under a remembered choice.
        ApplyView();

        if (RatesAvailable)
        {
            RateNotice = null;
            RateDetail = null;
            return;
        }

        string rates = RecentRates
            ? "Send, receive and total are each app's average over the last five minutes, as Windows counted it."
            : _figures.State == AppUsageState.Unavailable
                ? "Windows' own count of each app could not be read, so no rates are shown."
                : "Windows' count of each app is still being read.";

#if STORE
        RateNotice = rates;
        RateDetail = "Windows counts each program by its file and each Store app by its package, over the last few minutes. This edition takes no kernel trace, so nothing is measured second by second or split into processes.";
#else
        RateNotice = CanElevate
            ? rates + " Run as administrator to measure each second and to see each process."
            : string.Concat(_monitor.RateDetail, " ", rates).Trim();
        RateDetail = _monitor.RateExplanation;
#endif
    }

    private static ProcessIdentity Representative(List<ProcessUsage> members)
    {
        foreach (ProcessUsage member in members)
        {
            if (member.Identity.FilePath is not null)
            {
                return member.Identity;
            }
        }

        return members[0].Identity;
    }

    /// <summary>
    /// Whether an app is moving anything, by whichever count is on show. An app
    /// whose figure is unknown is kept: hiding it would claim it is idle.
    /// </summary>
    private static bool IsActive(ProcessRowViewModel row) =>
        !row.AreRatesKnown || row.TotalBytesPerSecond > 0;

    private void Apply(IReadOnlyList<ProcessUsage> rows)
    {
        _latest = rows;
        Rebuild();
    }

    private bool Narrowed
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SearchText) || ActiveOnly)
            {
                return true;
            }

            foreach (TableColumn<ProcessRowViewModel> column in Columns)
            {
                if (column.HasFilter && column.IsOffered)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private void Steer() => Map?.Follow(Narrowed ? _showing : null);

    private void Rebuild()
    {
        _desired.Clear();
        _buckets.Clear();
        _showing.Clear();

        if (IsProcessView)
        {
            GatherProcesses();
        }
        else
        {
            GatherApps();
        }

        _desired.Sort(Compare);

        for (int index = _desired.Count - 1; index >= 0; index--)
        {
            ProcessRowViewModel row = _desired[index];

            if (!row.IsExpandable || !row.IsExpanded)
            {
                continue;
            }

            _members.Clear();

            foreach (ProcessUsage usage in _buckets[row.Key])
            {
                _members.Add(RowFor(usage, RowKind.Child));
            }

            _members.Sort(Compare);

            for (int member = 0; member < _members.Count; member++)
            {
                _members[member].IsLastChild = member == _members.Count - 1;
            }

            _desired.InsertRange(index + 1, _members);
        }

        Reconcile();
        Illustrate();
        Prune();
        UpdateCount();
    }

    /// <summary>
    /// One row a process, or a group of processes sharing a name, each tested
    /// against the search and filters as itself.
    /// </summary>
    private void GatherProcesses()
    {
        _total = _latest.Count;
        _matched = 0;

        foreach (ProcessUsage usage in _latest)
        {
            ProcessRowViewModel row = RowFor(usage, IsGrouped ? RowKind.Child : RowKind.Standalone);

            if ((ActiveOnly && usage.TotalBytesPerSecond <= 0) || !Matches(row))
            {
                continue;
            }

            _matched++;
            _showing.Add(usage.ProcessId);

            if (!_buckets.TryGetValue(usage.Name, out List<ProcessUsage>? bucket))
            {
                bucket = [];
                _buckets[usage.Name] = bucket;
            }

            bucket.Add(usage);
        }

        foreach ((string name, List<ProcessUsage> bucket) in _buckets)
        {
            if (!IsGrouped || bucket.Count == 1)
            {
                foreach (ProcessUsage usage in bucket)
                {
                    _desired.Add(RowFor(usage, RowKind.Standalone));
                }

                continue;
            }

            ProcessRowViewModel group = GroupFor(name);
            group.UpdateGroup(bucket, RatesAvailable);
            group.IsExpanded = _expanded.Contains(name);
            _desired.Add(group);
        }
    }

    /// <summary>
    /// One row an app, holding every process of it. The row is what is tested
    /// against the search and filters, whole: a filter on a rate means the
    /// app's rate, not any one of its processes'.
    /// </summary>
    private void GatherApps()
    {
        Dictionary<string, List<ProcessUsage>> apps = new(RunningApps.KeyComparer);
        Dictionary<int, string> keys = RunningApps.KeysFor(_latest);

        foreach (ProcessUsage usage in _latest)
        {
            string key = keys[usage.ProcessId];

            if (!apps.TryGetValue(key, out List<ProcessUsage>? members))
            {
                members = [];
                apps[key] = members;
            }

            members.Add(usage);
        }

        _total = apps.Count;
        _matched = 0;
        _runningApps.Clear();
        _runningApps.UnionWith(apps.Keys);

        foreach ((string key, List<ProcessUsage> members) in apps)
        {
            ProcessRowViewModel row = AppFor(key, members);
            row.UpdateGroup(members, RatesAvailable);

            // Before testing and sorting, so the row is tested and sorted by
            // the figures it shows.
            if (!RatesAvailable)
            {
                row.ShowRecent(RecentRates ? _figures.For(Representative(members)) : null, RecentAppFigures.Seconds);
            }

            if ((ActiveOnly && !IsActive(row)) || !Matches(row))
            {
                continue;
            }

            _matched++;
            _buckets[key] = members;
            _desired.Add(row);

            foreach (ProcessUsage member in members)
            {
                _showing.Add(member.ProcessId);
            }
        }
    }

    private void Illustrate()
    {
        foreach (ProcessRowViewModel row in Rows)
        {
            if (row.Icon is null)
            {
                row.Icon = row.IsApp && row.AppKind == AppKind.Package && _buckets.TryGetValue(row.Key, out List<ProcessUsage>? members)
                    ? _appIcons.Find(PackageOf(members))
                    : _icons.Find(row.FilePath);
            }

            row.IsPinned = _pinnedRow is not null
                && string.Equals(row.Key, _pinnedRow, StringComparison.Ordinal);
        }
    }

    private AppDescription PackageOf(List<ProcessUsage> members) =>
        _names.Describe(Representative(members).PackageFamily);

    [RelayCommand]
    private static void Reveal(string? path) => FileReveal.Show(path);

    private void Reconcile()
    {
        for (int index = 0; index < _desired.Count; index++)
        {
            ProcessRowViewModel wanted = _desired[index];

            if (index >= Rows.Count)
            {
                Rows.Add(wanted);
                continue;
            }

            if (ReferenceEquals(Rows[index], wanted))
            {
                continue;
            }

            int existing = IndexOf(wanted, index + 1);

            if (existing >= 0)
            {
                Rows.Move(existing, index);
            }
            else
            {
                Rows.Insert(index, wanted);
            }
        }

        while (Rows.Count > _desired.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        Steer();
    }

    private int IndexOf(ProcessRowViewModel row, int from)
    {
        for (int index = from; index < Rows.Count; index++)
        {
            if (ReferenceEquals(Rows[index], row))
            {
                return index;
            }
        }

        return -1;
    }

    private void Prune()
    {
        if (_processRows.Count > _latest.Count * 2)
        {
            HashSet<int> alive = [.. _latest.Select(usage => usage.ProcessId)];

            foreach (int processId in _processRows.Keys.Where(id => !alive.Contains(id)).ToList())
            {
                _processRows.Remove(processId);
            }
        }

        if (_groupRows.Count > Math.Max(8, _buckets.Count * 2))
        {
            foreach (string name in _groupRows.Keys.Where(key => !_buckets.ContainsKey(key)).ToList())
            {
                _groupRows.Remove(name);
                _expanded.Remove(name);
            }
        }

        if (_appRows.Count > Math.Max(8, _buckets.Count * 2))
        {
            foreach (string key in _appRows.Keys.Where(key => !_runningApps.Contains(key)).ToList())
            {
                _appRows.Remove(key);
            }
        }
    }

    private ProcessRowViewModel RowFor(in ProcessUsage usage, RowKind kind)
    {
        if (_processRows.TryGetValue(usage.ProcessId, out ProcessRowViewModel? row))
        {
            row.Kind = kind;
            row.Update(usage, RatesAvailable);
            return row;
        }

        row = new ProcessRowViewModel(usage, kind, RatesAvailable) { Owner = this, ToggleCommand = ToggleGroupCommand };
        _processRows[usage.ProcessId] = row;
        return row;
    }

    private ProcessRowViewModel GroupFor(string name)
    {
        if (_groupRows.TryGetValue(name, out ProcessRowViewModel? row))
        {
            return row;
        }

        row = new ProcessRowViewModel(name, RatesAvailable) { Owner = this, ToggleCommand = ToggleGroupCommand };
        _groupRows[name] = row;
        return row;
    }

    private ProcessRowViewModel AppFor(string key, List<ProcessUsage> members)
    {
        if (!_appRows.TryGetValue(key, out ProcessRowViewModel? row))
        {
            ProcessIdentity identity = Representative(members);
            row = new ProcessRowViewModel(key, identity.Name, RatesAvailable) { Owner = this, ToggleCommand = ToggleGroupCommand };

            // A packaged app goes by the name its package gives it, which its
            // executables' own descriptions often do not.
            if (row.AppKind == AppKind.Package && identity.PackageFamily is { } family)
            {
                string name = _names.Describe(family).Name;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    row.AppName = name;
                }
            }

            _appRows[key] = row;
        }

        return row;
    }

    private int Compare(ProcessRowViewModel x, ProcessRowViewModel y)
    {
        int pinned = Pinned(y).CompareTo(Pinned(x));

        if (pinned != 0)
        {
            return pinned;
        }

        int result = _sortKey switch
        {
            nameof(ProcessRowViewModel.Name) or nameof(ProcessRowViewModel.DisplayName) =>
                string.Compare(x.DisplayName, y.DisplayName, StringComparison.OrdinalIgnoreCase),
            nameof(ProcessRowViewModel.Description) =>
                string.Compare(x.Description, y.Description, StringComparison.OrdinalIgnoreCase),
            nameof(ProcessRowViewModel.FilePath) =>
                string.Compare(x.FilePath, y.FilePath, StringComparison.OrdinalIgnoreCase),
            _ => Value(x).CompareTo(Value(y)),
        };

        if (_sortDescending)
        {
            result = -result;
        }

        if (result != 0)
        {
            return result;
        }

        result = y.Connections.CompareTo(x.Connections);

        if (result != 0)
        {
            return result;
        }

        result = string.Compare(x.DisplayName, y.DisplayName, StringComparison.OrdinalIgnoreCase);
        return result != 0 ? result : string.CompareOrdinal(x.Key, y.Key);
    }

    private int Pinned(ProcessRowViewModel row)
    {
        if (_pinnedTop is null)
        {
            return 0;
        }

        string wanted = row.Kind == RowKind.Child ? _pinnedRow ?? string.Empty : _pinnedTop;
        return string.Equals(row.Key, wanted, StringComparison.Ordinal) ? 1 : 0;
    }

    [RelayCommand]
    private void TogglePin(ProcessRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        Pin(string.Equals(row.Key, _pinnedRow, StringComparison.Ordinal) ? null : row);
    }

    /// <summary>
    /// Holds a row at the top of the table, or lets go of it.
    /// </summary>
    public void Pin(ProcessRowViewModel? row)
    {
        string? top = row is null
            ? null
            : row.Kind == RowKind.Child ? row.Name : row.Key;

        if (string.Equals(top, _pinnedTop, StringComparison.Ordinal)
            && string.Equals(row?.Key, _pinnedRow, StringComparison.Ordinal))
        {
            return;
        }

        _pinnedRow = row?.Key;
        _pinnedTop = top;

        // A member held at the top is no use inside a group folded shut.
        if (row is { Kind: RowKind.Child } && top is not null)
        {
            _expanded.Add(top);
        }

        Apply(_latest);
    }

    private double Value(ProcessRowViewModel row) => _sortKey switch
    {
        nameof(ProcessRowViewModel.ProcessId) => row.ProcessId,
        nameof(ProcessRowViewModel.SendBytesPerSecond) => row.SendBytesPerSecond,
        nameof(ProcessRowViewModel.ReceiveBytesPerSecond) => row.ReceiveBytesPerSecond,
        nameof(ProcessRowViewModel.PacketsPerSecond) => row.PacketsPerSecond,
        nameof(ProcessRowViewModel.BytesSent) => row.BytesSent,
        nameof(ProcessRowViewModel.BytesReceived) => row.BytesReceived,
        nameof(ProcessRowViewModel.BytesTransferred) => row.BytesTransferred,
        nameof(ProcessRowViewModel.Connections) => row.Connections,
        nameof(ProcessRowViewModel.TcpConnections) => row.TcpConnections,
        nameof(ProcessRowViewModel.UdpEndpoints) => row.UdpEndpoints,
        nameof(ProcessRowViewModel.UptimeSeconds) => row.UptimeSeconds,
        _ => row.TotalBytesPerSecond,
    };

    private void UpdateCount()
    {
        (string one, string many) = IsProcessView ? ("process", "processes") : ("app", "apps");

        CountText = _matched == _total
            ? string.Format(CultureInfo.CurrentCulture, "{0} {1}", _total, _total == 1 ? one : many)
            : string.Format(CultureInfo.CurrentCulture, "{0} of {1} {2}", _matched, _total, many);

        IsEmpty = Rows.Count == 0;
        EmptyText = _total == 0
            ? IsProcessView ? "No processes are using the network" : "No apps are using the network"
            : "Nothing matches the current filters";

        OnPropertyChanged(nameof(IsCustomised));
    }

    private bool Matches(ProcessRowViewModel row)
    {
        if (!string.IsNullOrWhiteSpace(SearchText) && !row.Contains(SearchText.Trim()))
        {
            return false;
        }

        foreach (TableColumn<ProcessRowViewModel> column in Columns)
        {
            if (!column.Matches(row))
            {
                return false;
            }
        }

        return true;
    }
}
