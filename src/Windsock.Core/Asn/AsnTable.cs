using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Windsock.Core.Adapters;

namespace Windsock.Core.Asn;

/// <summary>
/// Which network an address belongs to - its autonomous system, and so the
/// organisation running it - from a table shipped with Windsock. Nothing is
/// looked up online.
/// </summary>
/// <remarks>
/// <para>
/// The file (<c>Data\asn.dat</c>) is built by <c>tools/asn</c> from iptoasn.com's
/// combined table (Public Domain, PDDL 1.0). Layout, little-endian:
/// </para>
/// <code>
///  0  "WSCKASN1"                      magic, 8 bytes
///  8  u32 data date, yyyymmdd         when the source was downloaded
/// 12  u32 systems                     autonomous systems listed
/// 16  u32 name bytes
/// 20  u32 IPv4 rows, 24 u32 IPv6 rows
/// 28  u32 rows per index block
/// 32  u32 offsets of: systems, names, IPv4 index, IPv4 rows, IPv6 index, IPv6 rows
/// 56  u32 file length
/// 60  u32 reserved
/// systems  12 bytes each: u32 AS number, u32 name offset, u16 name length, 2 ASCII country
/// names    UTF-8
/// v4 index 8 bytes a block: u32 first start, u32 offset of the block's rows
/// v6 index 12 bytes a block: u64 first start (an address's top 64 bits), u32 offset
/// rows     varint (start - previous start; 0 for a block's first row),
///          varint (system index + 1; 0 means not routed)
/// </code>
/// <para>
/// A row covers from its start to the next row's start. IPv6 is keyed on the
/// top 64 bits, which is as far as the internet routes (no prefix longer than
/// a /48 is carried in practice); a few ranges in the source start inside a
/// /64, and those collapse onto the row for that /64.
/// </para>
/// </remarks>
public sealed class AsnTable : IDisposable
{
    /// <summary>The 8 bytes every table starts with.</summary>
    public const string Magic = "WSCKASN1";

    /// <summary>The header's size.</summary>
    public const int HeaderSize = 64;

    /// <summary>The file's name beside the program.</summary>
    public const string FileName = "asn.dat";

    private const int SystemSize = 12;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly int _systems;
    private readonly int _v4Rows;
    private readonly int _v6Rows;
    private readonly int _block;
    private readonly long _systemsAt;
    private readonly long _namesAt;
    private readonly long _v4IndexAt;
    private readonly long _v4DataAt;
    private readonly long _v6IndexAt;
    private readonly long _v6DataAt;
    private readonly long _length;
    private readonly long _nameBytes;
    private readonly Dictionary<int, AutonomousSystem> _known = [];
    private readonly Lock _gate = new();

    private bool _disposed;

    private AsnTable(string path)
    {
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, 0, MemoryMappedFileAccess.Read);

        try
        {
            _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            byte[] header = new byte[HeaderSize];

            if (_view.ReadArray(0, header, 0, HeaderSize) != HeaderSize || !header.AsSpan(0, 8).SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
            {
                throw new InvalidDataException("This is not a Windsock network table.");
            }

            ReadOnlySpan<byte> fields = header;
            Date = Number(fields, 8);
            _nameBytes = Number(fields, 16);
            _length = Number(fields, 56);
            _systems = (int)Number(fields, 12);
            _v4Rows = (int)Number(fields, 20);
            _v6Rows = (int)Number(fields, 24);
            _block = (int)Number(fields, 28);
            _systemsAt = Number(fields, 32);
            _namesAt = Number(fields, 36);
            _v4IndexAt = Number(fields, 40);
            _v4DataAt = Number(fields, 44);
            _v6IndexAt = Number(fields, 48);
            _v6DataAt = Number(fields, 52);

            // The view may be rounded up to a page, never down, so the file's own
            // length is what tells a table cut short.
            if (_length > _view.Capacity || _length > new FileInfo(path).Length)
            {
                throw new InvalidDataException("The network table is shorter than it says.");
            }

            if (_block <= 0 || _systems < 0 || _v4Rows < 0 || _v6Rows < 0)
            {
                throw new InvalidDataException("The network table has no index.");
            }

            // Every section inside the file, so a damaged one is refused here
            // rather than read past its end later, on whichever thread asked.
            if (!Fits(_systemsAt, _systems * (long)SystemSize)
                || !Fits(_namesAt, _nameBytes)
                || !Fits(_v4IndexAt, Blocks(_v4Rows) * 8)
                || !Fits(_v6IndexAt, Blocks(_v6Rows) * 12)
                || !Fits(_v4DataAt, 0)
                || !Fits(_v6DataAt, 0))
            {
                throw new InvalidDataException("The network table's sections do not fit in it.");
            }
        }
        catch
        {
            _view?.Dispose();
            _file.Dispose();
            throw;
        }
    }

    /// <summary>When the data was taken, as yyyymmdd.</summary>
    public uint Date { get; }

    /// <summary>How many autonomous systems it names.</summary>
    public int Systems => _systems;

    /// <summary>How many ranges it holds.</summary>
    public int Ranges => _v4Rows + _v6Rows;

    /// <summary>Where the shipped table lives.</summary>
    public static string ShippedPath => Path.Combine(AppContext.BaseDirectory, "Data", FileName);

    /// <summary>Opens a table, or nothing when it is missing or not a table.</summary>
    public static AsnTable? Open(string? path = null)
    {
        string file = path ?? ShippedPath;

        try
        {
            return File.Exists(file) ? new AsnTable(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The network an address belongs to, or nothing for one that is private, unrouted or not an address.</summary>
    public AutonomousSystem? Find(string? address) =>
        IPAddress.TryParse(address, out IPAddress? parsed) ? Find(parsed) : null;

    /// <summary>The network an address belongs to, or nothing for one that is private or unrouted.</summary>
    public AutonomousSystem? Find(IPAddress? address)
    {
        if (address is null || _disposed)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (AddressScopes.Of(address) != AddressScope.Public)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[16];

        if (!address.TryWriteBytes(bytes, out int written))
        {
            return null;
        }

        try
        {
            int system = address.AddressFamily == AddressFamily.InterNetwork && written == 4
                ? Look(BinaryPrimitives.ReadUInt32BigEndian(bytes), _v4Rows, _v4IndexAt, 8, _v4DataAt, six: false)
                : written == 16
                    ? Look(BinaryPrimitives.ReadUInt64BigEndian(bytes), _v6Rows, _v6IndexAt, 12, _v6DataAt, six: true)
                    : 0;

            return system > 0 ? System(system - 1) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            // Damaged inside a section: no name, as for an address not in the table.
            return null;
        }
    }

    private long Blocks(int rows) => (rows + (long)_block - 1) / _block;

    private bool Fits(long at, long size) => at >= HeaderSize && size >= 0 && at + size <= _length;

    private int Look(ulong key, int rows, long indexAt, int stride, long dataAt, bool six)
    {
        int blocks = (int)Blocks(rows);

        if (blocks == 0)
        {
            return 0;
        }

        int low = 0;
        int high = blocks - 1;

        while (low < high)
        {
            int middle = (low + high + 1) / 2;

            if (First(indexAt, stride, middle, six) <= key)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        ulong at = First(indexAt, stride, low, six);

        if (at > key)
        {
            return 0;
        }

        long from = dataAt + _view.ReadUInt32(indexAt + (low * (long)stride) + (six ? 8 : 4));
        int count = (int)Math.Min(_block, rows - (low * (long)_block));
        int found = 0;

        for (int step = 0; step < count; step++)
        {
            at += ReadVarint(ref from);
            int system = (int)ReadVarint(ref from);

            if (at > key)
            {
                break;
            }

            found = system;
        }

        return found;
    }

    private ulong First(long indexAt, int stride, int block, bool six) => six
        ? _view.ReadUInt64(indexAt + (block * (long)stride))
        : _view.ReadUInt32(indexAt + (block * (long)stride));

    private ulong ReadVarint(ref long at)
    {
        ulong value = 0;
        int shift = 0;

        while (shift < 64)
        {
            if (at >= _length)
            {
                throw new InvalidDataException("A row of the network table runs past its end.");
            }

            byte piece = _view.ReadByte(at++);
            value |= (ulong)(piece & 0x7F) << shift;

            if ((piece & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }

        throw new InvalidDataException("A number in the network table runs on too long.");
    }

    private AutonomousSystem? System(int which)
    {
        if (which < 0 || which >= _systems)
        {
            return null;
        }

        lock (_gate)
        {
            if (_known.TryGetValue(which, out AutonomousSystem? known))
            {
                return known;
            }

            long at = _systemsAt + (which * (long)SystemSize);
            uint number = _view.ReadUInt32(at);
            uint nameAt = _view.ReadUInt32(at + 4);
            ushort length = _view.ReadUInt16(at + 8);

            if (nameAt + (long)length > _nameBytes)
            {
                throw new InvalidDataException("A name in the network table runs past its section.");
            }

            byte[] name = new byte[length];
            _view.ReadArray(_namesAt + nameAt, name, 0, length);
            string country = string.Concat((char)_view.ReadByte(at + 10), (char)_view.ReadByte(at + 11)).Trim();

            AutonomousSystem system = new(number, Encoding.UTF8.GetString(name), country);
            _known[which] = system;
            return system;
        }
    }

    private static uint Number(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _view.Dispose();
        _file.Dispose();
    }
}
