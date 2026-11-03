using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Formatting;
using Windsock.Core.Processes;

namespace Windsock.App.ViewModels;

/// <summary>One socket, as a row in the process monitor.</summary>
public sealed partial class ConnectionRowViewModel : ObservableObject
{
    private readonly string _plain;
    private string? _named;

    public ConnectionRowViewModel(ProcessConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Key = connection.Key;
        ProcessId = connection.ProcessId;
        Source = connection;
        Protocol = connection.Protocol == TransportProtocol.Tcp ? "TCP" : "UDP";
        Version = connection.IsIpv6 ? "IPv6" : "IPv4";
        LocalAddress = connection.LocalAddress;
        LocalPort = connection.LocalPort;
        LocalPortText = PortNames.Describe(connection.LocalPort);
        RemoteAddress = connection.RemoteAddress ?? string.Empty;
        RemotePort = connection.RemotePort;

        RemotePortText = connection.RemoteAddress is null
            ? "—"
            : PortNames.Describe(connection.RemotePort);

        Opened = connection.Created;

        OpenedText = connection.Created is { } created
            ? ClockFormat.TimeWithSeconds(created.ToLocalTime())
            : "—";

        _plain = RemoteAddress.Length == 0 ? "\u2014" : RemoteAddress;
        Host = _plain;
        Update(connection);
    }

    /// <summary>The four-tuple that identifies this socket across readings.</summary>
    public string Key { get; }

    /// <summary>
    /// Which node on the graph this socket belongs to.
    /// </summary>
    public string PeerKey { get; set; } = string.Empty;

    public string Protocol { get; }

    public string Version { get; }

    public string LocalAddress { get; }

    /// <summary>
    /// The local address in words for a screen reader: "::" and "0.0.0.0" are
    /// every address the machine has, which is what a listening socket means by them.
    /// </summary>
    public string LocalAddressSpoken => LocalAddress switch
    {
        "::" => "Every IPv6 address",
        "0.0.0.0" => "Every IPv4 address",
        _ => LocalAddress,
    };

    public int LocalPort { get; }

    public string LocalPortText { get; }

    public string RemoteAddress { get; }

    public int RemotePort { get; }

    public string RemotePortText { get; }

    /// <summary>When the socket was opened, when Windows recorded it.</summary>
    public DateTimeOffset? Opened { get; }

    public string OpenedText { get; }

    /// <summary>The far end's name once it resolves, otherwise its address.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseName))]
    [NotifyPropertyChangedFor(nameof(HostSpoken))]
    public partial string Host { get; private set; }

    /// <summary>
    /// The far end in words for a screen reader: a listening socket's "::" or
    /// "0.0.0.0" is no far end at all, and a dash is a far end not known.
    /// </summary>
    public string HostSpoken => Host switch
    {
        "::" or "0.0.0.0" => "No far end",
        "\u2014" => "not known",
        _ => Host,
    };

    /// <summary>Where the connection is in its lifetime.</summary>
    [ObservableProperty]
    public partial string State { get; private set; } = string.Empty;

    /// <summary>How long the socket has been open.</summary>
    [ObservableProperty]
    public partial string Age { get; private set; } = "—";

    /// <summary>
    /// Whether the socket has gone since the last reading.
    /// </summary>
    [ObservableProperty]
    public partial bool IsClosed { get; set; }

    /// <summary>Whether the socket is waiting for callers rather than talking.</summary>
    [ObservableProperty]
    public partial bool IsListening { get; private set; }

    /// <summary>Whether this row's peer is the one under the pointer.</summary>
    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    /// <summary>Whether this connection is drawn on the graph above.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; } = true;

    /// <summary>The process holding the socket.</summary>
    public int ProcessId { get; }

    /// <summary>The socket as last read, for an action on it such as closing it.</summary>
    public ProcessConnection Source { get; private set; }

    /// <summary>Who runs the far end's network, from the table shipped with Windsock, or a dash.</summary>
    [ObservableProperty]
    public partial string Network { get; set; } = "—";

    /// <summary>The blocklist the far end is on, in words; empty when it is on none.</summary>
    [ObservableProperty]
    public partial string Listed { get; set; } = string.Empty;

    /// <summary>Whether the far end is on a blocklist the person switched on.</summary>
    [ObservableProperty]
    public partial bool IsListed { get; set; }

    /// <summary>The service that opened the socket, in a service host; empty otherwise.</summary>
    [ObservableProperty]
    public partial string Service { get; set; } = string.Empty;

    /// <summary>Whether Windows can close this connection from outside its program: talking IPv4 TCP only.</summary>
    [ObservableProperty]
    public partial bool CanClose { get; set; }

    /// <summary>
    /// What the row's Close button does (full edition). Carried by the row
    /// rather than found up the tree, for the reason the Apps table's rows
    /// carry their owner: a row being taken away has no tree to look up.
    /// </summary>
    public System.Windows.Input.ICommand? CloseCommand { get; set; }

    /// <summary>Takes a fresh reading of the same socket.</summary>
    public void Update(ProcessConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Source = connection;
        IsClosed = false;
        IsListening = connection.IsListening;
        State = Describe(connection);

        Age = connection.Created is { } created
            ? DurationLabel.Format((DateTimeOffset.Now - created).TotalSeconds)
            : "—";
    }

    /// <summary>
    /// What the row says, which is what a screen reader names the table's row
    /// by: "TCP to example.com, port 443 (https), Established", or "UDP, Bound
    /// on port 5353 (mdns)" for a socket with no far end.
    /// </summary>
    public override string ToString() => RemoteAddress.Length == 0 || IsListening
        ? string.Concat(Protocol, ", ", State, " on port ", LocalPortText)
        : string.Concat(Protocol, " to ", Host, ", port ", RemotePortText, ", ", State);

    /// <summary>
    /// What the row's Close button is called, so each says which connection it
    /// closes: "Close the connection to example.com, port 443, from local port
    /// 59007". The local port is what tells apart several connections a
    /// program holds open to one place.
    /// </summary>
    public string CloseName =>
        string.Concat("Close the connection to ", Host, ", port ", RemotePortText, ", from local port ", LocalPortText);

    /// <summary>
    /// Gives the row a name once the resolver has one, or takes it away again.
    /// </summary>
    public void Name(string? name, bool show)
    {
        if (!string.IsNullOrEmpty(name))
        {
            _named = name;
        }

        Host = show && _named is not null ? _named : _plain;
    }

    private static string Describe(ProcessConnection connection) => connection.Protocol switch
    {
        TransportProtocol.Udp => connection.RemoteAddress is null ? "Bound" : "Open",
        _ => connection.State switch
        {
            TcpConnectionState.Listening => "Listening",
            TcpConnectionState.Established => "Established",
            TcpConnectionState.SynSent => "Connecting",
            TcpConnectionState.SynReceived => "Connecting",
            TcpConnectionState.FinWait1 or TcpConnectionState.FinWait2 => "Closing",
            TcpConnectionState.CloseWait => "Closing",
            TcpConnectionState.Closing => "Closing",
            TcpConnectionState.LastAck => "Closing",
            TcpConnectionState.TimeWait => "Time wait",
            TcpConnectionState.Closed => "Closed",
            TcpConnectionState.DeleteTcb => "Closed",
            _ => "Unknown",
        },
    };
}
