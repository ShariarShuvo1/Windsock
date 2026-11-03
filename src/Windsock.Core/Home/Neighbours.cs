using System.Net;
using System.Net.Sockets;

namespace Windsock.Core.Home;

/// <summary>How sure Windows is that a neighbour is there (NL_NEIGHBOR_STATE in nldef.h).</summary>
public enum NeighbourState
{
    Unreachable = 0,

    Incomplete = 1,

    Probe = 2,

    Delay = 3,

    /// <summary>Known, but not confirmed lately. Still a device that was there.</summary>
    Stale = 4,

    /// <summary>Confirmed lately.</summary>
    Reachable = 5,

    /// <summary>Set by hand or by Windows itself: broadcast and multicast entries, mostly.</summary>
    Permanent = 6,
}

/// <summary>One row of Windows' neighbour table: an address on a local link and the hardware behind it.</summary>
/// <param name="Address">The neighbour's IP address.</param>
/// <param name="Mac">Its hardware address.</param>
/// <param name="InterfaceIndex">The interface it was seen on.</param>
/// <param name="State">How sure Windows is that it is there.</param>
/// <param name="IsRouter">Whether IPv6 neighbour discovery says it routes.</param>
/// <param name="IsUnreachable">Whether Windows marked it unreachable.</param>
public readonly record struct Neighbour(
    IPAddress Address,
    MacAddress Mac,
    uint InterfaceIndex,
    NeighbourState State,
    bool IsRouter,
    bool IsUnreachable);

/// <summary>Reads Windows' neighbour table, which this PC keeps for the hosts it has talked to on each link.</summary>
public interface INeighbourSource
{
    /// <summary>Every row as it stands, IPv4 and IPv6. Never throws; an empty list is a reading.</summary>
    IReadOnlyList<Neighbour> Read();

    /// <summary>
    /// Asks the network which hardware holds an IPv4 address, from a given local
    /// address, and waits for the answer: an ARP request, which stays on the
    /// link. Nothing when nobody answered. Blocks, for up to a few seconds.
    /// </summary>
    MacAddress? Ask(IPAddress target, IPAddress from);
}

/// <summary>Which rows of the neighbour table are devices.</summary>
/// <remarks>
/// Measured on the development machine's home LAN: 66 rows, 59 of them
/// Permanent (broadcast, multicast, 224.0.0.22 and friends), 5 Unreachable
/// (127.0.0.1 and 169.254.169.254 among them), one Stale and one Reachable -
/// the router. What is left after this is the devices.
/// </remarks>
public static class NeighbourFilter
{
    /// <summary>Whether a row stands for a device on the link.</summary>
    public static bool IsDevice(Neighbour row)
    {
        if (row.IsUnreachable
            || row.State is not (NeighbourState.Reachable or NeighbourState.Stale or NeighbourState.Delay or NeighbourState.Probe)
            || !row.Mac.IsUnicast)
        {
            return false;
        }

        return IsDeviceAddress(row.Address);
    }

    /// <summary>Whether an address could belong to one device rather than a group or this machine.</summary>
    public static bool IsDeviceAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> octets = stackalloc byte[4];
            address.TryWriteBytes(octets, out _);

            // Multicast, the limited broadcast, and the cloud metadata address
            // Windows keeps an unreachable row for.
            return octets[0] is < 224 and not 0
                && !(octets[0] == 169 && octets[1] == 254 && octets[2] == 169 && octets[3] == 254);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6Multicast;
    }
}
