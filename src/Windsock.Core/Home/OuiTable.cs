using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Windsock.Core.Home;

/// <summary>
/// Who made an adapter, from the table of IEEE assignments shipped with
/// Windsock (see <see cref="OuiTableWriter"/> for its layout).
/// </summary>
/// <remarks>
/// Looked up entirely on this machine: nothing is asked of anyone to name a
/// maker. The table is a snapshot of the day it was built, so an address block
/// assigned since then has no maker until the next release carries a newer one.
/// </remarks>
public sealed class OuiTable
{
    /// <summary>The file's name in the Data folder beside Windsock.</summary>
    public const string FileName = "makers.oui";

    private readonly byte[] _file;
    private readonly int _names;
    private readonly int _offsetsAt;
    private readonly int _textAt;
    private readonly (int At, int Rows)[] _tables;
    private readonly string?[] _decoded;

    private OuiTable(byte[] file)
    {
        if (file.Length < OuiTableWriter.HeaderLength
            || !file.AsSpan(0, 8).SequenceEqual(Encoding.ASCII.GetBytes(OuiTableWriter.Magic)))
        {
            throw new InvalidDataException("This is not a Windsock maker table.");
        }

        int retrieved = Read(8);
        _names = Read(12);
        _offsetsAt = Read(28);
        _textAt = Read(32);
        _tables = [(Read(36), Read(16)), (Read(40), Read(20)), (Read(44), Read(24))];

        int end = _tables[2].At + (7 * _tables[2].Rows);

        if (_names < 0
            || _offsetsAt < OuiTableWriter.HeaderLength
            || _textAt < _offsetsAt + (4 * (_names + 1))
            || end > file.Length
            || _tables.Any(table => table.At < _textAt || table.Rows < 0))
        {
            throw new InvalidDataException("The maker table is damaged.");
        }

        Retrieved = DateOnly.TryParseExact(
            retrieved.ToString("D8", CultureInfo.InvariantCulture),
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateOnly day) ? day : DateOnly.MinValue;
        _decoded = new string?[_names];
        _file = file;

        int Read(int at) => BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at, 4));
    }

    /// <summary>The day the IEEE listings were retrieved.</summary>
    public DateOnly Retrieved { get; }

    /// <summary>How many organisations the table names.</summary>
    public int Organisations => _names;

    /// <summary>How many blocks it holds, of all three widths.</summary>
    public int Blocks => _tables.Sum(table => table.Rows);

    /// <summary>Reads a table from its bytes.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a maker table.</exception>
    public static OuiTable Load(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new OuiTable(file);
    }

    /// <summary>Opens the table shipped beside Windsock, or nothing when it is missing or unreadable.</summary>
    public static OuiTable? Open(string? path = null)
    {
        string file = path ?? Path.Combine(AppContext.BaseDirectory, "Data", FileName);

        try
        {
            return File.Exists(file) ? new OuiTable(File.ReadAllBytes(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The organisation the address's block was assigned to, or nothing: for a
    /// private (locally administered) or group address, which no maker
    /// assigned, and for a block the table does not name.
    /// </summary>
    public string? Find(MacAddress address)
    {
        if (!address.HasMaker)
        {
            return null;
        }

        int name = Search(2, address.Prefix36, 5);

        if (name < 0)
        {
            name = Search(1, address.Prefix28, 4);
        }

        if (name < 0)
        {
            name = Search(0, address.Prefix24, 3);
        }

        return name < 0 ? null : Name(name);
    }

    private int Search(int table, ulong prefix, int keyBytes)
    {
        (int at, int rows) = _tables[table];
        int width = keyBytes + 2;
        int low = 0;
        int high = rows - 1;
        Span<byte> key = stackalloc byte[8];

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            int row = at + (middle * width);

            key.Clear();
            _file.AsSpan(row, keyBytes).CopyTo(key);
            ulong found = BinaryPrimitives.ReadUInt64LittleEndian(key);

            if (found == prefix)
            {
                return BinaryPrimitives.ReadUInt16LittleEndian(_file.AsSpan(row + keyBytes, 2));
            }

            if (found < prefix)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return -1;
    }

    private string? Name(int index)
    {
        if (index >= _names)
        {
            return null;
        }

        if (Volatile.Read(ref _decoded[index]) is { } known)
        {
            return known;
        }

        int start = BinaryPrimitives.ReadInt32LittleEndian(_file.AsSpan(_offsetsAt + (4 * index), 4));
        int end = BinaryPrimitives.ReadInt32LittleEndian(_file.AsSpan(_offsetsAt + (4 * (index + 1)), 4));

        if (start < 0 || end < start || _textAt + end > _file.Length)
        {
            return null;
        }

        string name = Encoding.UTF8.GetString(_file, _textAt + start, end - start);
        Volatile.Write(ref _decoded[index], name);
        return name;
    }
}
