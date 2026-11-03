using Windsock.Core.Apps;
using Windsock.Core.History;

namespace Windsock.Core.Plans;

/// <summary>One day of a cycle, as the plan counts it.</summary>
/// <param name="Date">The local date.</param>
/// <param name="Counted">Bytes that count against the allowance, measured and estimated.</param>
/// <param name="OffPeak">Bytes used in the free hours.</param>
/// <param name="Estimated">The part of <paramref name="Counted"/> that is Windows' figure for time Windsock was closed.</param>
/// <param name="IsFuture">Whether the day has not begun yet.</param>
public readonly record struct PlanDay(DateOnly Date, long Counted, long OffPeak, long Estimated, bool IsFuture);

/// <summary>What one network contributed to a cycle.</summary>
/// <param name="NetworkId">Its row in history; 0 for minutes whose network was not known.</param>
/// <param name="Counted">Bytes that counted against the allowance.</param>
/// <param name="OffPeak">Bytes in the free hours.</param>
public readonly record struct PlanNetworkUse(long NetworkId, long Counted, long OffPeak);

/// <summary>
/// A cycle's use as a plan counts it: what counts, what fell in the free hours,
/// and what was on networks the plan does not cover.
/// </summary>
/// <param name="Counted">Bytes counted against the allowance, measured and estimated.</param>
/// <param name="Measured">The part of <paramref name="Counted"/> Windsock recorded.</param>
/// <param name="Estimated">The part of <paramref name="Counted"/> that is Windows' figure for time Windsock was closed.</param>
/// <param name="OffPeak">Bytes used in the free hours, on the plan's networks.</param>
/// <param name="OtherNetworks">Bytes recorded on networks the plan does not cover.</param>
/// <param name="UnknownNetwork">
/// Bytes recorded without knowing the network, which a plan for chosen networks
/// cannot count - an older version's minutes, or the moment a network changed.
/// </param>
/// <param name="RecordedMinutes">How many minutes of the cycle Windsock recorded, on any network.</param>
/// <param name="Days">Each day of the cycle, the days still to come included.</param>
/// <param name="Networks">Each network's part, busiest first.</param>
/// <param name="Local">
/// Bytes on the plan's networks known to have stayed on the local network -
/// a NAS, a TV - which no plan counts and which are left out of everything
/// above. Zero where the local share is not known, which is everywhere but the
/// full edition run as administrator.
/// </param>
public sealed record PlanTally(
    long Counted,
    long Measured,
    long Estimated,
    long OffPeak,
    long OtherNetworks,
    long UnknownNetwork,
    long RecordedMinutes,
    IReadOnlyList<PlanDay> Days,
    IReadOnlyList<PlanNetworkUse> Networks,
    long Local = 0)
{
    /// <summary>A cycle nothing was recorded in.</summary>
    public static PlanTally Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, [], []);

    /// <summary>
    /// Counts a cycle.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="period">The cycle.</param>
    /// <param name="now">The present; days after it are future, and nothing after it is counted.</param>
    /// <param name="quarters">History's quarter hours in the cycle (see <see cref="IUsageAggregates.ReadQuarters"/>).</param>
    /// <param name="coveredIds">The history rows of the networks the plan covers; ignored for a plan on every network.</param>
    /// <param name="estimates">
    /// Windows' figures for time Windsock was closed in the cycle. Counted only
    /// by a plan on every network that asks for them, since they do not say
    /// which network they were on.
    /// </param>
    /// <param name="zone">The local clock.</param>
    public static PlanTally Count(
        DataPlan plan,
        BillingPeriod period,
        DateTimeOffset now,
        IReadOnlyList<UsageQuarter> quarters,
        IReadOnlySet<long> coveredIds,
        IReadOnlyList<UsageSpan> estimates,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(quarters);
        ArgumentNullException.ThrowIfNull(coveredIds);
        ArgumentNullException.ThrowIfNull(estimates);
        ArgumentNullException.ThrowIfNull(zone);

        long fromMinute = period.StartMinute;
        long untilMinute = Math.Min(period.EndMinute, EpochMinute.From(now) + 1);
        bool everyNetwork = plan.CoversEveryNetwork;
        bool downloadOnly = plan.Counts == PlanCounts.DownloadOnly;

        Dictionary<DateOnly, (long Counted, long OffPeak, long Estimated)> days = [];
        Dictionary<long, (long Counted, long OffPeak)> networks = [];
        long counted = 0, measured = 0, estimated = 0, offPeak = 0, other = 0, unknown = 0, minutes = 0, local = 0;

        foreach (UsageQuarter quarter in quarters)
        {
            if (quarter.StartMinute < fromMinute || quarter.StartMinute >= untilMinute)
            {
                continue;
            }

            minutes += quarter.Minutes;

            // Internet bytes only: what is known to have stayed on the local
            // network is no provider's business. Where that is not known the
            // whole figure counts, as it always has.
            long bytes = downloadOnly ? quarter.InternetDown : quarter.InternetTotal;
            long stayed = downloadOnly ? quarter.BytesDown - quarter.InternetDown : quarter.BytesTotal - quarter.InternetTotal;

            if (!everyNetwork && !coveredIds.Contains(quarter.NetworkId))
            {
                if (quarter.NetworkId == 0)
                {
                    unknown += bytes;
                }
                else
                {
                    other += bytes;
                }

                continue;
            }

            local += stayed;
            DateTime wall = LocalCalendar.Wall(quarter.StartMinute, zone);
            DateOnly date = DateOnly.FromDateTime(wall);
            (long dayCounted, long dayOff, long dayEstimated) = days.GetValueOrDefault(date);
            (long netCounted, long netOff) = networks.GetValueOrDefault(quarter.NetworkId);

            if (plan.OffPeak.Contains(LocalCalendar.MinuteOfDay(wall)))
            {
                offPeak += bytes;
                days[date] = (dayCounted, dayOff + bytes, dayEstimated);
                networks[quarter.NetworkId] = (netCounted, netOff + bytes);
            }
            else
            {
                counted += bytes;
                measured += bytes;
                days[date] = (dayCounted + bytes, dayOff, dayEstimated);
                networks[quarter.NetworkId] = (netCounted + bytes, netOff);
            }
        }

        if (everyNetwork && plan.CountWindowsEstimates)
        {
            foreach (UsageSpan span in estimates)
            {
                SpreadEstimate(plan, span, fromMinute, untilMinute, zone, days, ref counted, ref estimated, ref offPeak, downloadOnly);
            }
        }

        List<PlanDay> perDay = [];
        DateOnly today = LocalCalendar.DateOf(now, zone);
        DateOnly last = LocalCalendar.DateOf(period.End.AddMinutes(-1), zone);

        for (DateOnly date = LocalCalendar.DateOf(period.Start, zone); date <= last && perDay.Count < 1000; date = date.AddDays(1))
        {
            (long dayCounted, long dayOff, long dayEstimated) = days.GetValueOrDefault(date);
            perDay.Add(new PlanDay(date, dayCounted, dayOff, dayEstimated, date > today));
        }

        List<PlanNetworkUse> perNetwork = [.. networks.Select(each => new PlanNetworkUse(each.Key, each.Value.Counted, each.Value.OffPeak))];
        perNetwork.Sort(static (left, right) => (right.Counted + right.OffPeak).CompareTo(left.Counted + left.OffPeak));

        return new PlanTally(counted, measured, estimated, offPeak, other, unknown, minutes, perDay, perNetwork, local);
    }

    /// <summary>
    /// Shares one of Windows' figures out over its minutes, a quarter hour at a
    /// time, so the part of it in the free hours is free and each day gets its own.
    /// </summary>
    private static void SpreadEstimate(
        DataPlan plan,
        UsageSpan span,
        long fromMinute,
        long untilMinute,
        TimeZoneInfo zone,
        Dictionary<DateOnly, (long Counted, long OffPeak, long Estimated)> days,
        ref long counted,
        ref long estimated,
        ref long offPeak,
        bool downloadOnly)
    {
        if (span.Minutes <= 0)
        {
            return;
        }

        long bytes = downloadOnly ? span.BytesReceived : span.BytesReceived + span.BytesSent;
        long given = 0;
        long start = Math.Max(span.StartMinute, fromMinute);
        long end = Math.Min(span.EndMinute, untilMinute);

        for (long at = start; at < end;)
        {
            long next = Math.Min(end, at - (((at % 15) + 15) % 15) + 15);
            long share = next >= span.EndMinute && end == span.EndMinute && start == span.StartMinute
                ? bytes - given
                : bytes * (next - at) / span.Minutes;
            given += share;

            DateTime wall = LocalCalendar.Wall(at, zone);
            DateOnly date = DateOnly.FromDateTime(wall);
            (long dayCounted, long dayOff, long dayEstimated) = days.GetValueOrDefault(date);

            if (plan.OffPeak.Contains(LocalCalendar.MinuteOfDay(wall)))
            {
                offPeak += share;
                days[date] = (dayCounted, dayOff + share, dayEstimated);
            }
            else
            {
                counted += share;
                estimated += share;
                days[date] = (dayCounted + share, dayOff, dayEstimated + share);
            }

            at = next;
        }
    }
}
