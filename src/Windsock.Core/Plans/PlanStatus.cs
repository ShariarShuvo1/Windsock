namespace Windsock.Core.Plans;

/// <summary>Where a plan's cycle stands against the present.</summary>
public enum PlanState
{
    /// <summary>The present is inside the cycle.</summary>
    Running,

    /// <summary>A one-off plan that has not begun.</summary>
    NotStarted,

    /// <summary>A one-off plan that is over.</summary>
    Ended,
}

/// <summary>
/// A plan's cycle as it stands: what was used, what is left, and where it is heading.
/// </summary>
/// <param name="Plan">The plan, as it was when this was worked out.</param>
/// <param name="Period">The cycle.</param>
/// <param name="State">Whether the cycle is under way.</param>
/// <param name="At">When this was worked out.</param>
/// <param name="CarriedIn">Allowance carried in from earlier cycles.</param>
/// <param name="Tally">What the cycle has counted so far.</param>
/// <param name="Outlook">Where it is heading.</param>
public sealed record PlanStatus(
    DataPlan Plan,
    BillingPeriod Period,
    PlanState State,
    DateTimeOffset At,
    long CarriedIn,
    PlanTally Tally,
    PlanOutlook Outlook)
{
    /// <summary>The cycle's own allowance; zero for a plan that only keeps count.</summary>
    public long Limit => Math.Max(0, Plan.LimitBytes);

    /// <summary>Whether the plan has an allowance at all.</summary>
    public bool HasLimit => Limit > 0;

    /// <summary>The allowance with what was carried in; zero for a plan without one.</summary>
    public long Allowance => HasLimit ? Limit + CarriedIn : 0;

    /// <summary>What counts against the allowance so far.</summary>
    public long Used => Tally.Counted;

    /// <summary>What is left, never below zero; null for a plan without an allowance.</summary>
    public long? Left => HasLimit ? Math.Max(0, Allowance - Used) : null;

    /// <summary>How far past the allowance the cycle is; zero within it.</summary>
    public long Over => HasLimit ? Math.Max(0, Used - Allowance) : 0;

    /// <summary>The share of the allowance used, which may pass 1; null for a plan without one.</summary>
    public double? Fraction => HasLimit ? (double)Used / Allowance : null;

    /// <summary>What was used in the free hours.</summary>
    public long OffPeakUsed => Tally.OffPeak;

    /// <summary>What is left of the free hours' own allowance; null when they are simply free.</summary>
    public long? OffPeakLeft => Plan.OffPeak.Enabled && Plan.OffPeak.LimitBytes > 0
        ? Math.Max(0, Plan.OffPeak.LimitBytes - Tally.OffPeak)
        : null;

    /// <summary>
    /// The share of the cycle so far that Windsock recorded, from 0 to 1; low
    /// when it was closed for a stretch, which makes the figures low too.
    /// </summary>
    public double RecordedShare
    {
        get
        {
            DateTimeOffset end = At < Period.End ? At : Period.End;
            double gone = (end - Period.Start).TotalMinutes;
            return gone <= 0 ? 0 : Math.Clamp(Tally.RecordedMinutes / gone, 0, 1);
        }
    }

    /// <summary>The highest of the plan's levels the cycle has reached, or null for none.</summary>
    public int? LevelReached
    {
        get
        {
            int? reached = null;

            foreach (int level in Plan.Levels)
            {
                if (Reached(level))
                {
                    reached = level;
                }
            }

            return reached;
        }
    }

    /// <summary>Whether the cycle has used a given percentage of its allowance.</summary>
    public bool Reached(int level) =>
        HasLimit && State == PlanState.Running && (decimal)Used * 100 >= (decimal)level * Allowance;
}

/// <summary>A plan's cycle passing one of its levels.</summary>
/// <param name="Plan">The plan.</param>
/// <param name="Level">The level, in percent of the allowance.</param>
/// <param name="Status">The cycle as it stood when the crossing was seen.</param>
/// <param name="FirstLook">
/// Whether the level had already been passed the first time this run looked at
/// the cycle: crossed while Windsock was closed, or before the plan was made.
/// An alert may word that differently, or keep quiet about a plan just made.
/// </param>
public sealed record PlanLevelCrossing(DataPlan Plan, int Level, PlanStatus Status, bool FirstLook);
