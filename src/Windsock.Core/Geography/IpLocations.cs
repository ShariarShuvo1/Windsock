using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Windsock.Core.Geography;

/// <summary>
/// Where an address is, from a table shipped with Windsock.
/// </summary>
public sealed class IpLocations : IDisposable
{
    private const string Magic = "WSCKGEO1";
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly int _places;
    private readonly int _countries;
    private readonly int _v6Shift;
    private readonly int _block;
    private readonly int _v4Rows;
    private readonly int _v4Blocks;
    private readonly int _v6Rows;
    private readonly int _v6Blocks;
    private readonly long _countriesAt;
    private readonly long _namesAt;
    private readonly long _placesAt;
    private readonly long _v4IndexAt;
    private readonly long _v4DataAt;
    private readonly long _v6IndexAt;
    private readonly long _v6DataAt;
    private readonly Dictionary<int, WorldPlace> _known = [];
    private readonly Lock _gate = new();

    private bool _disposed;

    private IpLocations(string path)
    {
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, 0, MemoryMappedFileAccess.Read);
        _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        Span<byte> header = stackalloc byte[8 + (4 * 17)];

        for (int at = 0; at < header.Length; at++)
        {
            header[at] = _view.ReadByte(at);
        }

        if (!header[..8].SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
        {
            throw new InvalidDataException("This is not a Windsock place table.");
        }

        int field = 8;

        _ = Read32(header, ref field);
        _countries = (int)Read32(header, ref field);
        _ = Read32(header, ref field);
        _places = (int)Read32(header, ref field);
        _v4Rows = (int)Read32(header, ref field);
        _v4Blocks = (int)Read32(header, ref field);
        _v6Rows = (int)Read32(header, ref field);
        _v6Blocks = (int)Read32(header, ref field);
        _v6Shift = (int)Read32(header, ref field);
        _block = (int)Read32(header, ref field);

        _countriesAt = Read32(header, ref field);
        _namesAt = Read32(header, ref field);
        _placesAt = Read32(header, ref field);
        _v4IndexAt = Read32(header, ref field);
        _v4DataAt = Read32(header, ref field);
        _v6IndexAt = Read32(header, ref field);
        _v6DataAt = Read32(header, ref field);
    }

    /// <summary>How many ranges the table holds.</summary>
    public int Ranges => _v4Rows + _v6Rows;

    /// <summary>How many distinct places it names.</summary>
    public int Places => _places;

    /// <summary>How many countries appear in it.</summary>
    public int Countries => _countries;

    /// <summary>
    /// Opens the table shipped beside Windsock, or nothing if it is not there.
    /// </summary>
    public static IpLocations? Open(string? path = null)
    {
        string file = path ?? Beside("places.geo");

        try
        {
            return File.Exists(file) ? new IpLocations(file) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Where a file shipped with Windsock lives.</summary>
    public static string Beside(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Data", name);

    /// <summary>Where an address is, or nothing when it is not a real one.</summary>
    public WorldPlace? Find(string? address) =>
        IPAddress.TryParse(address, out IPAddress? parsed) ? Find(parsed) : null;

    /// <summary>Where an address is.</summary>
    public WorldPlace? Find(IPAddress? address)
    {
        if (address is null || _disposed)
        {
            return null;
        }
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[16];

        if (!address.TryWriteBytes(bytes, out int written))
        {
            return null;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && written == 16)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return Find(address.MapToIPv4());
            }

            ulong key = BinaryPrimitives.ReadUInt64BigEndian(bytes) >> _v6Shift;
            return Look(key, _v6Blocks, _v6IndexAt, 12, _v6DataAt, _v6Rows, six: true);
        }

        if (written != 4)
        {
            return null;
        }

        uint plain = BinaryPrimitives.ReadUInt32BigEndian(bytes);

        if (Private(plain))
        {
            return null;
        }

        return Look(plain, _v4Blocks, _v4IndexAt, 8, _v4DataAt, _v4Rows, six: false);
    }

    private static bool Private(uint address) =>
        (address >> 24) == 10
        || (address >> 24) == 127
        || (address >> 20) == 0xAC1                  // 172.16/12
        || (address >> 16) == 0xC0A8                 // 192.168/16
        || (address >> 22) == 0x1900                 // 100.64/10, carrier grade
        || (address >> 16) == 0xA9FE                 // 169.254/16, link local
        || address == 0;

    private WorldPlace? Look(ulong address, int blocks, long indexAt, int stride, long dataAt, int rows, bool six)
    {
        if (blocks == 0)
        {
            return null;
        }

        int low = 0;
        int high = blocks - 1;

        while (low < high)
        {
            int middle = (low + high + 1) / 2;

            if (First(indexAt, stride, middle, six) <= address)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (First(indexAt, stride, low, six) > address)
        {
            return null;
        }

        long from = dataAt + _view.ReadUInt32(indexAt + (low * stride) + (six ? 8 : 4));
        int count = Math.Min(_block, rows - (low * _block));

        ulong at = First(indexAt, stride, low, six);
        int place = -1;

        for (int step = 0; step < count; step++)
        {
            at += ReadVarint(ref from);
            int seat = (int)ReadVarint(ref from);

            if (at > address)
            {
                break;
            }

            place = seat;
        }

        return place < 0 ? null : Place(place);
    }

    private ulong First(long indexAt, int stride, int block, bool six) => six
        ? _view.ReadUInt64(indexAt + (block * stride))
        : _view.ReadUInt32(indexAt + (block * stride));

    private ulong ReadVarint(ref long at)
    {
        ulong value = 0;
        int shift = 0;

        while (true)
        {
            byte piece = _view.ReadByte(at++);
            value |= (ulong)(piece & 0x7F) << shift;

            if ((piece & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }
    }

    private WorldPlace Place(int which)
    {
        lock (_gate)
        {
            if (_known.TryGetValue(which, out WorldPlace known))
            {
                return known;
            }

            long at = _placesAt + (which * 12L);

            uint start = _view.ReadUInt32(at);
            ushort length = _view.ReadUInt16(at + 4);
            ushort country = _view.ReadUInt16(at + 6);
            short latitude = _view.ReadInt16(at + 8);
            short longitude = _view.ReadInt16(at + 10);

            string name = Text(_namesAt + start, length);
            string code = country < _countries ? Text(_countriesAt + (country * 2L), 2).Trim() : string.Empty;

            WorldPlace place = new(
                latitude / 32767d * 90,
                longitude / 32767d * 180,
                name,
                code);

            _known[which] = place;
            return place;
        }
    }

    private string Text(long at, int length)
    {
        if (length == 0)
        {
            return string.Empty;
        }

        byte[] bytes = new byte[length];
        _view.ReadArray(at, bytes, 0, length);

        return Encoding.UTF8.GetString(bytes);
    }

    private static uint Read32(ReadOnlySpan<byte> bytes, ref int at)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
        at += 4;
        return value;
    }

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
