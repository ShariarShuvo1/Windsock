using Windsock.Core.Plans;

namespace Windsock.Core.Feed;

/// <summary>Turns the data plans feature's status into what the feed tells.</summary>
public static class FeedPlans
{
    /// <summary>The feed's view of a plan's cycle, or nothing for no plan.</summary>
    public static FeedPlan? From(PlanStatus? status)
    {
        if (status is null)
        {
            return null;
        }

        return new FeedPlan(
            status.Plan.Name,
            status.HasLimit ? status.Allowance : null,
            status.Used,
            status.Left,
            status.Fraction,
            status.Period.Start,
            status.Period.End);
    }
}
