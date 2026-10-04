using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Apps;
using Windsock.Core.Formatting;
using Windsock.Core.Processes;

namespace Windsock.App.ViewModels;

/// <summary>What a row stands for.</summary>
public enum RowKind
{
    Standalone,

    Group,

    Child,
}

/// <summary>
/// One row of the Apps page's live table: an app - every process running one
/// program or one package - or, in the process view, a process or a group of
/// processes sharing a name.
/// </summary>
public sealed partial class ProcessRowViewModel : ObservableObject
{
    private const string Unknown = "—";
    private RowKind _kind;
    private ProcessIdentity _identity;
    private double _send;
    private double _receive;
    private double _packets;
    private long _sent;
    private long _received;
    private SocketCounts _sockets;
    private double _uptime;
    private int _children;
    private bool _expanded;
    private bool _last;
    private bool _ratesKnown;
    private bool _countedLive;
    private bool _minuteFigure;
    private string? _appName;

    /// <summary>Creates a row for one process.</summary>
    public ProcessRowViewModel(in ProcessUsage usage, RowKind kind, bool ratesKnown)
    {
        _kind = kind;
        Key = usage.ProcessId.ToString(CultureInfo.InvariantCulture);
        ProcessId = usage.ProcessId;
        ProcessIdText = Key;
        _identity = usage.Identity;
        _ratesKnown = ratesKnown;
        _countedLive = ratesKnown;
        Read(usage);
    }

    /// <summary>Creates a row that sums the processes of one name.</summary>
    public ProcessRowViewModel(string name, bool ratesKnown)
    {
        _kind = RowKind.Group;
        Key = name;
        ProcessIdText = string.Empty;
        _identity = new ProcessIdentity(name, null, null, null);
        _ratesKnown = ratesKnown;
        _countedLive = ratesKnown;
    }

    /// <summary>Creates a row that sums every process of one app.</summary>
    /// <param name="key">The app's key, from <see cref="RunningApps.KeyOf"/>.</param>
    /// <param name="name">What the app is called before its processes are read.</param>
    /// <param name="ratesKnown">Whether its processes are measured live.</param>
    public ProcessRowViewModel(string key, string name, bool ratesKnown)
    {
        _kind = RowKind.Group;
        Key = key;
        IsApp = true;
        AppKind = RunningApps.KindOf(key);
        ProcessIdText = string.Empty;
        _identity = new ProcessIdentity(name, null, null, null);
        _ratesKnown = ratesKnown;
        _countedLive = ratesKnown;
    }

    /// <summary>
    /// The table the row belongs to, for its row menu. Carried by the row
    /// rather than found by walking up from the cell: a process that starts and
    /// ends within a second has its cells built while its row is being taken
    /// away, and a lookup up the tree from there finds nothing.
    /// </summary>
    public object? Owner { get; set; }

    /// <summary>What pressing the row's chevron does, for the same reason as <see cref="Owner"/>.</summary>
    public ICommand? ToggleCommand { get; set; }

    /// <summary>Whether this row stands for an app rather than for processes.</summary>
    public bool IsApp { get; }

    /// <summary>What sort of app the row is, for an app row.</summary>
    public AppKind AppKind { get; }

    /// <summary>
    /// The name an app goes by where its processes' own descriptions would say
    /// something else - a packaged app's display name. Null to go by those.
    /// </summary>
    public string? AppName
    {
        get => _appName;
        set
        {
            if (!string.Equals(_appName, value, StringComparison.Ordinal))
            {
                _appName = value;
                Notify(nameof(DisplayName));
            }
        }
    }

    /// <summary>
    /// Whether this row is a process, a group, or one of a group's members.
    /// </summary>
    public RowKind Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsChild));
                OnPropertyChanged(nameof(IsExpandable));
                OnPropertyChanged(nameof(CountText));
            }
        }
    }

    /// <summary>
    /// The icon the program is drawn with, once it has been read.
    /// </summary>
    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    /// <summary>
    /// Whether this row is the one being held at the top of the table.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinLabel))]
    public partial bool IsPinned { get; set; }

    /// <summary>What the menu offers to do about the pin.</summary>
    public string PinLabel => IsPinned ? "Unpin" : "Pin to top";

    /// <summary>
    /// Whether this row is one process, and so has details to open.
    /// </summary>
    public bool CanOpenDetails => Kind != RowKind.Group || IsApp;

    /// <summary>Identity within the table: a process id, or a group's name.</summary>
    public string Key { get; }

    /// <summary>The operating system process id, or zero for a group.</summary>
    public int ProcessId { get; }

    /// <summary>The process id as text, empty for a group.</summary>
    public string ProcessIdText { get; }

    /// <summary>Executable name, which is also what a group is keyed by.</summary>
    public string Name => _identity.Name;

    /// <summary>
    /// What to call this row: the executable's own description when Windows
    /// will give one, and the name it runs under otherwise.
    /// </summary>
    public string DisplayName =>
        _appName ?? (string.IsNullOrEmpty(_identity.Description) ? _identity.Name : _identity.Description);

    /// <summary>The executable's own description, or empty when Windows will not say.</summary>
    public string Description => _identity.Description ?? string.Empty;

    /// <summary>Full path to the executable, or empty when Windows will not say.</summary>
    public string FilePath => _identity.FilePath ?? string.Empty;

    /// <summary>How many processes this row stands for.</summary>
    public int ChildCount
    {
        get => _children;
        private set
        {
            if (SetProperty(ref _children, value))
            {
                OnPropertyChanged(nameof(CountText));
                OnPropertyChanged(nameof(IsExpandable));
            }
        }
    }

    /// <summary>The count shown beside a group's name, empty for a single process.</summary>
    public string CountText =>
        Kind == RowKind.Group && (!IsApp || _children > 1)
            ? string.Create(CultureInfo.CurrentCulture, $"({_children})")
            : string.Empty;

    /// <summary>
    /// Whether this row can be opened to show the processes under it. An app
    /// cannot: its processes are one thing to Windows, and are shown apart only
    /// in the process view, where each is measured.
    /// </summary>
    public bool IsExpandable => Kind == RowKind.Group && !IsApp;

    /// <summary>Whether a group is currently showing its processes.</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set => SetProperty(ref _expanded, value);
    }

    /// <summary>Whether this row is listed under a group.</summary>
    public bool IsChild => Kind == RowKind.Child;

    /// <summary>
    /// Whether this is the last row under its group.
    /// </summary>
    public bool IsLastChild
    {
        get => _last;
        set => SetProperty(ref _last, value);
    }

    /// <summary>Whether the rate cells state a figure, measured or Windows' count, rather than a dash.</summary>
    public bool AreRatesKnown => _ratesKnown;

    /// <summary>Outbound rate in bytes per second. Sorted on.</summary>
    public double SendBytesPerSecond => _send;

    /// <summary>Inbound rate in bytes per second. Sorted on.</summary>
    public double ReceiveBytesPerSecond => _receive;

    /// <summary>Combined rate in bytes per second. Sorted on.</summary>
    public double TotalBytesPerSecond => _send + _receive;

    /// <summary>Packets per second in either direction. Sorted on.</summary>
    public double PacketsPerSecond => _packets;

    /// <summary>Bytes sent since Windsock first saw the process. Sorted on.</summary>
    public double BytesSent => _sent;

    /// <summary>Bytes received since Windsock first saw the process. Sorted on.</summary>
    public double BytesReceived => _received;

    /// <summary>Everything moved in either direction. Sorted on.</summary>
    public double BytesTransferred => _sent + _received;

    /// <summary>Open TCP connections and UDP endpoints. Sorted on.</summary>
    public int Connections => _sockets.Total;

    /// <summary>Established TCP connections. Sorted on.</summary>
    public int TcpConnections => _sockets.Tcp;

    /// <summary>Bound UDP endpoints. Sorted on.</summary>
    public int UdpEndpoints => _sockets.Udp;

    /// <summary>Seconds since the process started, or since the oldest of a group did.</summary>
    public double UptimeSeconds => _uptime;

    /// <summary>Whether this row is sending anything right now.</summary>
    public bool HasSend => _send > 0;

    /// <summary>Whether this row is receiving anything right now.</summary>
    public bool HasReceive => _receive > 0;

    public string SendText => Rate(_send);

    public string ReceiveText => Rate(_receive);

    public string TotalText => Rate(TotalBytesPerSecond);

    public string PacketsText =>
        _countedLive ? _packets.ToString("F0", CultureInfo.CurrentCulture) : Unknown;

    /// <summary>
    /// Whether the rates are Windows' count for the whole program over the last
    /// few minutes, rather than this process measured second by second.
    /// </summary>
    public bool IsRecentFigure => _minuteFigure;

    /// <summary>What the rate cells are, said on hover.</summary>
    public string RateSource => _minuteFigure
        ? IsApp
            ? "Windows' count for this app, averaged over the last five minutes"
            : "Windows' count for this whole program, averaged over the last five minutes"
        : _countedLive ? "Measured each second" : "Not measured";

    public string SentText => Volume(_sent);

    public string ReceivedText => Volume(_received);

    public string TransferredText => Volume(_sent + _received);

    public string UptimeText => _uptime > 0 ? DurationLabel.Format(_uptime) : Unknown;

    /// <summary>Applies a fresh reading to a single process or a group member.</summary>
    public void Update(in ProcessUsage usage, bool ratesKnown)
    {
        if (!ReferenceEquals(_identity, usage.Identity))
        {
            _identity = usage.Identity;
            Notify(nameof(Name), nameof(DisplayName), nameof(Description), nameof(FilePath));
        }

        _ratesKnown = ratesKnown;
        _countedLive = ratesKnown;
        _minuteFigure = false;
        Read(usage);
    }

    /// <summary>
    /// Shows Windows' count for the program this row stands for, averaged over
    /// <paramref name="seconds"/>, in place of rates nothing is measuring - or a
    /// dash when Windows counted nothing this row can be matched to.
    /// </summary>
    /// <remarks>
    /// Given only to a row standing for a whole program. Windows counts a
    /// program, not a process, and a figure shared out between processes it
    /// cannot tell apart would be invented.
    /// </remarks>
    public void ShowRecent(AppUsageRecord? record, double seconds)
    {
        bool was = _minuteFigure;
        _minuteFigure = record is not null;
        _ratesKnown = record is not null;
        _countedLive = false;

        Apply(
            record is { } found && seconds > 0 ? found.BytesSent / seconds : 0,
            record is { } again && seconds > 0 ? again.BytesReceived / seconds : 0,
            _packets,
            _sent,
            _received,
            _sockets,
            _uptime);

        // The texts depend on what is known as well as on the numbers, which
        // may not have moved.
        Notify(
            nameof(SendText),
            nameof(ReceiveText),
            nameof(TotalText),
            nameof(PacketsText),
            nameof(SentText),
            nameof(ReceivedText),
            nameof(TransferredText),
            nameof(RateSource));

        if (was != _minuteFigure)
        {
            Notify(nameof(IsRecentFigure));
        }
    }

    /// <summary>Applies a fresh reading to a group, summing what it holds.</summary>
    public void UpdateGroup(List<ProcessUsage> members, bool ratesKnown)
    {
        double send = 0;
        double receive = 0;
        double packets = 0;
        long sent = 0;
        long received = 0;
        int tcp = 0;
        int udp = 0;
        double uptime = 0;
        ProcessIdentity identity = _identity;

        foreach (ProcessUsage member in members)
        {
            send += member.SendBytesPerSecond;
            receive += member.ReceiveBytesPerSecond;
            packets += member.PacketsPerSecond;
            sent += member.BytesSent;
            received += member.BytesReceived;
            tcp += member.Sockets.Tcp;
            udp += member.Sockets.Udp;
            double age = Age(member);
            uptime = Math.Max(uptime, age);
            if (identity.Description is null && member.Identity.Description is not null)
            {
                identity = member.Identity;
            }
        }

        // An app's row takes its file from whichever process can say, since
        // they all run the same one or the same package.
        if (IsApp && identity.FilePath is null)
        {
            foreach (ProcessUsage member in members)
            {
                if (member.Identity.FilePath is not null)
                {
                    identity = member.Identity;
                    break;
                }
            }
        }

        if (!ReferenceEquals(_identity, identity))
        {
            _identity = identity;
            Notify(nameof(DisplayName), nameof(Description), nameof(FilePath));
        }

        _ratesKnown = ratesKnown;
        _countedLive = ratesKnown;
        _minuteFigure = false;
        ChildCount = members.Count;
        Apply(send, receive, packets, sent, received, new SocketCounts(tcp, udp), uptime);
    }

    /// <summary>Whether the row matches a search term.</summary>
    public bool Contains(string term) =>
        Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Description.Contains(term, StringComparison.OrdinalIgnoreCase)
        || FilePath.Contains(term, StringComparison.OrdinalIgnoreCase)
        || ProcessIdText.Contains(term, StringComparison.Ordinal);

    private void Read(in ProcessUsage usage) =>
        Apply(
            usage.SendBytesPerSecond,
            usage.ReceiveBytesPerSecond,
            usage.PacketsPerSecond,
            usage.BytesSent,
            usage.BytesReceived,
            usage.Sockets,
            Age(usage));

    private void Apply(
        double send,
        double receive,
        double packets,
        long sent,
        long received,
        SocketCounts sockets,
        double uptime)
    {
        _uptime = uptime;
        Notify(nameof(UptimeSeconds), nameof(UptimeText));

        if (_sockets != sockets)
        {
            _sockets = sockets;
            Notify(nameof(Connections), nameof(TcpConnections), nameof(UdpEndpoints));
        }

        bool moved = false;

        if (!_send.Equals(send))
        {
            _send = send;
            Notify(nameof(SendBytesPerSecond), nameof(SendText), nameof(HasSend));
            moved = true;
        }

        if (!_receive.Equals(receive))
        {
            _receive = receive;
            Notify(nameof(ReceiveBytesPerSecond), nameof(ReceiveText), nameof(HasReceive));
            moved = true;
        }

        if (!_packets.Equals(packets))
        {
            _packets = packets;
            Notify(nameof(PacketsPerSecond), nameof(PacketsText));
        }

        if (_sent != sent || _received != received)
        {
            _sent = sent;
            _received = received;
            Notify(
                nameof(BytesSent),
                nameof(BytesReceived),
                nameof(BytesTransferred),
                nameof(SentText),
                nameof(ReceivedText),
                nameof(TransferredText));
        }

        if (moved)
        {
            Notify(nameof(TotalBytesPerSecond), nameof(TotalText));
        }
    }

    private static double Age(in ProcessUsage usage) =>
        usage.Identity.StartedAt is { } started ? (DateTimeOffset.Now - started).TotalSeconds : 0;

    private void Notify(params string[] names)
    {
        foreach (string name in names)
        {
            OnPropertyChanged(name);
        }
    }

    private string Rate(double bytesPerSecond) =>
        _ratesKnown ? RateFormatter.Format(bytesPerSecond, RateFamily.Bytes, RateScale.Auto) : Unknown;

    private string Volume(long bytes) => _countedLive ? SizeFormatter.Format(bytes) : Unknown;
}
