using System.Globalization;
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
            ? created.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.CurrentCulture)
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
    public partial string Host { get; private set; }

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

    /// <summary>Takes a fresh reading of the same socket.</summary>
    public void Update(ProcessConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        IsClosed = false;
        IsListening = connection.IsListening;
        State = Describe(connection);

        Age = connection.Created is { } created
            ? DurationLabel.Format((DateTimeOffset.Now - created).TotalSeconds)
            : "—";
    }

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
