using System.Globalization;

namespace Windsock.Core.Processes;

/// <summary>Which transport a socket speaks.</summary>
public enum TransportProtocol
{
    Tcp,
    Udp,
}

/// <summary>
/// Where a TCP connection is in its lifetime, as Windows reports it.
/// </summary>
public enum TcpConnectionState
{
    Unknown = 0,
    Closed = 1,
    Listening = 2,
    SynSent = 3,
    SynReceived = 4,
    Established = 5,
    FinWait1 = 6,
    FinWait2 = 7,
    CloseWait = 8,
    Closing = 9,
    LastAck = 10,
    TimeWait = 11,
    DeleteTcb = 12,
}

/// <summary>
/// One socket a process holds open, with everything Windows will say about it.
/// </summary>
public sealed record ProcessConnection(
    int ProcessId,
    TransportProtocol Protocol,
    bool IsIpv6,
    string LocalAddress,
    int LocalPort,
    string? RemoteAddress,
    int RemotePort,
    TcpConnectionState State,
    DateTimeOffset? Created)
{
    /// <summary>
    /// Identifies the socket across readings.
    /// </summary>
    public string Key { get; } = string.Create(
        CultureInfo.InvariantCulture,
        $"{Protocol}|{LocalAddress}|{LocalPort}|{RemoteAddress}|{RemotePort}");

    /// <summary>Whether the socket is waiting for callers rather than talking.</summary>
    public bool IsListening =>
        State == TcpConnectionState.Listening || (Protocol == TransportProtocol.Udp && RemoteAddress is null);
}

/// <summary>Reads every socket a process holds open.</summary>
public interface IProcessConnectionDetailSource
{
    IReadOnlyList<ProcessConnection> Read(int processId);

    IReadOnlyList<ProcessConnection> ReadAll();
}
