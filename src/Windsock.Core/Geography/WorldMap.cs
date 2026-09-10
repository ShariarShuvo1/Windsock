using System.Buffers.Binary;

namespace Windsock.Core.Geography;

/// <summary>One country, as an outline and a name.</summary>
public sealed record WorldCountry(
    string Code,
    string Name,
    string Label,
    IReadOnlyList<double[]> Rings,
    double West,
    double South,
    double East,
    double North,
    double LabelLongitude,
    double LabelLatitude)
{
    /// <summary>Whether a point could possibly be inside, cheaply.</summary>
    public bool Around(double longitude, double latitude) =>
        longitude >= West && longitude <= East && latitude >= South && latitude <= North;

    /// <summary>
    /// Whether a point is inside the country.
    /// </summary>
    public bool Holds(double longitude, double latitude)
    {
        if (!Around(longitude, latitude))
        {
            return false;
        }

        foreach (double[] ring in Rings)
        {
            bool inside = false;

            for (int at = 0, last = ring.Length - 2; at < ring.Length; last = at, at += 2)
            {
                double x1 = ring[at];
                double y1 = ring[at + 1];
                double x2 = ring[last];
                double y2 = ring[last + 1];

                if (y1 > latitude != y2 > latitude
                    && longitude < ((x2 - x1) * (latitude - y1) / (y2 - y1)) + x1)
                {
                    inside = !inside;
                }
            }

            if (inside)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The outline of the world, as countries.
/// </summary>
public sealed class WorldMap
{
    private const string Magic = "WSCKMAP2";

    private WorldMap(IReadOnlyList<WorldCountry> countries)
    {
        Countries = countries;

        Dictionary<string, WorldCountry> byCode = new(StringComparer.OrdinalIgnoreCase);

        foreach (WorldCountry country in countries)
        {
            if (country.Code.Length > 0)
            {
                byCode.TryAdd(country.Code, country);
            }
        }

        _byCode = byCode;
    }

    private readonly Dictionary<string, WorldCountry> _byCode;

    /// <summary>Every country on the map.</summary>
    public IReadOnlyList<WorldCountry> Countries { get; }

    /// <summary>
    /// Reads the outline shipped beside Windsock, or nothing if it is not there.
    /// </summary>
    public static WorldMap? Open(string? path = null)
    {
        string file = path ?? IpLocations.Beside("world.map");

        try
        {
            return File.Exists(file) ? Read(file) : null;
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

    /// <summary>Reads the packed outline.</summary>
    public static WorldMap Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return From(File.ReadAllBytes(path));
    }

    /// <summary>Reads the packed outline from bytes already in hand.</summary>
    public static WorldMap From(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16 || !bytes[..8].SequenceEqual(System.Text.Encoding.ASCII.GetBytes(Magic)))
        {
            throw new InvalidDataException("This is not a Windsock world map.");
        }

        int at = 8;
        _ = Take32(bytes, ref at);
        int count = (int)Take32(bytes, ref at);

        List<WorldCountry> countries = new(count);

        for (int which = 0; which < count; which++)
        {
            string code = TakeText(bytes, ref at);
            string name = TakeText(bytes, ref at);
            string label = TakeText(bytes, ref at);

            double west = TakeSingle(bytes, ref at);
            double south = TakeSingle(bytes, ref at);
            double east = TakeSingle(bytes, ref at);
            double north = TakeSingle(bytes, ref at);
            double labelX = TakeSingle(bytes, ref at);
            double labelY = TakeSingle(bytes, ref at);

            int rings = (int)Take32(bytes, ref at);
            List<double[]> outlines = new(rings);

            for (int ring = 0; ring < rings; ring++)
            {
                int points = (int)Take32(bytes, ref at);
                double[] outline = new double[points * 2];

                for (int point = 0; point < points; point++)
                {
                    outline[point * 2] = TakeSingle(bytes, ref at);
                    outline[(point * 2) + 1] = TakeSingle(bytes, ref at);
                }

                outlines.Add(outline);
            }

            countries.Add(
                new WorldCountry(code, name, label, outlines, west, south, east, north, labelX, labelY));
        }

        return new WorldMap(countries);
    }

    /// <summary>The country a code belongs to, if it is on the map.</summary>
    public WorldCountry? Find(string? code) =>
        code is { Length: > 0 } && _byCode.TryGetValue(code, out WorldCountry? country) ? country : null;

    /// <summary>The country a point is in, if any.</summary>
    public WorldCountry? At(double longitude, double latitude)
    {
        foreach (WorldCountry country in Countries)
        {
            if (country.Holds(longitude, latitude))
            {
                return country;
            }
        }

        return null;
    }

    private static uint Take32(ReadOnlySpan<byte> bytes, ref int at)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
        at += 4;
        return value;
    }

    private static double TakeSingle(ReadOnlySpan<byte> bytes, ref int at)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]);
        at += 4;
        return value;
    }

    private static string TakeText(ReadOnlySpan<byte> bytes, ref int at)
    {
        int length = bytes[at++];
        string text = System.Text.Encoding.UTF8.GetString(bytes.Slice(at, length));
        at += length;
        return text;
    }
}
