using Windsock.Core.History;

namespace Windsock.Core.Plans;

/// <summary>
/// Allowance carried from one cycle into the next.
/// </summary>
public static class PlanRollover
{
    /// <summary>
    /// What is carried into each cycle, given what each counted, oldest first:
    /// element <c>i</c> of the result is what cycle <c>i</c> starts with beyond
    /// its own allowance. The first cycle starts with
    /// <see cref="DataPlan.CarriedInBytes"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RolloverRule.NextCycleOnly"/>: what is carried is used first,
    /// and what is left of the cycle's own allowance is all that moves on - so
    /// carried data never rolls twice. <see cref="RolloverRule.Accumulate"/>:
    /// everything unused moves on. Either way the cap, when there is one, holds.
    /// A plan without an allowance carries nothing.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<long> Carried(DataPlan plan, IReadOnlyList<long> countedPerCycle)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(countedPerCycle);

        long[] carried = new long[countedPerCycle.Count];

        if (carried.Length == 0)
        {
            return carried;
        }

        long limit = Math.Max(0, plan.LimitBytes);
        carried[0] = limit > 0 ? Cap(plan, Math.Max(0, plan.CarriedInBytes)) : 0;

        for (int each = 1; each < carried.Length; each++)
        {
            long used = Math.Max(0, countedPerCycle[each - 1]);
            long before = carried[each - 1];

            long next = limit <= 0 ? 0 : plan.Rollover switch
            {
                RolloverRule.NextCycleOnly => Math.Max(0, limit - Math.Max(0, used - before)),
                RolloverRule.Accumulate => Math.Max(0, limit + before - used),
                _ => 0,
            };

            carried[each] = Cap(plan, next);
        }

        return carried;
    }

    private static long Cap(DataPlan plan, long carried) =>
        plan.RolloverCapBytes > 0 ? Math.Min(carried, plan.RolloverCapBytes) : carried;
}

/// <summary>Where a cycle is heading, at the pace it has gone so far.</summary>
/// <param name="Projected">
/// What the cycle will have counted by its end at this pace, or null while it
/// is too early to say or once it is over.
/// </param>
/// <param name="DailyBudget">
/// What can be used each day left, today included, and still stay within the
/// allowance; null for a plan without one or a cycle that is over.
/// </param>
/// <param name="DaysLeft">Days left, today included: the 13th of a cycle ending as the 30th begins has 17.</param>
/// <param name="TimeLeft">Time until the cycle ends.</param>
/// <param name="RunsOutAt">When the allowance runs out at this pace, or null when it will not before the cycle ends.</param>
/// <param name="TooEarly">Whether so little of the cycle has gone that a pace means nothing yet.</param>
/// <param name="LastDay">The cycle's last local date: "312 GB by the 30th".</param>
public sealed record PlanOutlook(
    long? Projected,
    long? DailyBudget,
    int DaysLeft,
    TimeSpan TimeLeft,
    DateTimeOffset? RunsOutAt,
    bool TooEarly,
    DateOnly LastDay);

/// <summary>Works out a cycle's outlook.</summary>
public static class PlanForecast
{
    /// <summary>The least of a cycle that must have gone before a pace is given.</summary>
    public static readonly TimeSpan Earliest = TimeSpan.FromHours(12);

    /// <summary>The least share of a cycle that must have gone, for short cycles.</summary>
    public const double EarliestShare = 0.10;

    /// <summary>
    /// The outlook for a cycle.
    /// </summary>
    /// <param name="period">The cycle.</param>
    /// <param name="now">The present.</param>
    /// <param name="counted">What the cycle has counted so far.</param>
    /// <param name="allowance">The cycle's allowance with anything carried in; zero for none.</param>
    /// <param name="zone">The local clock.</param>
    /// <remarks>
    /// The pace is the cycle's own average so far: counted bytes over the time
    /// gone, free hours included, since they come round every day. Time
    /// Windsock was closed for is in the time gone too, so a plan whose
    /// estimates are not counted reads low after a long gap; the page says how
    /// much of the cycle was recorded.
    /// </remarks>
    public static PlanOutlook Look(BillingPeriod period, DateTimeOffset now, long counted, long allowance, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        DateOnly lastDay = LocalCalendar.DateOf(period.End.AddMinutes(-1), zone);

        if (now >= period.End)
        {
            return new PlanOutlook(null, null, 0, TimeSpan.Zero, null, TooEarly: false, lastDay);
        }

        DateTimeOffset from = now < period.Start ? period.Start : now;
        TimeSpan gone = from - period.Start;
        TimeSpan left = period.End - from;
        DateOnly today = LocalCalendar.DateOf(from, zone);
        int daysLeft = Math.Max(1, lastDay.DayNumber - today.DayNumber + 1);

        bool tooEarly = gone < Earliest && gone.Ticks < period.Length.Ticks * EarliestShare;
        double pace = gone > TimeSpan.Zero ? counted / gone.TotalSeconds : 0;

        long? projected = tooEarly ? null : counted + (long)Math.Round(pace * left.TotalSeconds);
        long? budget = allowance > 0 ? Math.Max(0, allowance - counted) / daysLeft : null;
        DateTimeOffset? runsOut = null;

        if (!tooEarly && allowance > 0 && counted < allowance && pace > 0)
        {
            DateTimeOffset at = from.AddSeconds((allowance - counted) / pace);
            runsOut = at < period.End ? at : null;
        }

        return new PlanOutlook(projected, budget, daysLeft, left, runsOut, tooEarly, lastDay);
    }
}
