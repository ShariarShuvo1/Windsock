using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Windsock.Core.Adapters;

/// <summary>What kind of address an address is, from its number alone.</summary>
public enum AddressScope
{
    /// <summary>Not an address anything can reach: all zeros.</summary>
    Unspecified,

    /// <summary>This PC talking to itself: 127.0.0.0/8 and ::1.</summary>
    Loopback,

    /// <summary>Made up by the adapter for the link alone: 169.254.0.0/16 and fe80::/10.</summary>
    LinkLocal,

    /// <summary>A home or office network's own range: 10/8, 172.16/12, 192.168/16 (RFC 1918).</summary>
    Private,

    /// <summary>
    /// The range providers use behind carrier-grade NAT: 100.64.0.0/10 (RFC 6598).
    /// An address here means the provider shares one public address among many customers.
    /// </summary>
    SharedCarrier,

    /// <summary>An IPv6 unique local address, fc00::/7: IPv6's own private range.</summary>
    UniqueLocal,

    /// <summary>The deprecated IPv6 site-local range, fec0::/10, which Windows still shows as placeholder DNS servers.</summary>
    SiteLocal,

    /// <summary>A multicast group.</summary>
    Multicast,

    /// <summary>Reserved for documentation, benchmarking or future use; never seen on a real link.</summary>
    Reserved,

    /// <summary>An address the internet can route to.</summary>
    Public,
}

/// <summary>Sorts addresses into <see cref="AddressScope"/>s.</summary>
public static class AddressScopes
{
    /// <summary>The scope of an address.</summary>
    public static AddressScope Of(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            return Of(address.MapToIPv4());
        }

        Span<byte> bytes = stackalloc byte[16];

        if (!address.TryWriteBytes(bytes, out int written))
        {
            return AddressScope.Unspecified;
        }

        return address.AddressFamily == AddressFamily.InterNetwork && written == 4
            ? OfFour(BinaryPrimitives.ReadUInt32BigEndian(bytes))
            : OfSix(bytes[..written]);
    }

    /// <summary>The scope of an address written as text, or nothing when it is not one.</summary>
    public static AddressScope? Of(string? text) =>
        IPAddress.TryParse(text, out IPAddress? parsed) ? Of(parsed) : null;

    /// <summary>Whether an address is in the carrier-grade NAT range, 100.64.0.0/10.</summary>
    public static bool IsSharedCarrier(IPAddress address) => Of(address) == AddressScope.SharedCarrier;

    /// <summary>Whether the internet could route to an address.</summary>
    public static bool IsPublic(IPAddress address) => Of(address) == AddressScope.Public;

    private static AddressScope OfFour(uint value)
    {
        byte first = (byte)(value >> 24);

        return value switch
        {
            0 => AddressScope.Unspecified,
            _ when first == 127 => AddressScope.Loopback,
            _ when (value >> 16) == 0xA9FE => AddressScope.LinkLocal,
            _ when first == 10 || (value >> 20) == 0xAC1 || (value >> 16) == 0xC0A8 => AddressScope.Private,
            _ when (value >> 22) == 0x191 => AddressScope.SharedCarrier,
            _ when first is >= 224 and <= 239 => AddressScope.Multicast,

            // 0/8, 192.0.0/24 (protocol assignments), 192.0.2/24, 198.51.100/24
            // and 203.0.113/24 (documentation), 198.18/15 (benchmarking),
            // 240/4 (future use) and the broadcast address.
            _ when first == 0
                || (value >> 8) == 0xC00000
                || (value >> 8) == 0xC00002
                || (value >> 8) == 0xC63364
                || (value >> 8) == 0xCB0071
                || (value >> 17) == 0x6309
                || first >= 240 => AddressScope.Reserved,
            _ => AddressScope.Public,
        };
    }

    private static AddressScope OfSix(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16)
        {
            return AddressScope.Unspecified;
        }

        ulong high = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        ulong low = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);

        if (high == 0 && low == 0)
        {
            return AddressScope.Unspecified;
        }

        if (high == 0 && low == 1)
        {
            return AddressScope.Loopback;
        }

        return bytes[0] switch
        {
            0xFF => AddressScope.Multicast,
            0xFE when (bytes[1] & 0xC0) == 0x80 => AddressScope.LinkLocal,
            0xFE when (bytes[1] & 0xC0) == 0xC0 => AddressScope.SiteLocal,
            0xFC or 0xFD => AddressScope.UniqueLocal,

            // 2001:db8::/32 is for documentation; everything else in 2000::/3
            // is global unicast.
            _ when (high >> 32) == 0x20010DB8 => AddressScope.Reserved,
            _ when (bytes[0] & 0xE0) == 0x20 => AddressScope.Public,
            _ => AddressScope.Reserved,
        };
    }
}
