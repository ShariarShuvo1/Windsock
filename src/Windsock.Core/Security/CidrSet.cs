using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace Windsock.Core.Security;

/// <summary>One entry of a blocklist: a network, and what the list says about it.</summary>
/// <param name="Cidr">The network as the list wrote it, such as <c>1.10.16.0/20</c>, or a single address.</param>
/// <param name="Reference">The list's own reference for it, such as Spamhaus's SBL number, or what the address does.</param>
public sealed record BlocklistEntry(string Cidr, string? Reference);

/// <summary>
/// A set of networks that answers "which entry holds this address" by binary
/// search over sorted ranges.
/// </summary>
/// <remarks>
/// Ranges are made disjoint as they are added: one inside another that came
/// first is dropped (the wider entry answers for it), and one that overlaps a
/// neighbour is cut to start after it. A blocklist of a few thousand networks
/// is a few hundred kilobytes and answers in well under a microsecond.
/// </remarks>
public sealed class CidrSet
{
    private readonly Range<uint>[] _four;
    private readonly Range<UInt128>[] _six;

    private CidrSet(Range<uint>[] four, Range<UInt128>[] six)
    {
        _four = four;
        _six = six;
    }

    /// <summary>A set holding nothing.</summary>
    public static CidrSet Empty { get; } = new([], []);

    /// <summary>How many disjoint ranges the set holds.</summary>
    public int Count => _four.Length + _six.Length;

    /// <summary>Builds a set from entries; ones whose network cannot be read are skipped.</summary>
    public static CidrSet From(IEnumerable<BlocklistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        List<Range<uint>> four = [];
        List<Range<UInt128>> six = [];

        foreach (BlocklistEntry entry in entries)
        {
            if (!TryParse(entry.Cidr, out IPAddress? network, out int prefix))
            {
                continue;
            }

            if (network.AddressFamily == AddressFamily.InterNetwork)
            {
                uint start = Four(network) & Mask32(prefix);
                four.Add(new Range<uint>(start, start | ~Mask32(prefix), entry));
            }
            else
            {
                UInt128 start = Six(network) & Mask128(prefix);
                six.Add(new Range<UInt128>(start, start | ~Mask128(prefix), entry));
            }
        }

        return new CidrSet(Disjoint(four), Disjoint(six));
    }

    /// <summary>The entry holding an address, or null.</summary>
    public BlocklistEntry? Find(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily == AddressFamily.InterNetwork
            ? Search(_four, Four(address))
            : address.AddressFamily == AddressFamily.InterNetworkV6 ? Search(_six, Six(address)) : null;
    }

    /// <summary>
    /// Reads <c>address/prefix</c>, or a bare address as a network of one. The
    /// prefix must fit the family.
    /// </summary>
    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IPAddress? network, out int prefix)
    {
        network = null;
        prefix = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        int slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        string addressPart = slash < 0 ? trimmed : trimmed[..slash];

        if (!IPAddress.TryParse(addressPart, out IPAddress? parsed)
            || parsed.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return false;
        }

        int bits = parsed.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;

        if (slash < 0)
        {
            prefix = bits;
        }
        else if (!int.TryParse(trimmed.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out prefix)
            || prefix < 0
            || prefix > bits)
        {
            return false;
        }

        network = parsed;
        return true;
    }

    private static BlocklistEntry? Search<T>(Range<T>[] ranges, T value)
        where T : struct, IComparable<T>
    {
        int low = 0;
        int high = ranges.Length - 1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            Range<T> range = ranges[middle];

            if (value.CompareTo(range.Start) < 0)
            {
                high = middle - 1;
            }
            else if (value.CompareTo(range.End) > 0)
            {
                low = middle + 1;
            }
            else
            {
                return range.Entry;
            }
        }

        return null;
    }

    private static Range<T>[] Disjoint<T>(List<Range<T>> ranges)
        where T : struct, IComparable<T>, IIncrementOperators<T>
    {
        // Earlier start first; of two starting together, the wider first, so it
        // swallows the narrower.
        ranges.Sort(static (a, b) =>
        {
            int byStart = a.Start.CompareTo(b.Start);
            return byStart != 0 ? byStart : b.End.CompareTo(a.End);
        });

        List<Range<T>> kept = new(ranges.Count);

        foreach (Range<T> range in ranges)
        {
            if (kept.Count == 0)
            {
                kept.Add(range);
                continue;
            }

            Range<T> last = kept[^1];

            if (range.End.CompareTo(last.End) <= 0)
            {
                continue;
            }

            if (range.Start.CompareTo(last.End) <= 0)
            {
                T after = last.End;
                after++;
                kept.Add(range with { Start = after });
                continue;
            }

            kept.Add(range);
        }

        return [.. kept];
    }

    private static uint Four(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        return address.TryWriteBytes(bytes, out int written) && written == 4
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes)
            : 0;
    }

    private static UInt128 Six(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        return address.TryWriteBytes(bytes, out int written) && written == 16
            ? BinaryPrimitives.ReadUInt128BigEndian(bytes)
            : UInt128.Zero;
    }

    private static uint Mask32(int prefix) => prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);

    private static UInt128 Mask128(int prefix) => prefix == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - prefix);

    private readonly record struct Range<T>(T Start, T End, BlocklistEntry Entry);
}
