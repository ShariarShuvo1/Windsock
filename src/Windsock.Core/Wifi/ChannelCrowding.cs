namespace Windsock.Core.Wifi;

/// <summary>How busy one channel is with other people's networks.</summary>
/// <param name="Band">The band.</param>
/// <param name="Channel">The channel.</param>
/// <param name="Networks">How many networks overlap it at all.</param>
/// <param name="Load">Those networks weighted by how loudly they were heard and how much they overlap; lower is quieter.</param>
/// <param name="Dfs">Whether it is a 5 GHz channel a router may only use after listening for radar.</param>
public sealed record ChannelLoad(WifiBand Band, int Channel, int Networks, double Load, bool Dfs);

/// <summary>A quieter channel worth moving the router to, in one band.</summary>
/// <param name="Band">The band.</param>
/// <param name="Channel">The suggested channel.</param>
/// <param name="Load">How busy it is.</param>
/// <param name="CurrentChannel">The channel the person's network uses in this band, when known.</param>
/// <param name="CurrentLoad">How busy that one is.</param>
public sealed record ChannelSuggestion(WifiBand Band, int Channel, double Load, int? CurrentChannel, double? CurrentLoad)
{
    /// <summary>Whether moving would help: the suggestion is clearly quieter than where the network is now.</summary>
    public bool IsBetter => CurrentLoad is not { } current || (current - Load >= 0.5 && Load <= current * 0.7);
}

/// <summary>Works out how crowded each channel is from a scan, and which is quietest.</summary>
/// <remarks>
/// <para>
/// Candidates are the channels routers are told to choose from: 1, 6 and 11 on
/// 2.4 GHz (the only three that do not overlap); every 5 GHz channel, with the
/// ones needing radar detection (52 to 144) marked, since some routers will not
/// use them and any may be pushed off them; and on 6 GHz the preferred scanning
/// channels (5, 21, 37 ... 229), which devices look on first.
/// </para>
/// <para>
/// A network counts against a channel by how much of the channel's width it
/// overlaps, times how loudly it was heard: a neighbour at -50 dBm counts fully,
/// one at -90 dBm hardly at all, linear between. That is a judgement, not a
/// standard; it ranks channels sensibly without pretending to measure airtime.
/// The person's own network is left out: it is the thing being moved.
/// </para>
/// </remarks>
public static class ChannelCrowding
{
    private static readonly int[] TwoPointFour = [1, 6, 11];

    private static readonly int[] Five =
    [
        36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165,
    ];

    private static readonly int[] SixPreferred = [5, 21, 37, 53, 69, 85, 101, 117, 133, 149, 165, 181, 197, 213, 229];

    /// <summary>The channels a router in a band would choose among.</summary>
    public static IReadOnlyList<int> Candidates(WifiBand band) => band switch
    {
        WifiBand.TwoPointFour => TwoPointFour,
        WifiBand.Five => Five,
        WifiBand.Six => SixPreferred,
        _ => [],
    };

    /// <summary>Whether a 5 GHz channel needs radar detection (DFS).</summary>
    public static bool IsDfs(WifiBand band, int channel) => band == WifiBand.Five && channel is >= 52 and <= 144;

    /// <summary>How heavily a network heard at a strength counts, 0.05 to 1.</summary>
    public static double Weight(int rssi) => Math.Clamp((rssi + 90) / 40d, 0.05, 1);

    /// <summary>How busy each candidate channel in a band is.</summary>
    /// <param name="networks">What the scan heard.</param>
    /// <param name="band">The band.</param>
    /// <param name="widthMhz">The width the person's network would use; 20 when not known.</param>
    /// <param name="own">Whether a network is the person's own, to be left out.</param>
    public static IReadOnlyList<ChannelLoad> Loads(
        IEnumerable<NearbyNetwork> networks,
        WifiBand band,
        int widthMhz = 20,
        Func<NearbyNetwork, bool>? own = null)
    {
        ArgumentNullException.ThrowIfNull(networks);

        NearbyNetwork[] others = [.. networks.Where(each => each.Band == band && own?.Invoke(each) != true)];
        int width = band == WifiBand.TwoPointFour ? 20 : Math.Max(20, widthMhz);
        List<ChannelLoad> loads = [];

        foreach (int channel in Candidates(band))
        {
            double centre = WifiBands.FrequencyOf(band, WifiBands.BlockCentre(band, channel, width));
            (double low, double high) = (centre - (width / 2d), centre + (width / 2d));
            int count = 0;
            double load = 0;

            foreach (NearbyNetwork network in others)
            {
                (double theirLow, double theirHigh) = network.Span;
                double overlap = Math.Min(high, theirHigh) - Math.Max(low, theirLow);

                if (overlap <= 0)
                {
                    continue;
                }

                count++;
                load += Weight(network.Rssi) * Math.Min(1, overlap / width);
            }

            loads.Add(new ChannelLoad(band, channel, count, Math.Round(load, 3), IsDfs(band, channel)));
        }

        return loads;
    }

    /// <summary>
    /// The quietest channel in a band, preferring one without radar detection
    /// when it is as quiet as the quietest that needs it, or nothing when the
    /// band has no candidates.
    /// </summary>
    /// <param name="loads">From <see cref="Loads"/>.</param>
    /// <param name="currentChannel">Where the person's network is in this band now, when known.</param>
    public static ChannelSuggestion? Suggest(IReadOnlyList<ChannelLoad> loads, int? currentChannel = null)
    {
        ArgumentNullException.ThrowIfNull(loads);

        if (loads.Count == 0)
        {
            return null;
        }

        double quietest = loads.Min(each => each.Load);
        ChannelLoad best = loads
            .Where(each => each.Load <= quietest + 0.25)
            .OrderBy(each => each.Dfs)
            .ThenBy(each => each.Load)
            .ThenBy(each => each.Channel)
            .First();

        ChannelLoad? current = currentChannel is { } now ? Nearest(loads, now) : null;

        return new ChannelSuggestion(best.Band, best.Channel, best.Load, currentChannel, current?.Load);
    }

    private static ChannelLoad? Nearest(IReadOnlyList<ChannelLoad> loads, int channel) =>
        loads.FirstOrDefault(each => each.Channel == channel)
        ?? loads.MinBy(each => Math.Abs(each.Channel - channel));
}
