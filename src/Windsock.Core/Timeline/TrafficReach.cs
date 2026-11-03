using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Windsock.Core.Adapters;

namespace Windsock.Core.Timeline;

/// <summary>One network the PC is on directly: an address of an adapter's own and its prefix.</summary>
/// <param name="IsSix">Whether it is an IPv6 network.</param>
/// <param name="Network">The network's address, as a number, the bits past the prefix cleared.</param>
/// <param name="Length">The prefix length.</param>
public readonly record struct OnLinkPrefix(bool IsSix, UInt128 Network, int Length)
{
    /// <summary>The network an adapter's address is on, or null for one that names no useful network.</summary>
    /// <remarks>
    /// Too-short prefixes are refused - shorter than /48 for IPv6 or /16 for
    /// IPv4 - since a tunnel or a misconfigured adapter declaring one would
    /// make half the internet look local. Single-address prefixes say nothing.
    /// </remarks>
    public static OnLinkPrefix? Of(IPAddress address, int length)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        bool six = address.AddressFamily == AddressFamily.InterNetworkV6;
        int bits = six ? 128 : 32;

        if (length < (six ? 48 : 16) || length >= bits)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[16];
        return address.TryWriteBytes(bytes, out int written)
            ? new OnLinkPrefix(six, Mask(Number(bytes[..written]), length, bits), length)
            : null;
    }

    /// <summary>Whether an address, already a number, is on this network.</summary>
    internal bool Holds(bool six, UInt128 value) =>
        six == IsSix && Mask(value, Length, six ? 128 : 32) == Network;

    internal static UInt128 Number(ReadOnlySpan<byte> bytes)
    {
        UInt128 value = 0;

        foreach (byte each in bytes)
        {
            value = (value << 8) | each;
        }

        return value;
    }

    private static UInt128 Mask(UInt128 value, int length, int bits) =>
        length <= 0 ? 0 : value >> (bits - length) << (bits - length);
}

/// <summary>Whether traffic to an address stays on the local network.</summary>
public static class TrafficReach
{
    private static OnLinkPrefix[]? _onLink;
    private static int _following;

    /// <summary>
    /// Whether an address is on a local network: a private range, link-local,
    /// an IPv6 unique local or site-local address, multicast, the broadcast
    /// address, or an address on a network one of this PC's adapters is on
    /// directly - which is how a home's IPv6 devices are reached, by global
    /// addresses that look like anyone's. Bytes to these never cross the
    /// internet connection, so no data plan counts them.
    /// </summary>
    /// <remarks>
    /// Carrier-grade NAT (100.64.0.0/10) is not local: it is the provider's
    /// side of the connection, and traffic to it goes through the line. Called
    /// for every packet the trace sees, so it allocates nothing once the
    /// adapters' networks are known.
    /// </remarks>
    public static bool IsLocal(IPAddress? address) => IsLocal(address, OnLink());

    /// <summary>The same, against a given list of the PC's own networks.</summary>
    internal static bool IsLocal(IPAddress? address, ReadOnlySpan<OnLinkPrefix> onLink)
    {
        if (address is null)
        {
            return false;
        }

        AddressScope scope = AddressScopes.Of(address);

        return scope is AddressScope.Private
                or AddressScope.LinkLocal
                or AddressScope.UniqueLocal
                or AddressScope.SiteLocal
                or AddressScope.Multicast
            || IsBroadcast(address)
            || IsOnLink(address, onLink);
    }

    /// <summary>The networks the PC's adapters are on now, from Windows.</summary>
    public static IReadOnlyList<OnLinkPrefix> ReadOnLink()
    {
        List<OnLinkPrefix> prefixes = [];

        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (OnLinkPrefix.Of(unicast.Address, unicast.PrefixLength) is { } prefix && !prefixes.Contains(prefix))
                    {
                        prefixes.Add(prefix);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException or PlatformNotSupportedException)
        {
        }

        return prefixes;
    }

    private static ReadOnlySpan<OnLinkPrefix> OnLink()
    {
        if (Volatile.Read(ref _onLink) is { } known)
        {
            return known;
        }

        // Read once on first use, and again whenever Windows says an address
        // changed - joining another network, a new IPv6 prefix.
        if (Interlocked.Exchange(ref _following, 1) == 0)
        {
            NetworkChange.NetworkAddressChanged += (_, _) => Volatile.Write(ref _onLink, [.. ReadOnLink()]);
        }

        OnLinkPrefix[] read = [.. ReadOnLink()];
        Volatile.Write(ref _onLink, read);
        return read;
    }

    private static bool IsOnLink(IPAddress address, ReadOnlySpan<OnLinkPrefix> onLink)
    {
        if (onLink.IsEmpty)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[16];

        if (!address.TryWriteBytes(bytes, out int written))
        {
            return false;
        }

        bool six = written == 16;
        ReadOnlySpan<byte> used = bytes[..written];

        // An IPv4 address carried in IPv6 is judged as IPv4.
        if (six && address.IsIPv4MappedToIPv6)
        {
            used = bytes[12..16];
            six = false;
        }

        UInt128 value = OnLinkPrefix.Number(used);

        foreach (OnLinkPrefix prefix in onLink)
        {
            if (prefix.Holds(six, value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBroadcast(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];

        return address.TryWriteBytes(bytes, out int written)
            && written == 4
            && bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255;
    }
}
