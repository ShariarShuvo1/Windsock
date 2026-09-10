namespace Windsock.Core.Geography;

/// <summary>
/// Somewhere on the earth, as far as an address can say.
/// </summary>
public readonly record struct WorldPlace(double Latitude, double Longitude, string City, string Country)
{
    /// <summary>Whether this names anywhere at all.</summary>
    public bool IsKnown => Country.Length > 0;

    /// <summary>
    /// How far away somewhere else is, in kilometres.
    /// </summary>
    public double From(WorldPlace other)
    {
        const double Radius = 6371;

        double lat = (other.Latitude - Latitude) * Math.PI / 180;
        double lon = (other.Longitude - Longitude) * Math.PI / 180;
        double here = Latitude * Math.PI / 180;
        double there = other.Latitude * Math.PI / 180;

        double a = (Math.Sin(lat / 2) * Math.Sin(lat / 2))
            + (Math.Cos(here) * Math.Cos(there) * Math.Sin(lon / 2) * Math.Sin(lon / 2));

        return 2 * Radius * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>
    /// The furthest away something answering in this long could possibly be.
    /// </summary>
    public static double Reachable(TimeSpan roundTrip) => roundTrip.TotalMilliseconds * 100;
}
