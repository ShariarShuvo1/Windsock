using Windsock.Core.History;

namespace Windsock.Core.Plans;

/// <summary>One cycle of a plan: from when its allowance starts to when it starts again.</summary>
/// <param name="Start">When it starts.</param>
/// <param name="End">When the next starts. Exclusive.</param>
public readonly record struct BillingPeriod(DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>How long it lasts.</summary>
    public TimeSpan Length => End - Start;

    /// <summary>The epoch minute it starts at; what its crossings are kept under.</summary>
    public long StartMinute => EpochMinute.From(Start);

    /// <summary>The epoch minute it ends at. Exclusive.</summary>
    public long EndMinute => EpochMinute.From(End);

    /// <summary>Whether a moment falls inside it.</summary>
    public bool Contains(DateTimeOffset moment) => moment >= Start && moment < End;
}

/// <summary>
/// Works out a plan's cycles on the local clock.
/// </summary>
/// <remarks>
/// A cycle starts at local midnight of its reset day (see
/// <see cref="LocalCalendar.At"/> for a midnight the clocks skip or repeat). A
/// monthly plan resetting on the 29th, 30th or 31st starts again on a short
/// month's last day, and goes back to its own day the month after: the 31st,
/// then 28 February (29th in a leap year), then 31 March.
/// </remarks>
public static class PlanCycles
{
    /// <summary>
    /// The cycle holding a moment. For a one-off plan it is the plan's own
    /// stretch whatever the moment, or null when the plan has none.
    /// </summary>
    public static BillingPeriod? Containing(DataPlan plan, DateTimeOffset moment, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(zone);

        DateOnly today = LocalCalendar.DateOf(moment, zone);

        switch (plan.Cycle)
        {
            case PlanCycle.OneOff:
                return plan.OneOffStart is { } start && plan.OneOffEnd is { } end && end > start
                    ? new BillingPeriod(start, end)
                    : null;

            case PlanCycle.Daily:
            {
                DateTimeOffset begins = LocalCalendar.StartOf(today, zone);
                DateOnly day = moment < begins ? today.AddDays(-1) : today;
                return new BillingPeriod(LocalCalendar.StartOf(day, zone), LocalCalendar.StartOf(day.AddDays(1), zone));
            }

            case PlanCycle.Weekly:
            {
                DateOnly first = LocalCalendar.WeekOf(today, plan.ResetWeekday);

                if (moment < LocalCalendar.StartOf(first, zone))
                {
                    first = first.AddDays(-7);
                }

                return new BillingPeriod(LocalCalendar.StartOf(first, zone), LocalCalendar.StartOf(first.AddDays(7), zone));
            }

            default:
            {
                int day = Math.Clamp(plan.ResetDay, 1, 31);
                DateOnly reset = ResetIn(today.Year, today.Month, day);

                if (moment < LocalCalendar.StartOf(reset, zone))
                {
                    DateOnly before = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
                    reset = ResetIn(before.Year, before.Month, day);
                }

                DateOnly following = new DateOnly(reset.Year, reset.Month, 1).AddMonths(1);
                return new BillingPeriod(
                    LocalCalendar.StartOf(reset, zone),
                    LocalCalendar.StartOf(ResetIn(following.Year, following.Month, day), zone));
            }
        }
    }

    /// <summary>The cycle before one, or null for a one-off plan, which has only the one.</summary>
    public static BillingPeriod? Before(DataPlan plan, BillingPeriod period, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Cycle == PlanCycle.OneOff ? null : Containing(plan, period.Start.AddMinutes(-1), zone);
    }

    /// <summary>The cycle after one, or null for a one-off plan.</summary>
    public static BillingPeriod? After(DataPlan plan, BillingPeriod period, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Cycle == PlanCycle.OneOff ? null : Containing(plan, period.End, zone);
    }

    /// <summary>
    /// The cycles from the one holding the plan's start to the one holding a
    /// moment, oldest first, at most <paramref name="most"/> of the latest.
    /// A plan with no start has only the cycle holding the moment.
    /// </summary>
    public static IReadOnlyList<BillingPeriod> Through(DataPlan plan, DateTimeOffset moment, TimeZoneInfo zone, int most = 400)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (Containing(plan, moment, zone) is not { } current)
        {
            return [];
        }

        List<BillingPeriod> periods = [current];

        if (plan.Since is { } since && plan.Cycle != PlanCycle.OneOff)
        {
            BillingPeriod each = current;

            while (periods.Count < most && each.Start > since && Before(plan, each, zone) is { } earlier && earlier.End > since)
            {
                periods.Add(earlier);
                each = earlier;
            }
        }

        periods.Reverse();
        return periods;
    }

    /// <summary>The reset date in a month: the plan's day, or the month's last when it is shorter.</summary>
    public static DateOnly ResetIn(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
}
