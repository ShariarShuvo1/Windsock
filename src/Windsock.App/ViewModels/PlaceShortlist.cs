using System.Globalization;

namespace Windsock.App.ViewModels;

/// <summary>
/// Which of one app's places a graph draws one by one, and the node that
/// stands for the rest.
/// </summary>
/// <remarks>
/// A torrent client talks to a thousand peers at once. Drawn one by one, with
/// the trace running, the map's layout searched room for every one of them on
/// every reading and held the UI thread for up to 13 seconds at a time, long
/// enough for Windows to call the window not responding. Measured on
/// 2026-09-15: 26 apps, 900 to 1,600 places, 2,000 nodes on a canvas 20,000
/// pixels across. Nobody reads a thousand places anyway.
/// </remarks>
internal static class PlaceShortlist
{
    /// <summary>The key the rest are filed under, which no address or name can be.</summary>
    public const string RestKey = "more:";

    /// <summary>A place already drawn keeps its spot until another is this much busier.</summary>
    private const double Hold = 1.5;

    /// <summary>
    /// The busiest places, at most <paramref name="most"/>, plus
    /// <paramref name="rest"/> filled in for everything else. All of them,
    /// when there are few enough that folding would hide only one.
    /// </summary>
    /// <param name="places">Every place the app is talking to.</param>
    /// <param name="most">How many are drawn on their own.</param>
    /// <param name="drawn">What was drawn last time, so places do not swap on every reading.</param>
    /// <param name="followed">A place being followed, which is always drawn.</param>
    /// <param name="rest">The node reused for the rest, from one reading to the next.</param>
    /// <param name="rates">Writes a pair of rates, receive then send, the way the graph shows them.</param>
    public static List<PeerNodeViewModel> Choose(
        ICollection<PeerNodeViewModel> places,
        int most,
        IReadOnlyCollection<PeerNodeViewModel> drawn,
        object? followed,
        PeerNodeViewModel rest,
        Func<double, double, string> rates)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(drawn);
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(rates);

        if (places.Count <= most + 1)
        {
            return [.. places];
        }

        HashSet<PeerNodeViewModel> before = [.. drawn];
        List<PeerNodeViewModel> ranked = [.. places];

        double Standing(PeerNodeViewModel place) =>
            ReferenceEquals(place, followed)
                ? double.MaxValue
                : before.Contains(place) ? (place.Weight * Hold) + 0.5 : place.Weight;

        ranked.Sort((left, right) =>
        {
            int byStanding = Standing(right).CompareTo(Standing(left));
            return byStanding != 0 ? byStanding : left.Seen.CompareTo(right.Seen);
        });

        List<PeerNodeViewModel> shown = ranked.GetRange(0, most);
        double send = 0;
        double receive = 0;
        bool measured = false;

        for (int index = most; index < ranked.Count; index++)
        {
            PeerNodeViewModel place = ranked[index];

            send += place.SendRate;
            receive += place.ReceiveRate;
            measured |= place.HasRates;
        }

        int hidden = ranked.Count - most;

        rest.Label = string.Format(CultureInfo.CurrentCulture, "{0:N0} more places", hidden);
        rest.SendRate = send;
        rest.ReceiveRate = receive;
        rest.HasRates = measured;
        rest.Weight = send + receive;
        rest.RateText = measured ? rates(receive, send) : "Quieter places, together";
        rest.Sites = string.Format(
            CultureInfo.CurrentCulture,
            "The busiest {0:N0} are drawn on their own; these {1:N0} are drawn as one, so the picture stays readable",
            most,
            hidden);

        shown.Add(rest);
        return shown;
    }
}
