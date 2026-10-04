using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Windsock.Core.Geography;

/// <summary>
/// Where this machine is, from what it already knows about itself. Nothing is
/// sent to find out.
/// </summary>
/// <remarks>
/// <para>
/// The map used to put the machine at the nearest traced hop, and failing that
/// at the first far end it could place. With routes off - the default - that
/// meant wherever the first peer of a torrent client was: on 2026-09-15 a
/// machine in Dhaka was drawn in Russia, and moved between redraws.
/// </para>
/// <para>
/// The clock's time zone is the most honest sign of where somebody physically
/// is: a VPN moves every address, but not the zone. Windows' own region setting
/// is not: it was "US" on that machine, as it is on a great many that were never
/// asked. So the zone's city comes first, and an address of the machine's own
/// (a public IPv6 address, usually) or the first hop is taken instead only where
/// it lands in the same country, which makes it a finer answer to the same
/// question rather than a different one.
/// </para>
/// </remarks>
public static class HomeGuess
{
    /// <summary>How far, in hours, a city's sun time may sit from its zone's clock.</summary>
    private const double SunDrift = 3.5;

    /// <summary>
    /// Old names ICU still gives for zones whose cities the place table calls
    /// by their current name.
    /// </summary>
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Calcutta"] = "Kolkata",
        ["Saigon"] = "Ho Chi Minh City",
        ["Katmandu"] = "Kathmandu",
        ["Rangoon"] = "Yangon",
        ["Kiev"] = "Kyiv",
        ["Godthab"] = "Nuuk",
        ["Ulan Bator"] = "Ulaanbaatar",
        ["Dacca"] = "Dhaka",
        ["Asmera"] = "Asmara",
        ["Thimbu"] = "Thimphu",
    };

    /// <summary>
    /// The best guess at this machine's place, or nothing when nothing points
    /// anywhere with confidence.
    /// </summary>
    public static WorldPlace? Estimate(IpLocations? places, TimeZoneInfo zone, string? region, IEnumerable<IPAddress> own)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(own);

        if (places is null)
        {
            return null;
        }

        WorldPlace? clock = ZonePlace(places, zone, region);

        foreach (IPAddress address in own)
        {
            if (IsPublic(address)
                && places.Find(address) is { IsKnown: true } there
                && (clock is null || string.Equals(there.Country, clock.Value.Country, StringComparison.OrdinalIgnoreCase)))
            {
                return there;
            }
        }

        return clock;
    }

    /// <summary>
    /// A finer place for home, when it agrees with the country of the guess.
    /// </summary>
    public static WorldPlace? Refine(WorldPlace? guess, WorldPlace? finer) =>
        finer is { IsKnown: true } near
            && (guess is null || string.Equals(near.Country, guess.Value.Country, StringComparison.OrdinalIgnoreCase))
            ? near
            : guess;

    /// <summary>The city of the zone's clock, where the place table knows it.</summary>
    public static WorldPlace? ZonePlace(IpLocations places, TimeZoneInfo zone, string? region)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(zone);

        if (ZoneCity(zone, region) is not { } city)
        {
            return null;
        }

        double offset = zone.BaseUtcOffset.TotalHours;
        WorldPlace? best = null;
        double closest = double.MaxValue;

        foreach (WorldPlace place in places.Named(city))
        {
            // Sun time against the clock: London, Ontario is five hours out.
            double drift = Math.Abs((place.Longitude / 15) - offset);
            drift = Math.Min(drift, 24 - drift);

            if (drift > SunDrift)
            {
                continue;
            }

            // The region breaks a tie, never decides alone; a city without a
            // district is the city itself rather than a suburb of it.
            double rank = drift
                - (string.Equals(place.Country, region, StringComparison.OrdinalIgnoreCase) ? 10 : 0)
                - (string.Equals(place.City, city, StringComparison.OrdinalIgnoreCase) ? 1 : 0);

            if (rank < closest)
            {
                closest = rank;
                best = place;
            }
        }

        return best;
    }

    /// <summary>
    /// The city a zone is named for: "Bangladesh Standard Time" is Asia/Dhaka,
    /// so Dhaka. Nothing for a zone that names no city (Etc/GMT+3, UTC).
    /// </summary>
    public static string? ZoneCity(TimeZoneInfo zone, string? region)
    {
        ArgumentNullException.ThrowIfNull(zone);

        string? iana = zone.HasIanaId ? zone.Id : null;

        if (iana is null
            && !(region is { Length: 2 } && TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, region, out iana))
            && !TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out iana))
        {
            return null;
        }

        if (iana is null || !iana.Contains('/', StringComparison.Ordinal) || iana.StartsWith("Etc/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string city = iana[(iana.LastIndexOf('/') + 1)..].Replace('_', ' ');

        return Renamed.TryGetValue(city, out string? now) ? now : city;
    }

    /// <summary>The addresses this machine's working adapters carry.</summary>
    public static IReadOnlyList<IPAddress> OwnAddresses()
    {
        List<IPAddress> found = [];

        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    found.Add(unicast.Address);
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return found;
    }

    /// <summary>
    /// Whether an address is one the internet routes to: not private, not
    /// link-local, not a unique local IPv6 address, not Teredo.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[16];

        if (!address.TryWriteBytes(bytes, out int written))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return IsPublic(address.MapToIPv4());
            }

            // Global unicast is 2000::/3; Teredo's 2001::/32 says nothing of where.
            return (bytes[0] & 0xE0) == 0x20
                && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0);
        }

        return written == 4
            && bytes[0] != 10
            && bytes[0] != 127
            && !(bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
            && !(bytes[0] == 192 && bytes[1] == 168)
            && !(bytes[0] == 100 && (bytes[1] & 0xC0) == 64)
            && !(bytes[0] == 169 && bytes[1] == 254)
            && bytes[0] != 0
            && bytes[0] < 224;
    }
}
