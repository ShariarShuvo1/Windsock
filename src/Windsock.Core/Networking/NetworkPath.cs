using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Windsock.Core.Networking;

/// <summary>One step on the way to somewhere.</summary>
public sealed record PathHop(int Distance, string? Address, TimeSpan? RoundTrip, bool IsDestination);

/// <summary>What a single probe at one distance told us.</summary>
public enum HopOutcome
{
    Onward,

    Arrived,

    Silent,

    Blocked,
}

/// <summary>Rules for walking a route out, shared by the probe and its tests.</summary>
public static class PathTrace
{
    /// <summary>How far out to look before giving up.</summary>
    public const int MaximumHops = 24;

    /// <summary>How many silences in a row end the walk.</summary>
    public const int GiveUpAfter = 4;

    /// <summary>How long to wait at each distance.</summary>
    public static readonly TimeSpan HopPatience = TimeSpan.FromSeconds(1);

    /// <summary>Reads a probe's result as a decision about what to do next.</summary>
    public static HopOutcome Read(IPStatus status) => status switch
    {
        IPStatus.Success => HopOutcome.Arrived,
        IPStatus.TtlExpired or IPStatus.TimeExceeded => HopOutcome.Onward,
        IPStatus.TimedOut => HopOutcome.Silent,
        _ => HopOutcome.Blocked,
    };

    /// <summary>
    /// Whether walking a route to an address would mean anything.
    /// </summary>
    public static bool Walkable(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address, out IPAddress? parsed))
        {
            return false;
        }

        if (IPAddress.IsLoopback(parsed)
            || parsed.Equals(IPAddress.Any)
            || parsed.Equals(IPAddress.IPv6Any)
            || parsed.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        return !Shouted(parsed);
    }

    private static bool Shouted(IPAddress address)
    {
        if (address.IsIPv6Multicast)
        {
            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> quads = stackalloc byte[4];

        // 224.0.0.0 through 239.255.255.255, which is the whole of it.
        return address.TryWriteBytes(quads, out _) && quads[0] is >= 224 and <= 239;
    }

    /// <summary>
    /// Drops the trailing silences from a walk that ran out rather than arrived.
    /// </summary>
    public static IReadOnlyList<PathHop> Trim(IReadOnlyList<PathHop> hops)
    {
        ArgumentNullException.ThrowIfNull(hops);

        int last = hops.Count - 1;

        while (last >= 0 && hops[last].Address is null)
        {
            last--;
        }

        return last == hops.Count - 1 ? hops : [.. hops.Take(last + 1)];
    }
}

/// <summary>Finds the route to a far end, and how long it takes to answer.</summary>
public interface INetworkPathProbe
{
    IAsyncEnumerable<PathHop> TraceAsync(string address, CancellationToken cancellationToken);

    Task<TimeSpan?> PingAsync(string address, CancellationToken cancellationToken);
}

/// <summary>Walks a route with ICMP echoes of increasing hop limit.</summary>
public sealed class PingNetworkPathProbe : INetworkPathProbe
{
    private static readonly byte[] Payload = new byte[32];

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public async IAsyncEnumerable<PathHop> TraceAsync(
        string address,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (await ResolveAsync(address, cancellationToken).ConfigureAwait(false) is not { } target)
        {
            yield break;
        }

        int silent = 0;

        using Ping ping = new();

        for (int distance = 1; distance <= PathTrace.MaximumHops; distance++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long started = Stopwatch.GetTimestamp();
            PingReply reply;

            try
            {
                reply = await ping.SendPingAsync(
                        target,
                        PathTrace.HopPatience,
                        Payload,
                        new PingOptions(distance, dontFragment: false),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PingException or SocketException)
            {
                break;
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            HopOutcome outcome = PathTrace.Read(reply.Status);

            if (outcome == HopOutcome.Silent)
            {
                yield return new PathHop(distance, null, null, IsDestination: false);

                if (++silent >= PathTrace.GiveUpAfter)
                {
                    break;
                }

                continue;
            }

            silent = 0;
            yield return new PathHop(
                distance,
                reply.Address?.ToString(),
                elapsed,
                outcome == HopOutcome.Arrived);

            if (outcome != HopOutcome.Onward)
            {
                break;
            }
        }
    }

    /// <inheritdoc />
    public async Task<TimeSpan?> PingAsync(string address, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(address, cancellationToken).ConfigureAwait(false) is not { } target)
        {
            return null;
        }

        using Ping ping = new();
        long started = Stopwatch.GetTimestamp();

        try
        {
            PingReply reply = await ping.SendPingAsync(target, Patience, Payload, null, cancellationToken)
                .ConfigureAwait(false);

            return reply.Status == IPStatus.Success ? Stopwatch.GetElapsedTime(started) : null;
        }
        catch (Exception ex) when (ex is PingException or SocketException)
        {
            return null;
        }
    }

    private static async Task<IPAddress?> ResolveAsync(string address, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        if (IPAddress.TryParse(address, out IPAddress? parsed))
        {
            return parsed;
        }

        try
        {
            IPAddress[] found = await Dns.GetHostAddressesAsync(address, cancellationToken).ConfigureAwait(false);
            return found.Length > 0 ? found[0] : null;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }
}
