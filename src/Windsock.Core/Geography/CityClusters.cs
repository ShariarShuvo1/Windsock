namespace Windsock.Core.Geography;

/// <summary>
/// Gathers places into the cities a reader would call them, for drawing one
/// dot per city.
/// </summary>
/// <remarks>
/// <para>
/// The place table names far finer than a city. Three addresses measured on
/// 2026-09-13 came back as "Dhaka", "Dhaka (Gulshan)" and "Dhaka (Cantonment)",
/// all within a kilometre or two of each other; Microsoft's addresses around New
/// York answer as "New York", "Newark" and "Teterboro". Grouping on the name as
/// written kept every one of those apart, and the map then had to push three
/// dots aside from the same spot, which is the opposite of one dot per city.
/// </para>
/// <para>
/// So a place joins the nearest city already found in the same country if it
/// lies within <see cref="Reach"/> of where that city started, and starts one
/// of its own otherwise. Measuring from the first place rather than from every
/// member keeps a city from creeping outward one suburb at a time. The country
/// has to match, so a city is never drawn over a border it does not cross.
/// </para>
/// <para>
/// Feed places in a stable order - first seen first - and the same connections
/// make the same cities on every redraw, which keeps a dot from jumping about.
/// </para>
/// </remarks>
public sealed class CityClusters
{
    /// <summary>How far from where a city started another place may be and still be in it, in kilometres.</summary>
    public const double Reach = 40;

    private readonly List<City> _cities = [];

    /// <summary>The cities found so far, in the order they were started.</summary>
    public IReadOnlyList<City> Cities => _cities;

    /// <summary>Puts a place into its city, starting the city if need be.</summary>
    public City Join(WorldPlace place)
    {
        City? nearest = null;
        double best = double.MaxValue;

        foreach (City city in _cities)
        {
            if (!string.Equals(city.Anchor.Country, place.Country, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            double away = city.Anchor.From(place);

            if (away <= Reach && away < best)
            {
                nearest = city;
                best = away;
            }
        }

        if (nearest is null)
        {
            nearest = new City(place);
            _cities.Add(nearest);
        }

        nearest.Take(place);
        return nearest;
    }

    /// <summary>
    /// A place's name without the district the table appends in brackets:
    /// "Dhaka (Gulshan)" is Dhaka.
    /// </summary>
    public static string BaseName(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();

        if (trimmed.EndsWith(')') && trimmed.LastIndexOf(" (", StringComparison.Ordinal) is > 0 and int open)
        {
            return trimmed[..open].TrimEnd();
        }

        return trimmed;
    }
}

/// <summary>One city, and what it is called.</summary>
public sealed class City
{
    private readonly Dictionary<string, int> _named = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorldPlace> _firstAt = new(StringComparer.OrdinalIgnoreCase);
    private int _leading;

    internal City(WorldPlace anchor)
    {
        Anchor = anchor;
        Place = anchor;

        Key = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{anchor.Country}|{CityClusters.BaseName(anchor.City)}|{anchor.Latitude:F2}|{anchor.Longitude:F2}");

        Name = Describe(CityClusters.BaseName(anchor.City), anchor.Country);
    }

    /// <summary>
    /// What identifies the city across redraws: where it started, which does
    /// not change as places join it.
    /// </summary>
    public string Key { get; }

    /// <summary>The first place in it, which is what distance is measured from.</summary>
    public WorldPlace Anchor { get; }

    /// <summary>
    /// Where to draw it: the first place carrying the name it goes by, so the
    /// dot and its label agree.
    /// </summary>
    public WorldPlace Place { get; private set; }

    /// <summary>
    /// What to call it: the name most of its places give, with its country. A
    /// city started by an address in Teterboro that goes on to hold ten in New
    /// York is New York.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>How many places have joined it.</summary>
    public int Count { get; private set; }

    internal void Take(WorldPlace place)
    {
        Count++;

        string name = CityClusters.BaseName(place.City);

        if (name.Length == 0)
        {
            return;
        }

        _firstAt.TryAdd(name, place);
        _named.TryGetValue(name, out int seen);
        _named[name] = ++seen;

        // Strictly more, so a tie stays with the name that got there first.
        if (seen > _leading)
        {
            _leading = seen;
            Place = _firstAt[name];
            Name = Describe(name, Place.Country);
        }
    }

    private static string Describe(string city, string country) =>
        city.Length > 0 && country.Length > 0
            ? string.Concat(city, ", ", country)
            : city.Length > 0 ? city : country;
}
