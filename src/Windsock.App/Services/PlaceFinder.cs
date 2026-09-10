using System.Globalization;
using Windsock.App.ViewModels;
using Windsock.Core.Formatting;
using Windsock.Core.Geography;
using Windsock.Core.Networking;

namespace Windsock.App.Services;

/// <summary>
/// Turns the addresses on a graph into places on the earth.
/// </summary>
public sealed class PlaceFinder
{
    private readonly IpLocations? _places;
    private readonly Dictionary<string, WorldPlace> _known = new(StringComparer.Ordinal);
    private readonly HashSet<string> _nowhere = new(StringComparer.Ordinal);

    public PlaceFinder(IpLocations? places) => _places = places;

    /// <summary>Whether anything can be located at all.</summary>
    public bool CanLocate => _places is not null;

    /// <summary>Where an address is, asked once and remembered.</summary>
    public WorldPlace? Find(string? address)
    {
        if (_places is null || address is not { Length: > 0 })
        {
            return null;
        }

        if (_known.TryGetValue(address, out WorldPlace found))
        {
            return found;
        }

        if (_nowhere.Contains(address))
        {
            return null;
        }

        if (_places.Find(address) is { } place)
        {
            _known[address] = place;
            return place;
        }
        _nowhere.Add(address);
        return null;
    }

    /// <summary>
    /// Where a far end is, out of everything known about it.
    /// </summary>
    public WorldPlace? Find(PeerNodeViewModel peer)
    {
        ArgumentNullException.ThrowIfNull(peer);

        foreach (string address in peer.Probes)
        {
            if (Find(address) is { } place)
            {
                return place;
            }
        }

        return null;
    }

    /// <summary>
    /// Works out where this machine is, and what each country holds.
    /// </summary>
    public Survey Look(IEnumerable<PeerNodeViewModel> peers, IEnumerable<RouteNode> roots)
    {
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(roots);

        Dictionary<string, List<(string Name, double Rate)>> holds =
            new(StringComparer.OrdinalIgnoreCase);

        WorldPlace? anywhere = null;
        bool any = false;

        foreach (PeerNodeViewModel peer in peers)
        {
            if (Find(peer) is not { } place)
            {
                continue;
            }

            any = true;
            anywhere ??= place;

            if (!holds.TryGetValue(place.Country, out List<(string, double)>? there))
            {
                there = [];
                holds[place.Country] = there;
            }

            there.Add((peer.Label, peer.SendRate + peer.ReceiveRate));
        }

        WorldPlace? home = null;
        int nearest = int.MaxValue;

        foreach (RouteNode root in roots)
        {
            Closest(root, ref home, ref nearest);
        }

        Dictionary<string, CountryNote> notes = new(StringComparer.OrdinalIgnoreCase);
        double loudest = 0;
        int most = 0;

        foreach (List<(string Name, double Rate)> each in holds.Values)
        {
            loudest = Math.Max(loudest, each.Sum(one => one.Rate));
            most = Math.Max(most, each.Count);
        }

        foreach ((string country, List<(string Name, double Rate)> there) in holds)
        {
            there.Sort(static (left, right) => right.Rate.CompareTo(left.Rate));

            double moving = there.Sum(each => each.Rate);

            string what = there.Count == 1
                ? "1 place"
                : string.Format(CultureInfo.CurrentCulture, "{0:N0} places", there.Count);

            notes[country] = new CountryNote(
                moving > 0
                    ? string.Concat(
                        what,
                        ", ",
                        RateFormatter.Format(moving, RateFamily.Bytes, RateScale.Auto),
                        " between them")
                    : what,
                Name(there),
                loudest > 0 ? moving / loudest : (double)there.Count / most);
        }

        return new Survey(home ?? anywhere, any || home is not null, notes);
    }

    private static string Name(List<(string Name, double Rate)> there)
    {
        const int Listed = 6;

        List<string> names = [];

        foreach ((string name, _) in there)
        {
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names.Count <= Listed
            ? string.Join(", ", names)
            : string.Format(
                CultureInfo.CurrentCulture,
                "{0} and {1:N0} more",
                string.Join(", ", names.Take(Listed)),
                names.Count - Listed);
    }

    /// <summary>
    /// Where something is, in words, and how far away that makes it.
    /// </summary>
    public static string Describe(WorldPlace? place, WorldPlace? home, TimeSpan? roundTrip)
    {
        if (place is not { IsKnown: true } there)
        {
            return string.Empty;
        }

        string named = there.City.Length > 0
            ? string.Concat(there.City, ", ", there.Country)
            : there.Country;

        if (home is not { IsKnown: true } here)
        {
            return named;
        }

        double away = there.From(here);

        string said = string.Format(
            CultureInfo.CurrentCulture,
            "{0} \u00b7 {1:N0} km away",
            named,
            away);

        if (roundTrip is not { } round || away <= WorldPlace.Reachable(round))
        {
            return said;
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            "{0} - though it answers in {1:N0} ms, too fast to be that far",
            said,
            round.TotalMilliseconds);
    }

    private void Closest(RouteNode node, ref WorldPlace? home, ref int nearest)
    {
        if (node.Address is { Length: > 0 } address
            && node.Distance < nearest
            && Find(address) is { } place)
        {
            home = place;
            nearest = node.Distance;
        }

        foreach (RouteNode child in node.Children)
        {
            Closest(child, ref home, ref nearest);
        }
    }
}

/// <summary>What a look at the addresses on a picture turned up.</summary>
public readonly record struct Survey(
    WorldPlace? Home,
    bool Anything,
    IReadOnlyDictionary<string, CountryNote> Notes);

/// <summary>What one country has on it.</summary>
public sealed record CountryNote(string Summary, string Places, double Weight);
