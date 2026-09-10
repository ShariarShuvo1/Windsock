namespace Windsock.Core.Processes;

/// <summary>
/// What a process is, as opposed to what it is doing.
/// </summary>
public sealed record ProcessIdentity(
    string Name,
    string? Description,
    string? FilePath,
    DateTimeOffset? StartedAt);

/// <summary>Sockets a process holds open, split by protocol.</summary>
public readonly record struct SocketCounts(int Tcp, int Udp)
{
    /// <summary>Both protocols together.</summary>
    public int Total => Tcp + Udp;
}

/// <summary>Cumulative counters attributed to one process since tracing began.</summary>
public readonly record struct ProcessTraffic(long BytesSent, long BytesReceived, long Packets);

/// <summary>One process's share of the network over the last interval.</summary>
public readonly record struct ProcessUsage(
    int ProcessId,
    ProcessIdentity Identity,
    double SendBytesPerSecond,
    double ReceiveBytesPerSecond,
    double PacketsPerSecond,
    long BytesSent,
    long BytesReceived,
    SocketCounts Sockets)
{
    /// <summary>Executable name.</summary>
    public string Name => Identity.Name;

    /// <summary>Combined rate in both directions.</summary>
    public double TotalBytesPerSecond => SendBytesPerSecond + ReceiveBytesPerSecond;

    /// <summary>Everything moved in either direction since Windsock saw the process.</summary>
    public long BytesTransferred => BytesSent + BytesReceived;

    /// <summary>Sockets held open, both protocols.</summary>
    public int Connections => Sockets.Total;
}
