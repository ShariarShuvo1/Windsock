namespace Windsock.Core.Networking;

/// <summary>One minute of probes to one hop, for drawing its loss over time.</summary>
/// <param name="Minute">The minute's start.</param>
/// <param name="Sent">Probes sent.</param>
/// <param name="Received">Answers received.</param>
/// <param name="AverageMs">The answers' mean round trip.</param>
public sealed record HopMinute(DateTimeOffset Minute, int Sent, int Received, double? AverageMs)
{
    /// <summary>The share lost.</summary>
    public double? Loss => Sent > 0 ? (double)(Sent - Received) / Sent : null;
}

/// <summary>One row of the hop-by-hop table: WinMTR's columns, and the minutes behind them.</summary>
/// <param name="Distance">The hop number.</param>
/// <param name="Address">The address that answered most often at this distance, or nothing for a silent hop.</param>
/// <param name="OtherAddresses">Any other addresses that answered here: load-balanced paths.</param>
/// <param name="Sent">Probes sent.</param>
/// <param name="Received">Answers received.</param>
/// <param name="LastMs">The latest round trip.</param>
/// <param name="BestMs">The quickest.</param>
/// <param name="AverageMs">The mean.</param>
/// <param name="WorstMs">The slowest.</param>
/// <param name="DeviationMs">The standard deviation of the round trips.</param>
/// <param name="IsDestination">Whether this is the far end itself.</param>
/// <param name="LossCarriesOn">
/// Whether this hop's loss shows at every hop after it too. Routers answer
/// probes addressed to themselves at low priority, so loss at one hop that
/// vanishes further on is that router's indifference, not lost traffic (the
/// mtr manual's rule).
/// </param>
/// <param name="Minutes">The last hour, a minute at a time.</param>
public sealed record HopRow(
    int Distance,
    string? Address,
    IReadOnlyList<string> OtherAddresses,
    int Sent,
    int Received,
    double? LastMs,
    double? BestMs,
    double? AverageMs,
    double? WorstMs,
    double? DeviationMs,
    bool IsDestination,
    bool LossCarriesOn,
    IReadOnlyList<HopMinute> Minutes)
{
    /// <summary>The share of probes lost, 0 to 1.</summary>
    public double? Loss => Sent > 0 ? (double)(Sent - Received) / Sent : null;
}

/// <summary>The table as it stands.</summary>
/// <param name="Target">The far end.</param>
/// <param name="Since">When the table started.</param>
/// <param name="Rounds">How many rounds of probes have gone out.</param>
/// <param name="Rows">One row per hop, nearest first.</param>
public sealed record HopTableSnapshot(string Target, DateTimeOffset Since, int Rounds, IReadOnlyList<HopRow> Rows)
{
    /// <summary>The first hop whose loss carries on to the far end: where trouble starts.</summary>
    public HopRow? TroubleStartsAt => Rows.FirstOrDefault(each => each.LossCarriesOn && each.Loss > 0);
}

/// <summary>Keeps the hop-by-hop figures as rounds of probes come back. Pure, so it can be tested a reply at a time.</summary>
public sealed class HopTally
{
    private const int MinutesKept = 60;

    /// <summary>Loss within this of a hop's own still counts as carrying on.</summary>
    private const double Tolerance = 0.02;

    private readonly SortedDictionary<int, Hop> _hops = [];

    /// <summary>How many rounds have been counted.</summary>
    public int Rounds { get; private set; }

    /// <summary>The distance the far end answered from, when it has.</summary>
    public int? DestinationAt { get; private set; }

    /// <summary>Counts one round: a reply, or a silence (nothing), for each distance probed.</summary>
    public void Add(DateTimeOffset at, IReadOnlyList<HopReply> replies)
    {
        ArgumentNullException.ThrowIfNull(replies);

        Rounds++;
        DateTimeOffset minute = new(at.Ticks - (at.Ticks % TimeSpan.TicksPerMinute), at.Offset);

        foreach (HopReply reply in replies)
        {
            if (DestinationAt is { } far && reply.Distance > far)
            {
                continue;
            }

            if (!_hops.TryGetValue(reply.Distance, out Hop? hop))
            {
                hop = new Hop();
                _hops[reply.Distance] = hop;
            }

            hop.Count(minute, reply);

            if (reply.Outcome == HopOutcome.Arrived)
            {
                if (DestinationAt is null || reply.Distance < DestinationAt)
                {
                    DestinationAt = reply.Distance;

                    foreach (int beyond in _hops.Keys.Where(key => key > reply.Distance).ToList())
                    {
                        _hops.Remove(beyond);
                    }
                }
            }
        }
    }

    /// <summary>The distances worth probing next round: up to the far end, or one past the furthest that answered.</summary>
    public int Furthest(int most)
    {
        if (DestinationAt is { } far)
        {
            return far;
        }

        int answered = _hops.Where(pair => pair.Value.Received > 0).Select(pair => pair.Key).DefaultIfEmpty(0).Max();
        return Math.Clamp(answered + PathTrace.GiveUpAfter, 1, most);
    }

    /// <summary>The rows as they stand.</summary>
    public IReadOnlyList<HopRow> Rows()
    {
        List<(int Distance, Hop Hop)> hops = [.. _hops.Select(pair => (pair.Key, pair.Value))];

        // Trailing silence past the last hop that ever answered is not a hop.
        while (DestinationAt is null && hops.Count > 0 && hops[^1].Hop.Received == 0)
        {
            hops.RemoveAt(hops.Count - 1);
        }

        List<HopRow> rows = new(hops.Count);

        for (int each = 0; each < hops.Count; each++)
        {
            (int distance, Hop hop) = hops[each];
            double? loss = hop.Sent > 0 ? (double)(hop.Sent - hop.Received) / hop.Sent : null;

            bool carries = loss > 0 && hops.Skip(each + 1).All(later =>
                later.Hop.Sent == 0 || (double)(later.Hop.Sent - later.Hop.Received) / later.Hop.Sent >= loss - Tolerance);

            rows.Add(hop.Row(distance, distance == DestinationAt, carries));
        }

        return rows;
    }

    private sealed class Hop
    {
        private readonly Dictionary<string, int> _addresses = new(StringComparer.Ordinal);
        private readonly LinkedList<(DateTimeOffset Minute, int Sent, int Received, double Sum)> _minutes = new();
        private double _sum;
        private double _squares;
        private double? _last;
        private double? _best;
        private double? _worst;

        public int Sent { get; private set; }

        public int Received { get; private set; }

        public void Count(DateTimeOffset minute, HopReply reply)
        {
            Sent++;

            if (_minutes.Last is null || _minutes.Last.Value.Minute != minute)
            {
                _minutes.AddLast((minute, 0, 0, 0));

                while (_minutes.Count > MinutesKept)
                {
                    _minutes.RemoveFirst();
                }
            }

            (DateTimeOffset Minute, int Sent, int Received, double Sum) slot = _minutes.Last!.Value;
            slot.Sent++;

            if (reply.Outcome is HopOutcome.Onward or HopOutcome.Arrived && reply.RoundTrip is { } took)
            {
                double ms = took.TotalMilliseconds;
                Received++;
                _sum += ms;
                _squares += ms * ms;
                _last = ms;
                _best = _best is { } best ? Math.Min(best, ms) : ms;
                _worst = _worst is { } worst ? Math.Max(worst, ms) : ms;
                slot.Received++;
                slot.Sum += ms;

                if (reply.Address is { Length: > 0 } address)
                {
                    _addresses[address] = _addresses.GetValueOrDefault(address) + 1;
                }
            }

            _minutes.Last.Value = slot;
        }

        public HopRow Row(int distance, bool destination, bool carries)
        {
            double? mean = Received > 0 ? _sum / Received : null;
            double? deviation = Received > 1 ? Math.Sqrt(Math.Max(0, (_squares / Received) - (mean!.Value * mean.Value))) : null;
            string[] byCount = [.. _addresses.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key)];

            return new HopRow(
                distance,
                byCount.Length > 0 ? byCount[0] : null,
                byCount.Length > 1 ? byCount[1..] : [],
                Sent,
                Received,
                _last,
                _best,
                mean,
                _worst,
                deviation,
                destination,
                carries,
                [.. _minutes.Select(each => new HopMinute(each.Minute, each.Sent, each.Received, each.Received > 0 ? each.Sum / each.Received : null))]);
        }
    }
}
