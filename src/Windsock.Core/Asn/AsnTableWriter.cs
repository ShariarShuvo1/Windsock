using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Windsock.Core.Asn;

/// <summary>One range of addresses and the network announcing it.</summary>
/// <param name="Start">The first address.</param>
/// <param name="End">The last address.</param>
/// <param name="Number">The AS number, 0 for space nobody announces.</param>
/// <param name="Country">The two-letter country, or empty.</param>
/// <param name="Name">The AS's registered name.</param>
public sealed record AsnRange(IPAddress Start, IPAddress End, uint Number, string Country, string Name);

/// <summary>Builds the <see cref="AsnTable"/> file from ranges, and reads iptoasn.com's TSV.</summary>
/// <remarks>
/// Kept in Windsock.Core, beside the reader, so the tests can write a table and
/// read it back, and so a later version can refresh the table on request
/// without a second implementation of the format.
/// </remarks>
public static class AsnTableWriter
{
    /// <summary>Rows per index block: a lookup decodes at most this many.</summary>
    public const int Block = 64;

    /// <summary>
    /// Reads iptoasn.com's ip2asn TSV: start, end, AS number, country, name,
    /// tab-separated, no header. Unrouted space is AS 0, country "None".
    /// </summary>
    public static IEnumerable<AsnRange> ReadIptoasn(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        while (reader.ReadLine() is { } line)
        {
            string[] fields = line.Split('\t');

            if (fields.Length < 5
                || !IPAddress.TryParse(fields[0], out IPAddress? start)
                || !IPAddress.TryParse(fields[1], out IPAddress? end)
                || !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint number))
            {
                continue;
            }

            string country = fields[3].Length == 2 ? fields[3].ToUpperInvariant() : string.Empty;
            yield return new AsnRange(start, end, number, country, fields[4].Trim());
        }
    }

    /// <summary>Writes a table.</summary>
    /// <param name="ranges">Ranges in any order, both families mixed.</param>
    /// <param name="output">Where to write.</param>
    /// <param name="date">When the data was taken.</param>
    /// <returns>How many rows of each family were written, after merging.</returns>
    public static (int Four, int Six, int Systems) Write(IEnumerable<AsnRange> ranges, Stream output, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentNullException.ThrowIfNull(output);

        Dictionary<uint, int> systemIndex = [];
        List<AsnRange> systems = [];
        List<(ulong Start, ulong End, int System)> four = [];
        List<(ulong Start, ulong End, int System)> six = [];

        foreach (AsnRange range in ranges)
        {
            int system = 0;

            if (range.Number != 0)
            {
                if (!systemIndex.TryGetValue(range.Number, out int index))
                {
                    index = systems.Count;
                    systemIndex[range.Number] = index;
                    systems.Add(range);
                }

                system = index + 1;
            }

            if (range.Start.AddressFamily == AddressFamily.InterNetwork)
            {
                four.Add((Key(range.Start), Key(range.End), system));
            }
            else if (range.Start.AddressFamily == AddressFamily.InterNetworkV6 && !range.Start.IsIPv4MappedToIPv6)
            {
                six.Add((Key(range.Start), Key(range.End), system));
            }
        }

        List<(ulong Start, int System)> fourRows = Merge(four, uint.MaxValue);
        List<(ulong Start, int System)> sixRows = Merge(six, ulong.MaxValue);

        byte[] names;
        byte[] systemBytes = new byte[systems.Count * 12];

        using (MemoryStream nameStream = new())
        {
            for (int each = 0; each < systems.Count; each++)
            {
                byte[] name = Encoding.UTF8.GetBytes(systems[each].Name);
                int length = Math.Min(name.Length, ushort.MaxValue);
                Span<byte> slot = systemBytes.AsSpan(each * 12, 12);

                BinaryPrimitives.WriteUInt32LittleEndian(slot, systems[each].Number);
                BinaryPrimitives.WriteUInt32LittleEndian(slot[4..], (uint)nameStream.Position);
                BinaryPrimitives.WriteUInt16LittleEndian(slot[8..], (ushort)length);

                string country = systems[each].Country.Length == 2 ? systems[each].Country : "  ";
                slot[10] = (byte)country[0];
                slot[11] = (byte)country[1];

                nameStream.Write(name, 0, length);
            }

            names = nameStream.ToArray();
        }

        (byte[] fourIndex, byte[] fourData) = Rows(fourRows, six: false);
        (byte[] sixIndex, byte[] sixData) = Rows(sixRows, six: true);

        uint at = AsnTable.HeaderSize;
        uint systemsAt = at;
        at += (uint)systemBytes.Length;
        uint namesAt = at;
        at += (uint)names.Length;
        uint fourIndexAt = at;
        at += (uint)fourIndex.Length;
        uint fourDataAt = at;
        at += (uint)fourData.Length;
        uint sixIndexAt = at;
        at += (uint)sixIndex.Length;
        uint sixDataAt = at;
        at += (uint)sixData.Length;

        byte[] header = new byte[AsnTable.HeaderSize];
        Encoding.ASCII.GetBytes(AsnTable.Magic).CopyTo(header, 0);
        uint[] fields =
        [
            (uint)((date.Year * 10000) + (date.Month * 100) + date.Day),
            (uint)systems.Count,
            (uint)names.Length,
            (uint)fourRows.Count,
            (uint)sixRows.Count,
            Block,
            systemsAt,
            namesAt,
            fourIndexAt,
            fourDataAt,
            sixIndexAt,
            sixDataAt,
            at,
            0,
        ];

        for (int each = 0; each < fields.Length; each++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8 + (each * 4)), fields[each]);
        }

        output.Write(header);
        output.Write(systemBytes);
        output.Write(names);
        output.Write(fourIndex);
        output.Write(fourData);
        output.Write(sixIndex);
        output.Write(sixData);

        return (fourRows.Count, sixRows.Count, systems.Count);
    }

    /// <summary>An address as a sortable key: IPv4 whole, IPv6 its top 64 bits.</summary>
    public static ulong Key(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out int written);

        return written == 4 ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    /// <summary>
    /// Sorts ranges, fills gaps with unrouted rows, joins neighbours that name
    /// the same system, and lets a later range that starts on the same key
    /// replace an earlier one.
    /// </summary>
    /// <param name="ranges">The ranges, in the source's order.</param>
    /// <param name="top">The highest key there is: 255.255.255.255 for IPv4, the top /64 for IPv6.</param>
    /// <remarks>
    /// The sort is stable, or "later replaces earlier" would depend on how the
    /// sort happened to order two ranges starting together. A range reaching
    /// <paramref name="top"/> leaves nothing after it: an unrouted row past
    /// 255.255.255.255 would not fit an IPv4 row and wrapped round to 0.0.0.0.
    /// </remarks>
    internal static List<(ulong Start, int System)> Merge(List<(ulong Start, ulong End, int System)> ranges, ulong top)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        List<(ulong Start, int System)> rows = [];
        ulong? expected = null;
        bool topped = false;

        foreach ((ulong start, ulong end, int system) in ranges.OrderBy(each => each.Start))
        {
            if (!topped && expected is { } next && start > next)
            {
                Add(rows, next, 0);
            }

            Add(rows, start, system);

            if (end >= top)
            {
                topped = true;
                expected = null;
            }
            else if (!topped)
            {
                expected = Math.Max(end + 1, expected ?? 0);
            }
        }

        if (!topped && expected is { } tail && (rows.Count == 0 || rows[^1].System != 0))
        {
            Add(rows, tail, 0);
        }

        return rows;

        static void Add(List<(ulong Start, int System)> rows, ulong start, int system)
        {
            if (rows.Count > 0 && rows[^1].Start == start)
            {
                rows[^1] = (start, system);

                if (rows.Count > 1 && rows[^2].System == system)
                {
                    rows.RemoveAt(rows.Count - 1);
                }

                return;
            }

            if (rows.Count > 0 && rows[^1].System == system)
            {
                return;
            }

            rows.Add((start, system));
        }
    }

    private static (byte[] Index, byte[] Data) Rows(List<(ulong Start, int System)> rows, bool six)
    {
        using MemoryStream index = new();
        using MemoryStream data = new();
        Span<byte> scratch = stackalloc byte[12];
        ulong previous = 0;

        for (int each = 0; each < rows.Count; each++)
        {
            (ulong start, int system) = rows[each];

            if (each % Block == 0)
            {
                if (six)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(scratch, start);
                    BinaryPrimitives.WriteUInt32LittleEndian(scratch[8..], (uint)data.Position);
                    index.Write(scratch[..12]);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(scratch, (uint)start);
                    BinaryPrimitives.WriteUInt32LittleEndian(scratch[4..], (uint)data.Position);
                    index.Write(scratch[..8]);
                }

                previous = start;
            }

            WriteVarint(data, start - previous);
            WriteVarint(data, (ulong)system);
            previous = start;
        }

        return (index.ToArray(), data.ToArray());
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }
}
