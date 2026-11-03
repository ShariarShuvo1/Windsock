using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Windsock.Core.Home;

/// <summary>How wide a block of hardware addresses the IEEE assigned.</summary>
public enum OuiBlock
{
    /// <summary>MA-L, the classic OUI: the first 24 bits.</summary>
    Large,

    /// <summary>MA-M: the first 28 bits.</summary>
    Medium,

    /// <summary>MA-S, and the older IAB: the first 36 bits.</summary>
    Small,
}

/// <summary>One block of hardware addresses and the organisation it was assigned to.</summary>
/// <param name="Block">How wide the block is.</param>
/// <param name="Prefix">The block's leading bits, as a number: 24, 28 or 36 of them.</param>
/// <param name="Organisation">Who it was assigned to.</param>
public readonly record struct OuiAssignment(OuiBlock Block, ulong Prefix, string Organisation);

/// <summary>
/// Builds the maker table Windsock ships, from the IEEE Registration
/// Authority's public listings.
/// </summary>
/// <remarks>
/// <para>
/// The layout, all integers little-endian:
/// </para>
/// <code>
///  0  8  "WSCKOUI1"
///  8  4  the day the listings were retrieved, as yyyymmdd
/// 12  4  names            16  4  MA-L rows      20  4  MA-M rows      24  4  MA-S rows
/// 28  4  where the name offsets start (names + 1 of them, u32, into the name bytes)
/// 32  4  where the name bytes start (UTF-8)
/// 36  4  where the MA-L rows start: 5 bytes each, u24 prefix then u16 name
/// 40  4  where the MA-M rows start: 6 bytes each, u32 prefix then u16 name
/// 44  4  where the MA-S rows start: 7 bytes each, u40 prefix then u16 name
/// </code>
/// <para>
/// Rows are sorted by prefix within their table, so a lookup is three binary
/// searches, the narrowest block first. The organisations' postal addresses are
/// left out: they are most of the listing's size and no use here. So are the
/// rows that name nobody - "Private" (the holder asked not to be listed) and
/// "IEEE Registration Authority" (an MA-L carved into MA-M or MA-S blocks) -
/// which leaves an address in a block nobody holds unnamed, as it should be.
/// </para>
/// </remarks>
public static class OuiTableWriter
{
    /// <summary>The first eight bytes of the file.</summary>
    public const string Magic = "WSCKOUI1";

    /// <summary>Where the rows start.</summary>
    internal const int HeaderLength = 48;

    private const int MostNames = ushort.MaxValue;

    /// <summary>Organisations that stand for nobody in particular.</summary>
    private static readonly HashSet<string> Nobody = new(StringComparer.OrdinalIgnoreCase)
    {
        "Private",
        "IEEE Registration Authority",
    };

    /// <summary>
    /// Reads one of the IEEE's CSV listings (oui.csv, mam.csv, oui36.csv,
    /// iab.csv): "Registry,Assignment,Organization Name,Organization Address".
    /// CID rows are skipped, since a company ID is never a hardware address.
    /// </summary>
    public static IEnumerable<OuiAssignment> ReadCsv(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        bool first = true;

        while (reader.ReadLine() is { } line)
        {
            if (first)
            {
                first = false;

                if (line.StartsWith("Registry", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            List<string> fields = Fields(line);

            if (fields.Count < 3)
            {
                continue;
            }

            OuiBlock? block = fields[0].Trim() switch
            {
                "MA-L" => OuiBlock.Large,
                "MA-M" => OuiBlock.Medium,
                "MA-S" or "IAB" => OuiBlock.Small,
                _ => null,
            };

            string assignment = fields[1].Trim();
            int digits = block switch
            {
                OuiBlock.Large => 6,
                OuiBlock.Medium => 7,
                OuiBlock.Small => 9,
                _ => 0,
            };

            if (block is null
                || assignment.Length != digits
                || !ulong.TryParse(assignment, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong prefix))
            {
                continue;
            }

            yield return new OuiAssignment(block.Value, prefix, fields[2]);
        }
    }

    /// <summary>The table, ready to write to a file.</summary>
    public static byte[] Build(IEnumerable<OuiAssignment> assignments, DateOnly retrieved)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        Dictionary<string, int> names = new(StringComparer.Ordinal);
        List<string> ordered = [];
        SortedDictionary<ulong, int>[] tables = [new(), new(), new()];

        foreach (OuiAssignment each in assignments)
        {
            string name = Tidy(each.Organisation);

            if (name.Length == 0 || Nobody.Contains(name))
            {
                continue;
            }

            SortedDictionary<ulong, int> table = tables[(int)each.Block];

            if (table.ContainsKey(each.Prefix))
            {
                continue;
            }

            if (!names.TryGetValue(name, out int index))
            {
                index = ordered.Count;

                if (index >= MostNames)
                {
                    throw new InvalidOperationException("More organisations than the table's name index can hold.");
                }

                names[name] = index;
                ordered.Add(name);
            }

            table[each.Prefix] = index;
        }

        byte[][] encoded = [.. ordered.Select(Encoding.UTF8.GetBytes)];
        int nameBytes = encoded.Sum(each => each.Length);

        int offsetsAt = HeaderLength;
        int textAt = offsetsAt + (4 * (ordered.Count + 1));
        int largeAt = textAt + nameBytes;
        int mediumAt = largeAt + (5 * tables[0].Count);
        int smallAt = mediumAt + (6 * tables[1].Count);
        int total = smallAt + (7 * tables[2].Count);

        byte[] file = new byte[total];
        Span<byte> span = file;

        Encoding.ASCII.GetBytes(Magic, span);
        WriteInt(span, 8, (retrieved.Year * 10000) + (retrieved.Month * 100) + retrieved.Day);
        WriteInt(span, 12, ordered.Count);
        WriteInt(span, 16, tables[0].Count);
        WriteInt(span, 20, tables[1].Count);
        WriteInt(span, 24, tables[2].Count);
        WriteInt(span, 28, offsetsAt);
        WriteInt(span, 32, textAt);
        WriteInt(span, 36, largeAt);
        WriteInt(span, 40, mediumAt);
        WriteInt(span, 44, smallAt);

        int written = 0;

        for (int index = 0; index < encoded.Length; index++)
        {
            WriteInt(span, offsetsAt + (4 * index), written);
            encoded[index].CopyTo(span[(textAt + written)..]);
            written += encoded[index].Length;
        }

        WriteInt(span, offsetsAt + (4 * encoded.Length), written);

        WriteRows(span[largeAt..], tables[0], 3);
        WriteRows(span[mediumAt..], tables[1], 4);
        WriteRows(span[smallAt..], tables[2], 5);

        return file;
    }

    /// <summary>
    /// An organisation's name as listed, trimmed, with runs of spaces made one:
    /// the listing pads some names and breaks others across spaces.
    /// </summary>
    internal static string Tidy(string name)
    {
        StringBuilder tidy = new(name.Length);
        bool space = false;

        foreach (char each in name.Trim())
        {
            if (char.IsWhiteSpace(each) || char.IsControl(each))
            {
                space = tidy.Length > 0;
                continue;
            }

            if (space)
            {
                tidy.Append(' ');
                space = false;
            }

            tidy.Append(each);
        }

        return tidy.ToString();
    }

    private static void WriteRows(Span<byte> destination, SortedDictionary<ulong, int> table, int keyBytes)
    {
        int at = 0;
        Span<byte> key = stackalloc byte[8];

        foreach ((ulong prefix, int name) in table)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(key, prefix);
            key[..keyBytes].CopyTo(destination[at..]);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[(at + keyBytes)..], (ushort)name);
            at += keyBytes + 2;
        }
    }

    private static void WriteInt(Span<byte> span, int at, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(span[at..], value);

    /// <summary>Splits one CSV line, honouring double quotes and doubled quotes inside them.</summary>
    internal static List<string> Fields(string line)
    {
        List<string> fields = [];
        StringBuilder field = new();
        bool quoted = false;

        for (int index = 0; index < line.Length; index++)
        {
            char each = line[index];

            if (quoted)
            {
                if (each == '"')
                {
                    if (index + 1 < line.Length && line[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(each);
                }
            }
            else if (each == '"')
            {
                quoted = true;
            }
            else if (each == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(each);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }
}
