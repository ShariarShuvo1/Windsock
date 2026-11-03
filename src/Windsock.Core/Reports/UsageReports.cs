using Windsock.Core.Apps;
using Windsock.Core.Connectivity;
using Windsock.Core.History;
using Windsock.Core.Networking;

namespace Windsock.Core.Reports;

/// <summary>
/// Reports over history: a day, week or month with its peaks and busiest
/// times, two stretches compared, a year's calendar, the hours of the week,
/// everything since recording began, and one app over time.
/// </summary>
/// <remarks>
/// <para>
/// Built from history's quarter-hour totals (<see cref="IUsageAggregates"/>),
/// bucketed on the local clock here rather than in SQL, so a time zone can be
/// handed in and the clocks changing is handled once, in
/// <see cref="LocalCalendar"/>. A year is about thirty-five thousand rows.
/// </para>
/// <para>
/// What Windows counted while Windsock was closed is the fill History draws
/// (<see cref="MovedTraffic.Fill"/>), kept apart in every total
/// (<see cref="UsageTotals.Estimated"/>) and placed in the bucket it starts in,
/// as History places it. Every call reads history and may throw a storage
/// failure (<see cref="HistoryFailure.IsStorage"/>); call off the UI thread.
/// </para>
/// <para>
/// Every report can be made on internet bytes alone (<c>internetOnly</c>):
/// busiest times, profiles, shades and changes then leave out what is known to
/// have stayed on the local network (see <see cref="ILocalTrafficStore"/>),
/// and take the whole figure wherever that is not known.
/// </para>
/// </remarks>
public sealed class UsageReports
{
    private readonly IUsageAggregates _aggregates;
    private readonly IUsageHistoryStore _recorded;
    private readonly IAppUsageStore _windows;
    private readonly AppHistory _apps;
    private readonly IOutageStore? _outages;
    private readonly TimeProvider _clock;
    private readonly Func<TimeZoneInfo> _zone;

    public UsageReports(
        IUsageAggregates aggregates,
        IUsageHistoryStore recorded,
        IAppUsageStore windows,
        AppHistory apps,
        IOutageStore outages,
        TimeProvider clock)
        : this(aggregates, recorded, windows, apps, outages, clock, static () => TimeZoneInfo.Local)
    {
    }

    internal UsageReports(
        IUsageAggregates aggregates,
        IUsageHistoryStore recorded,
        IAppUsageStore windows,
        AppHistory apps,
        IOutageStore? outages,
        TimeProvider clock,
        Func<TimeZoneInfo> zone)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(zone);

        _aggregates = aggregates;
        _recorded = recorded;
        _windows = windows;
        _apps = apps;
        _outages = outages;
        _clock = clock;
        _zone = zone;
    }

    /// <summary>The time zone reports are made in.</summary>
    public TimeZoneInfo Zone => _zone();

    /// <summary>A report on a stretch: up to now when it is still going.</summary>
    /// <param name="period">The stretch.</param>
    /// <param name="topApps">How many of the busiest apps to name.</param>
    /// <param name="withChange">Whether to compare with the stretch before.</param>
    /// <param name="internetOnly">Whether to rank and compare by internet bytes alone.</param>
    public PeriodReport Report(ReportPeriod period, int topApps = 10, bool withChange = true, bool internetOnly = false)
    {
        ArgumentNullException.ThrowIfNull(period);

        TimeZoneInfo zone = _zone();
        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset until = Clip(period, now);
        long fromMinute = period.FromMinute;
        long untilMinute = EpochMinute.From(until);

        List<Piece> pieces = Gather(fromMinute, untilMinute);
        UsageTotals totals = Sum(pieces);

        List<ReportPoint> series = Series(pieces, period.From, period.Until, period.Step, zone, period.FirstDayOfWeek);
        List<ReportPoint> days = period.Step == CalendarStep.Day ? series : Series(pieces, period.From, until, CalendarStep.Day, zone, period.FirstDayOfWeek);
        List<ReportPoint> hours = period.Step == CalendarStep.Hour ? series : Series(pieces, period.From, until, CalendarStep.Hour, zone, period.FirstDayOfWeek);

        long[] hourOfDay = new long[24];
        long[] dayOfWeek = new long[7];

        foreach (Piece piece in pieces)
        {
            DateTime wall = LocalCalendar.Wall(piece.Minute, zone);
            hourOfDay[wall.Hour] += piece.Totals.Measure(internetOnly);
            dayOfWeek[(int)wall.DayOfWeek] += piece.Totals.Measure(internetOnly);
        }

        PeriodChange? change = null;

        if (withChange)
        {
            ReportPeriod before = period.Previous(zone);
            bool cut = until < period.Until;
            long beforeUntil = cut ? Math.Min(before.UntilMinute, before.FromMinute + (untilMinute - fromMinute)) : before.UntilMinute;
            change = PeriodChange.Between(Sum(Gather(before.FromMinute, beforeUntil)), totals, cut, internetOnly);
        }

        IReadOnlyList<AppShare> apps = untilMinute > fromMinute ? _apps.Totals(period.From, until) : [];

        return new PeriodReport(
            period,
            now,
            totals,
            until > period.From ? until - period.From : TimeSpan.Zero,
            Peak(fromMinute, untilMinute, upload: false, zone),
            Peak(fromMinute, untilMinute, upload: true, zone),
            period.Length > TimeSpan.FromDays(1.5) ? Busiest(days, internetOnly) : null,
            Busiest(hours, internetOnly),
            series,
            hourOfDay,
            dayOfWeek,
            Networks(pieces),
            [.. apps.Take(Math.Max(0, topApps))],
            Outages(period.From, until, now),
            change,
            internetOnly);
    }

    /// <summary>
    /// Two stretches side by side: "this month against last", "this week
    /// against the same week last year". A stretch still going is taken up to
    /// now, and the difference and the apps' changes are then against the same
    /// length of the baseline from its start ("so far" against "by this point"),
    /// while the step-by-step chart still holds the whole baseline.
    /// </summary>
    public PeriodComparison Compare(ReportPeriod baseline, ReportPeriod compared, int apps = 20, bool internetOnly = false)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(compared);

        PeriodReport first = Report(baseline, topApps: int.MaxValue, withChange: false, internetOnly);
        PeriodReport second = Report(compared, topApps: int.MaxValue, withChange: false, internetOnly);

        List<(ReportPoint?, ReportPoint?)> aligned = [];
        int steps = Math.Max(first.Series.Count, second.Series.Count);

        for (int each = 0; each < steps; each++)
        {
            aligned.Add((
                each < first.Series.Count ? first.Series[each] : null,
                each < second.Series.Count ? second.Series[each] : null));
        }

        // A stretch under way is set against the same length of the baseline.
        bool cut = second.IsUnderWay && second.Elapsed < baseline.Length;
        long baselineUntil = cut ? Math.Min(baseline.UntilMinute, baseline.FromMinute + (long)second.Elapsed.TotalMinutes) : baseline.UntilMinute;
        UsageTotals earlier = cut ? Sum(Gather(baseline.FromMinute, baselineUntil)) : first.Totals;
        IReadOnlyList<AppShare> appsBefore = cut
            ? (baselineUntil > baseline.FromMinute ? _apps.Totals(baseline.From, EpochMinute.ToUtc(baselineUntil)) : [])
            : first.TopApps;

        Dictionary<string, (long Before, long After)> changes = new(StringComparer.Ordinal);

        foreach (AppShare share in appsBefore)
        {
            changes[share.AttributionId] = (share.Total, 0);
        }

        foreach (AppShare share in second.TopApps)
        {
            (long before, _) = changes.GetValueOrDefault(share.AttributionId);
            changes[share.AttributionId] = (before, share.Total);
        }

        List<AppChange> appChanges = [.. changes.Select(each => new AppChange(each.Key, each.Value.Before, each.Value.After))];
        appChanges.Sort(static (left, right) => Math.Abs(right.Difference).CompareTo(Math.Abs(left.Difference)));

        long then = earlier.Measure(internetOnly);
        long difference = second.Totals.Measure(internetOnly) - then;

        return new PeriodComparison(
            first with { TopApps = [.. first.TopApps.Take(apps)] },
            second with { TopApps = [.. second.TopApps.Take(apps)] },
            difference,
            then > 0 ? (double)difference / then : null,
            aligned,
            [.. appChanges.Take(apps)],
            cut);
    }

    /// <summary>Every day of a year, shaded by how much it moved.</summary>
    public YearCalendar Calendar(int year, bool internetOnly = false)
    {
        TimeZoneInfo zone = _zone();
        ReportPeriod period = ReportPeriod.Year(year, zone);
        DateTimeOffset until = Clip(period, _clock.GetUtcNow());
        List<Piece> pieces = Gather(period.FromMinute, EpochMinute.From(until));

        Dictionary<DateOnly, UsageTotals> byDay = [];

        foreach (Piece piece in pieces)
        {
            DateOnly date = DateOnly.FromDateTime(LocalCalendar.Wall(piece.Minute, zone));
            byDay[date] = byDay.GetValueOrDefault(date) + piece.Totals;
        }

        long[] busy = [.. byDay.Values.Select(totals => totals.Measure(internetOnly)).Where(value => value > 0).Order()];
        List<CalendarDay> days = [];
        CalendarDay? busiest = null;

        for (DateOnly date = new(year, 1, 1); date.Year == year; date = date.AddDays(1))
        {
            UsageTotals totals = byDay.GetValueOrDefault(date);
            long value = totals.Measure(internetOnly);
            CalendarDay day = new(date, totals, Shade(value, busy));
            days.Add(day);

            if (value > 0 && (busiest is null || value > busiest.Value.Totals.Measure(internetOnly)))
            {
                busiest = day;
            }
        }

        return new YearCalendar(year, days, Sum(pieces), busiest);
    }

    /// <summary>When in the week the network is busiest, over a stretch.</summary>
    public WeekHeat WeekHeat(DateTimeOffset from, DateTimeOffset until, bool internetOnly = false)
    {
        TimeZoneInfo zone = _zone();
        long[][] totals = [.. Enumerable.Range(0, 7).Select(_ => new long[24])];
        int[][] hours = [.. Enumerable.Range(0, 7).Select(_ => new int[24])];
        HashSet<(DateOnly, int)> seen = [];

        foreach (Piece piece in Gather(EpochMinute.From(from), EpochMinute.From(until)))
        {
            DateTime wall = LocalCalendar.Wall(piece.Minute, zone);
            int day = (int)wall.DayOfWeek;
            totals[day][wall.Hour] += piece.Totals.Measure(internetOnly);

            if (seen.Add((DateOnly.FromDateTime(wall), wall.Hour)))
            {
                hours[day][wall.Hour]++;
            }
        }

        return new WeekHeat(from, until, totals, hours);
    }

    /// <summary>Everything since recording began, and each year of it.</summary>
    public LifetimeReport Lifetime(bool internetOnly = false)
    {
        TimeZoneInfo zone = _zone();
        UsageExtent extent = _recorded.Extent();

        if (extent.First is not { } first || extent.Last is not { } last)
        {
            return new LifetimeReport(null, null, UsageTotals.Empty, [], null, null, null);
        }

        long fromMinute = EpochMinute.From(first);
        long untilMinute = EpochMinute.From(last) + 1;
        List<Piece> pieces = Gather(fromMinute, untilMinute);

        SortedDictionary<int, UsageTotals> years = [];
        Dictionary<DateOnly, UsageTotals> days = [];

        foreach (Piece piece in pieces)
        {
            DateOnly date = DateOnly.FromDateTime(LocalCalendar.Wall(piece.Minute, zone));
            years[date.Year] = years.GetValueOrDefault(date.Year) + piece.Totals;
            days[date] = days.GetValueOrDefault(date) + piece.Totals;
        }

        ReportPoint? busiest = null;

        foreach ((DateOnly date, UsageTotals totals) in days)
        {
            long value = totals.Measure(internetOnly);

            if (value > 0 && (busiest is null || value > busiest.Value.Totals.Measure(internetOnly)))
            {
                busiest = new ReportPoint(LocalCalendar.StartOf(date, zone), totals);
            }
        }

        return new LifetimeReport(
            first,
            last,
            Sum(pieces),
            [.. years.Select(each => new YearTotal(each.Key, each.Value))],
            busiest,
            Peak(fromMinute, untilMinute, upload: false, zone),
            Peak(fromMinute, untilMinute, upload: true, zone));
    }

    /// <summary>
    /// One app over a stretch, step by step: what Windsock measured of it, and
    /// what Windows counted where Windsock did not measure. A Windows record
    /// counts in the step it starts in, when it lies wholly inside the stretch,
    /// as History counts it.
    /// </summary>
    public IReadOnlyList<AppPoint> AppOverTime(string attributionId, DateTimeOffset from, DateTimeOffset until, CalendarStep step, DayOfWeek firstDay = DayOfWeek.Monday)
    {
        ArgumentNullException.ThrowIfNull(attributionId);

        TimeZoneInfo zone = _zone();
        List<AppPoint> points = [];
        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset end = until < now ? until : now;

        for (DateTimeOffset start = LocalCalendar.BucketStart(EpochMinute.From(from), step, zone, firstDay);
             start < until && points.Count < 2000;
             start = LocalCalendar.NextBucket(start, step, zone))
        {
            DateTimeOffset next = LocalCalendar.NextBucket(start, step, zone);
            DateTimeOffset bucketFrom = start < from ? from : start;
            DateTimeOffset bucketUntil = next < end ? next : end;
            AppShare share = default;

            if (bucketUntil > bucketFrom)
            {
                foreach (AppShare each in _apps.Breakdown(bucketFrom, bucketUntil, end))
                {
                    if (string.Equals(each.AttributionId, attributionId, StringComparison.Ordinal))
                    {
                        share = each;
                        break;
                    }
                }
            }

            points.Add(new AppPoint(start, share.MeasuredDown, share.MeasuredUp, share.WindowsDown, share.WindowsUp));
        }

        return points;
    }

    /// <summary>A stretch's end, or now when the stretch is still going; never before its start.</summary>
    private static DateTimeOffset Clip(ReportPeriod period, DateTimeOffset now) =>
        now >= period.Until ? period.Until : now <= period.From ? period.From : now;

    /// <summary>A minute or quarter hour recorded, or a stretch Windows counted, with where it starts.</summary>
    private readonly record struct Piece(long Minute, long NetworkId, UsageTotals Totals);

    /// <summary>Everything in a range, in time order.</summary>
    private List<Piece> Gather(long fromMinute, long untilMinute)
    {
        List<Piece> pieces = [];

        if (untilMinute <= fromMinute)
        {
            return pieces;
        }

        foreach (UsageQuarter quarter in _aggregates.ReadQuarters(fromMinute, untilMinute))
        {
            pieces.Add(new Piece(
                quarter.StartMinute,
                quarter.NetworkId,
                new UsageTotals(quarter.BytesDown, quarter.BytesUp, 0, 0, quarter.Minutes, 0, quarter.LocalDown, quarter.LocalUp)));
        }

        foreach (UsageSpan span in MovedTraffic.Fill(_recorded, _windows, EpochMinute.ToUtc(fromMinute), EpochMinute.ToUtc(untilMinute)))
        {
            pieces.Add(new Piece(span.StartMinute, -1, new UsageTotals(0, 0, span.BytesReceived, span.BytesSent, 0, span.Minutes)));
        }

        pieces.Sort(static (left, right) => left.Minute.CompareTo(right.Minute));
        return pieces;
    }

    private static UsageTotals Sum(List<Piece> pieces)
    {
        UsageTotals sum = UsageTotals.Empty;

        foreach (Piece piece in pieces)
        {
            sum += piece.Totals;
        }

        return sum;
    }

    /// <summary>Every step of a stretch with what fell in it, the empty ones included.</summary>
    private static List<ReportPoint> Series(List<Piece> pieces, DateTimeOffset from, DateTimeOffset until, CalendarStep step, TimeZoneInfo zone, DayOfWeek firstDay)
    {
        List<ReportPoint> series = [];
        List<long> ends = [];

        for (DateTimeOffset start = LocalCalendar.BucketStart(EpochMinute.From(from), step, zone, firstDay);
             start < until && series.Count < 20_000;
             start = LocalCalendar.NextBucket(start, step, zone))
        {
            series.Add(new ReportPoint(start, UsageTotals.Empty));
            ends.Add(EpochMinute.From(LocalCalendar.NextBucket(start, step, zone)));
        }

        // Pieces are in time order, and so are the steps.
        int at = 0;

        foreach (Piece piece in pieces)
        {
            while (at < ends.Count && piece.Minute >= ends[at])
            {
                at++;
            }

            if (at >= series.Count)
            {
                break;
            }

            if (piece.Minute >= EpochMinute.From(series[at].Start))
            {
                series[at] = series[at] with { Totals = series[at].Totals + piece.Totals };
            }
        }

        return series;
    }

    private static ReportPoint? Busiest(List<ReportPoint> points, bool internetOnly)
    {
        ReportPoint? busiest = null;

        foreach (ReportPoint point in points)
        {
            long value = point.Totals.Measure(internetOnly);

            if (value > 0 && (busiest is null || value > busiest.Value.Totals.Measure(internetOnly)))
            {
                busiest = point;
            }
        }

        return busiest;
    }

    private List<NetworkShare> Networks(List<Piece> pieces)
    {
        Dictionary<long, (long Down, long Up, long Minutes)> byNetwork = [];

        foreach (Piece piece in pieces)
        {
            if (piece.NetworkId < 0)
            {
                continue;
            }

            (long down, long up, long minutes) = byNetwork.GetValueOrDefault(piece.NetworkId);
            byNetwork[piece.NetworkId] = (down + piece.Totals.Down, up + piece.Totals.Up, minutes + piece.Totals.RecordedMinutes);
        }

        if (byNetwork.Count == 0)
        {
            return [];
        }

        IReadOnlyDictionary<long, NetworkTag> tags = _aggregates.ReadNetworkTags();
        List<NetworkShare> shares = [];

        foreach ((long id, (long down, long up, long minutes)) in byNetwork)
        {
            NetworkTag? tag = tags.GetValueOrDefault(id);
            shares.Add(new NetworkShare(id, tag?.Key, tag?.Name, tag?.Kind, down, up, minutes));
        }

        shares.Sort(static (left, right) => right.Total.CompareTo(left.Total));
        return shares;
    }

    private PeakRate? Peak(long fromMinute, long untilMinute, bool upload, TimeZoneInfo zone) =>
        _aggregates.ReadPeak(fromMinute, untilMinute, upload) is { } peak
            ? new PeakRate(TimeZoneInfo.ConvertTime(EpochMinute.ToUtc(peak.Minute), zone), peak.BytesPerSecond)
            : null;

    private OutageSummary Outages(DateTimeOffset from, DateTimeOffset until, DateTimeOffset now)
    {
        if (_outages is null || until <= from)
        {
            return default;
        }

        IReadOnlyList<Outage> outages;

        try
        {
            outages = _outages.Read(from, until);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            // The outage log is a file of its own; a report goes on without it.
            return default;
        }

        TimeSpan offline = TimeSpan.Zero;

        foreach (Outage outage in outages)
        {
            DateTimeOffset start = outage.Start > from ? outage.Start : from;
            DateTimeOffset end = outage.End ?? now;
            end = end < until ? end : until;

            if (end > start)
            {
                offline += end - start;
            }
        }

        return new OutageSummary(outages.Count, offline);
    }

    /// <summary>How dark a day is drawn: by quartiles of the days that moved anything.</summary>
    internal static int Shade(long value, long[] sortedBusy)
    {
        if (value <= 0 || sortedBusy.Length == 0)
        {
            return 0;
        }

        long Quartile(double share) => sortedBusy[Math.Clamp((int)Math.Ceiling(share * sortedBusy.Length) - 1, 0, sortedBusy.Length - 1)];

        return value <= Quartile(0.25) ? 1
            : value <= Quartile(0.50) ? 2
            : value <= Quartile(0.75) ? 3
            : 4;
    }
}
