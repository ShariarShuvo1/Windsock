namespace Windsock.Core.Processes;

/// <summary>
/// What a process is, as opposed to what it is doing.
/// </summary>
/// <param name="Name">The executable's name, without its extension.</param>
/// <param name="Description">The executable's own description, when it has one.</param>
/// <param name="FilePath">The executable's full path, when Windows will say.</param>
/// <param name="StartedAt">When the process started, when Windows will say.</param>
/// <param name="PackageFamily">
/// The package family, when the process runs a file installed with a packaged
/// app - which is how Windows counts its traffic. Null for everything else,
/// a program a packaged app started from somewhere else included.
/// </param>
public sealed record ProcessIdentity(
    string Name,
    string? Description,
    string? FilePath,
    DateTimeOffset? StartedAt,
    string? PackageFamily = null);

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
