using System.Globalization;
using System.Text;
using Windsock.Core.Apps;
using Windsock.Core.History;

namespace Windsock.Core.Plans;

/// <summary>
/// Works out a plan's status from history: the cycle, what it counted, what
/// was carried in, and the outlook.
/// </summary>
/// <remarks>
/// A month's cycle is a few thousand quarter-hour rows, so a status costs a
/// millisecond or two. What earlier cycles counted is needed only for rollover.
/// A cycle's total can still change after it ends - its last minute is written
/// a moment late, and Windows' figures for time Windsock was closed are
/// collected up to <see cref="AppUsagePlan.Backfill"/> back - so a total is
/// kept only once its cycle ended longer ago than that (<see cref="Settled"/>),
/// and then until history itself changes (<see cref="IUsageAggregates.Revision"/>:
/// an import or an erase). It is kept under everything that decides it: the
/// plan's counting rules, the time zone, and the cycle's start and end, since
/// a daily and a monthly cycle can start at the same minute. Windows' figures
/// for time Windsock was closed take a few small reads, and Windows only adds
/// to them every few minutes, so they are kept for a few minutes per cycle.
/// </remarks>
public sealed class PlanCalculator
{
    private static readonly TimeSpan EstimatesKept = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long after a cycle ends nothing more can arrive for it: Windows'
    /// figures are collected up to <see cref="AppUsagePlan.Backfill"/> back,
    /// and a day more covers a catch-up that began just inside that.
    /// </summary>
    internal static readonly TimeSpan Settled = TimeSpan.FromMinutes(AppUsagePlan.Backfill) + TimeSpan.FromDays(1);

    private readonly IUsageAggregates _aggregates;
    private readonly IUsageHistoryStore _recorded;
    private readonly IAppUsageStore _windows;
    private readonly Func<TimeZoneInfo> _zone;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Rule, long Start, long End), long> _pastCounted = [];
    private readonly Dictionary<(long Start, long End), (DateTimeOffset At, IReadOnlyList<UsageSpan> Spans)> _estimates = [];
    private long _revision = long.MinValue;

    public PlanCalculator(IUsageAggregates aggregates, IUsageHistoryStore recorded, IAppUsageStore windows)
        : this(aggregates, recorded, windows, static () => TimeZoneInfo.Local)
    {
    }

    internal PlanCalculator(IUsageAggregates aggregates, IUsageHistoryStore recorded, IAppUsageStore windows, Func<TimeZoneInfo> zone)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(zone);

        _aggregates = aggregates;
        _recorded = recorded;
        _windows = windows;
        _zone = zone;
    }

    /// <summary>The time zone cycles and days are counted in.</summary>
    public TimeZoneInfo Zone => _zone();

    /// <summary>
    /// A plan's current cycle as it stands, or null for a one-off plan without
    /// its dates. Throws a storage failure when history cannot be read.
    /// </summary>
    public PlanStatus? Status(DataPlan plan, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);

        TimeZoneInfo zone = _zone();
        IReadOnlyList<BillingPeriod> periods = PlanCycles.Through(plan, now, zone);

        if (periods.Count == 0)
        {
            return null;
        }

        BillingPeriod current = periods[^1];
        IReadOnlySet<long> covered = CoveredIds(plan);
        PlanTally tally = Count(plan, current, now, covered, zone, now);
        long carried = Carried(plan, periods, tally.Counted, covered, zone, now);
        long allowance = plan.LimitBytes > 0 ? plan.LimitBytes + carried : 0;

        PlanState state = now < current.Start ? PlanState.NotStarted
            : now >= current.End ? PlanState.Ended
            : PlanState.Running;

        return new PlanStatus(plan, current, state, now, carried, tally, PlanForecast.Look(current, now, tally.Counted, allowance, zone));
    }

    /// <summary>
    /// The plan's cycles before the current one, newest first, each as it
    /// finished: for a list of earlier cycles.
    /// </summary>
    public IReadOnlyList<PlanStatus> Earlier(DataPlan plan, DateTimeOffset now, int most)
    {
        ArgumentNullException.ThrowIfNull(plan);

        TimeZoneInfo zone = _zone();
        List<PlanStatus> earlier = [];

        if (PlanCycles.Containing(plan, now, zone) is not { } current)
        {
            return earlier;
        }

        IReadOnlySet<long> covered = CoveredIds(plan);
        BillingPeriod each = current;

        while (earlier.Count < most && PlanCycles.Before(plan, each, zone) is { } before)
        {
            if (plan.Since is { } since && before.End <= since)
            {
                break;
            }

            PlanTally tally = Count(plan, before, before.End, covered, zone, now);
            IReadOnlyList<BillingPeriod> chain = PlanCycles.Through(plan, before.Start, zone);
            long carried = Carried(plan, chain, tally.Counted, covered, zone, now);
            long allowance = plan.LimitBytes > 0 ? plan.LimitBytes + carried : 0;

            earlier.Add(new PlanStatus(plan, before, PlanState.Ended, before.End, carried, tally, PlanForecast.Look(before, before.End, tally.Counted, allowance, zone)));
            each = before;
        }

        return earlier;
    }

    private HashSet<long> CoveredIds(DataPlan plan)
    {
        if (plan.CoversEveryNetwork)
        {
            return [];
        }

        IReadOnlyDictionary<string, long> ids = _aggregates.ReadNetworkIds();
        HashSet<long> covered = [];

        foreach (PlanNetwork network in plan.Networks)
        {
            if (ids.TryGetValue(network.Key, out long id))
            {
                covered.Add(id);
            }
        }

        return covered;
    }

    /// <summary>What a cycle counted up to <paramref name="now"/>, worked out when it is <paramref name="asked"/>.</summary>
    private PlanTally Count(DataPlan plan, BillingPeriod period, DateTimeOffset now, IReadOnlySet<long> covered, TimeZoneInfo zone, DateTimeOffset asked)
    {
        if (now <= period.Start)
        {
            return PlanTally.Count(plan, period, now, [], covered, [], zone);
        }

        DateTimeOffset until = now < period.End ? now : period.End;
        long untilMinute = Math.Min(period.EndMinute, EpochMinute.From(until) + 1);
        IReadOnlyList<UsageQuarter> quarters = _aggregates.ReadQuarters(period.StartMinute, untilMinute);
        IReadOnlyList<UsageSpan> estimates = plan.CoversEveryNetwork && plan.CountWindowsEstimates
            ? Estimates(period, until, asked)
            : [];

        return PlanTally.Count(plan, period, now, quarters, covered, estimates, zone);
    }

    /// <summary>Windows' figures for a cycle's closed time, as read at <paramref name="now"/> or within a few minutes before.</summary>
    private IReadOnlyList<UsageSpan> Estimates(BillingPeriod period, DateTimeOffset until, DateTimeOffset now)
    {
        // Kept per cycle, not per moment: a running cycle's end moves every
        // minute, and a key holding it would never be found again.
        (long, long) key = (period.StartMinute, period.EndMinute);
        DateTimeOffset from = period.Start;

        lock (_gate)
        {
            Forget();

            if (_estimates.TryGetValue(key, out (DateTimeOffset At, IReadOnlyList<UsageSpan> Spans) kept) && now - kept.At < EstimatesKept)
            {
                return kept.Spans;
            }
        }

        // The fill History draws: windows with no recorded minute, and what
        // Windows moved out of the time Windsock was closed.
        IReadOnlyList<UsageSpan> spans = MovedTraffic.Fill(_recorded, _windows, from, until);

        lock (_gate)
        {
            if (_estimates.Count > 32)
            {
                _estimates.Clear();
            }

            _estimates[key] = (now, spans);
        }

        return spans;
    }

    /// <summary>
    /// What was carried into the last of a run of cycles, given what the last
    /// counted. The run starts with the cycle holding <see cref="DataPlan.Since"/>,
    /// which is the one <see cref="DataPlan.CarriedInBytes"/> goes into; a plan
    /// without a start has nothing to carry.
    /// </summary>
    private long Carried(DataPlan plan, IReadOnlyList<BillingPeriod> periods, long lastCounted, IReadOnlySet<long> covered, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (plan.LimitBytes <= 0 || plan.Since is null || periods.Count == 0)
        {
            return 0;
        }

        if (periods.Count == 1)
        {
            return PlanRollover.Carried(plan, [lastCounted])[0];
        }

        if (plan.Rollover == RolloverRule.None)
        {
            return 0;
        }

        string rule = string.Concat(Rule(plan), "|", zone.Id);
        List<long> counted = new(periods.Count);

        for (int each = 0; each < periods.Count - 1; each++)
        {
            BillingPeriod period = periods[each];
            (string, long, long) key = (rule, period.StartMinute, period.EndMinute);
            bool settled = now - period.End > Settled;

            if (settled)
            {
                lock (_gate)
                {
                    Forget();

                    if (_pastCounted.TryGetValue(key, out long kept))
                    {
                        counted.Add(kept);
                        continue;
                    }
                }
            }

            long found = Count(plan, period, period.End, covered, zone, now).Counted;

            if (settled)
            {
                lock (_gate)
                {
                    _pastCounted[key] = found;
                }
            }

            counted.Add(found);
        }

        counted.Add(lastCounted);
        return PlanRollover.Carried(plan, counted)[^1];
    }

    /// <summary>Drops what was kept when history changed under it. Call with the gate held.</summary>
    private void Forget()
    {
        long revision = _aggregates.Revision;

        if (revision != _revision)
        {
            _revision = revision;
            _pastCounted.Clear();
            _estimates.Clear();
        }
    }

    /// <summary>Everything about a plan that decides what a finished cycle counted.</summary>
    private static string Rule(DataPlan plan)
    {
        StringBuilder rule = new();
        rule.Append(plan.Counts).Append('|').Append(plan.CountWindowsEstimates).Append('|');
        rule.Append(plan.OffPeak.Enabled).Append(',').Append(plan.OffPeak.Start.ToString(CultureInfo.InvariantCulture))
            .Append(',').Append(plan.OffPeak.End.ToString(CultureInfo.InvariantCulture)).Append('|');

        foreach (string key in plan.Networks.Select(network => network.Key).Order(StringComparer.Ordinal))
        {
            rule.Append(key).Append(';');
        }

        return rule.ToString();
    }
}
