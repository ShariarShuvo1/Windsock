using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.App.Services;
using Windsock.Core.Processes;
using Windsock.Core.Startup;

namespace Windsock.App.ViewModels;

/// <summary>
/// Backs the process table: which processes are on the network, how much they
/// are moving, and everything the header does to that list.
/// </summary>
public sealed partial class ProcessPanelViewModel : ObservableObject
{
    private const string DefaultSortKey = nameof(ProcessRowViewModel.TotalBytesPerSecond);

    private readonly ProcessUsageMonitor _monitor;
    private readonly ProcessIconCache _icons;
    private readonly Dispatcher _dispatcher;
    private readonly HashSet<int> _showing = [];
    private readonly Dictionary<int, ProcessRowViewModel> _processRows = [];
    private readonly Dictionary<string, ProcessRowViewModel> _groupRows = [];
    private readonly Dictionary<string, List<ProcessUsage>> _buckets = [];
    private readonly HashSet<string> _expanded = [];
    private readonly List<ProcessRowViewModel> _desired = [];
    private readonly List<ProcessRowViewModel> _members = [];
    private IReadOnlyList<ProcessUsage> _latest = [];
    private string? _pinnedRow;
    private string? _pinnedTop;
    private string _sortKey = DefaultSortKey;
    private bool _sortDescending = true;
    private int _matched;
    private int _total;
    private bool _running;
    private IDisposable? _watch;

    public ProcessPanelViewModel(
        ProcessUsageMonitor monitor,
        ProcessIconCache icons,
        Dispatcher dispatcher)
    {
        _monitor = monitor;
        _icons = icons;
        _dispatcher = dispatcher;

        NameColumn = Column.Text<ProcessRowViewModel>("Process", row => row.Name);
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

    /// <summary>Free text matched against the name, description and id of every row.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Whether to hide processes that are not moving anything right now.</summary>
    [ObservableProperty]
    public partial bool ActiveOnly { get; set; }

    /// <summary>Whether processes sharing a name are collected under one row.</summary>
    [ObservableProperty]
    public partial bool IsGrouped { get; set; } = true;

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
    public partial bool RatesAvailable { get; set; }

    /// <summary>Why rates are unavailable, or <see langword="null"/> when they are.</summary>
    [ObservableProperty]
    public partial string? RateNotice { get; set; }

    /// <summary>The longer version of the notice, shown on hover.</summary>
    [ObservableProperty]
    public partial string? RateDetail { get; set; }

    /// <summary>Whether restarting with administrator rights would help.</summary>
    [ObservableProperty]
    public partial bool CanElevate { get; set; }

    /// <summary>How many processes the table is showing out of how many it holds.</summary>
    [ObservableProperty]
    public partial string CountText { get; set; } = string.Empty;

    /// <summary>Whether the table has no rows to show.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary>What to say when the table is empty, which depends on why.</summary>
    [ObservableProperty]
    public partial string EmptyText { get; set; } = string.Empty;

    /// <summary>
    /// Whether anything about the view differs from how it started.
    /// </summary>
    public bool IsCustomised =>
        !string.IsNullOrWhiteSpace(SearchText)
        || ActiveOnly
        || !IsGrouped
        || Columns.Any(column => !column.IsDefault)
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
        ReadRateState();
        Apply(_monitor.Latest);
    }

    /// <summary>
    /// Stops listening, and lets go of the collection this tab asked for.
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

        _watch?.Dispose();
        _watch = null;
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
                or nameof(ProcessRowViewModel.Description)
                or nameof(ProcessRowViewModel.FilePath));
        }

        Rebuild();
    }

    /// <summary>Raised when a row asks to be watched in its own window.</summary>
    public event EventHandler<int>? WatchRequested;

    /// <summary>
    /// Whether the map of the whole machine is open above the table.
    /// </summary>
    [ObservableProperty]
    public partial bool IsMapOpen { get; set; }

    /// <summary>
    /// The map itself, while it is open.
    /// </summary>
    [ObservableProperty]
    public partial NetworkMapViewModel? Map { get; set; }

    partial void OnMapChanged(NetworkMapViewModel? value)
    {
        // A map opened while the table is paused opens paused.
        if (value is not null)
        {
            value.IsLive = IsLive;
        }

        Steer();
    }

    [RelayCommand]
    private void Watch(ProcessRowViewModel? row)
    {
        if (row is not null && row.Kind != RowKind.Group)
        {
            WatchRequested?.Invoke(this, row.ProcessId);
        }
    }

    [RelayCommand]
    private void ToggleGroup(ProcessRowViewModel? row)
    {
        if (row is not { Kind: RowKind.Group })
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
        _dispatcher.BeginInvoke(ReadRateState);

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
        CanElevate = _monitor.RateState == TrafficSourceState.RequiresElevation;
        RateNotice = RatesAvailable ? null : _monitor.RateDetail;
        RateDetail = RatesAvailable ? null : _monitor.RateExplanation;
    }

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
                if (column.HasFilter)
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
        _total = 0;
        _matched = 0;

        foreach (ProcessUsage usage in _latest)
        {
            _total++;

            if (!Matches(usage))
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

        _desired.Sort(Compare);
        for (int index = _desired.Count - 1; index >= 0; index--)
        {
            ProcessRowViewModel row = _desired[index];

            if (row.Kind != RowKind.Group || !row.IsExpanded)
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

    private void Illustrate()
    {
        foreach (ProcessRowViewModel row in Rows)
        {
            row.Icon ??= _icons.Find(row.FilePath);

            row.IsPinned = _pinnedRow is not null
                && string.Equals(row.Key, _pinnedRow, StringComparison.Ordinal);
        }
    }

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
    }

    private ProcessRowViewModel RowFor(in ProcessUsage usage, RowKind kind)
    {
        if (_processRows.TryGetValue(usage.ProcessId, out ProcessRowViewModel? row))
        {
            row.Kind = kind;
            row.Update(usage, RatesAvailable);
            return row;
        }

        row = new ProcessRowViewModel(usage, kind, RatesAvailable);
        _processRows[usage.ProcessId] = row;
        return row;
    }

    private ProcessRowViewModel GroupFor(string name)
    {
        if (_groupRows.TryGetValue(name, out ProcessRowViewModel? row))
        {
            return row;
        }

        row = new ProcessRowViewModel(name, RatesAvailable);
        _groupRows[name] = row;
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
            nameof(ProcessRowViewModel.Name) =>
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
        return result != 0 ? result : x.ProcessId.CompareTo(y.ProcessId);
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
        CountText = _matched == _total
            ? string.Format(CultureInfo.CurrentCulture, "{0} processes", _total)
            : string.Format(CultureInfo.CurrentCulture, "{0} of {1} processes", _matched, _total);

        IsEmpty = Rows.Count == 0;
        EmptyText = _total == 0
            ? "No processes are using the network"
            : "Nothing matches the current filters";

        OnPropertyChanged(nameof(IsCustomised));
    }

    private bool Matches(in ProcessUsage usage)
    {
        if (ActiveOnly && usage.TotalBytesPerSecond <= 0)
        {
            return false;
        }

        ProcessRowViewModel row = RowFor(usage, IsGrouped ? RowKind.Child : RowKind.Standalone);

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
