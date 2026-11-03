using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.App.Controls;
using Windsock.Core.Plans;

namespace Windsock.App.ViewModels;

/// <summary>One network's part of a plan's cycle, for the split by network.</summary>
/// <param name="Name">What to call the network.</param>
/// <param name="Amount">What it counted against the plan, written out.</param>
/// <param name="Note">What it used in the free hours, or empty.</param>
/// <param name="Share">Its share of what the cycle counted, 0 to 1.</param>
public sealed record PlanNetworkRow(string Name, string Amount, string Note, double Share)
{
    /// <summary>The share, written out.</summary>
    public string ShareText => UsageText.Percent(Share);

    /// <summary>What a screen reader says for the row; a record's own would list its fields.</summary>
    public override string ToString() =>
        string.Join(", ", new[] { Name, Amount, ShareText, Note }.Where(part => part.Length > 0));
}

/// <summary>One earlier cycle of a plan.</summary>
/// <param name="Period">When it ran.</param>
/// <param name="Used">What it counted.</param>
/// <param name="Verdict">How it ended against its allowance.</param>
/// <param name="IsOver">Whether it went past its allowance.</param>
public sealed record EarlierCycleRow(string Period, string Used, string Verdict, bool IsOver)
{
    /// <summary>What a screen reader says for the row; a record's own would list its fields.</summary>
    public override string ToString() =>
        string.Join(", ", new[] { Period, Used, Verdict }.Where(part => part.Length > 0));
}

/// <summary>
/// A plan's cycle as the Data plan page shows it: headline figures, the bar,
/// the forecast, each day, and the split by network. Made afresh from each
/// status; nothing in it changes after.
/// </summary>
public sealed partial class PlanCardViewModel : ObservableObject
{
    public PlanCardViewModel(PlanStatus status, bool isActive, Func<long, string?> networkName, Func<string, string> keyName)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(networkName);
        ArgumentNullException.ThrowIfNull(keyName);

        Status = status;
        IsActive = isActive;
        DataPlan plan = status.Plan;
        PlanTally tally = status.Tally;
        PlanOutlook outlook = status.Outlook;

        Name = string.IsNullOrWhiteSpace(plan.Name) ? "Data plan" : plan.Name.Trim();
        NetworksText = plan.CoversEveryNetwork
            ? "Every network"
            : string.Join(", ", plan.Networks.Select(network => keyName(network.Key) is { Length: > 0 } known ? known : network.Name));
        CycleText = CycleOf(plan, status.Period);

        UsedText = UsageText.Bytes(status.Used);
        Fraction = status.Fraction ?? 0;
        Levels = plan.Levels;
        ProjectedFraction = status.HasLimit && outlook.Projected is { } projected && status.Allowance > 0
            ? (double)projected / status.Allowance
            : null;

        LeftText = status switch
        {
            { State: PlanState.NotStarted } => "Not started",
            { State: PlanState.Ended } => "Ended",
            { HasLimit: false } => UsedText + " used",
            { Over: > 0 } => "Over by " + UsageText.Bytes(status.Over),
            _ => UsageText.Bytes(status.Left ?? 0) + " left",
        };

        AllowanceText = !status.HasLimit
            ? "No allowance: this plan keeps count"
            : status.CarriedIn > 0
                ? string.Format(CultureInfo.CurrentCulture, "{0} used of {1} ({2} carried in)", UsedText, UsageText.Bytes(status.Allowance), UsageText.Bytes(status.CarriedIn))
                : string.Format(CultureInfo.CurrentCulture, "{0} used of {1}", UsedText, UsageText.Bytes(status.Allowance));

        PercentText = status.Fraction is { } share ? UsageText.Percent(share) + " used" : string.Empty;
        TimeLeftText = status.State == PlanState.Running ? PlanWords.TimeLeft(outlook) + " left" : string.Empty;

        // The plates' own figures, short enough for a narrow window: the label says what they are.
        (LeftLabel, LeftValue) = status switch
        {
            { State: PlanState.NotStarted } => ("STARTS", UsageText.Date(DateOnly.FromDateTime(status.Period.Start.LocalDateTime))),
            { State: PlanState.Ended } => ("USED", UsedText),
            { HasLimit: false } => ("USED", UsedText),
            { Over: > 0 } => ("OVER BY", UsageText.Bytes(status.Over)),
            _ => ("LEFT", UsageText.Bytes(status.Left ?? 0)),
        };

        TimeLeftValue = status.State == PlanState.Running ? PlanWords.TimeLeft(outlook) : "None";
        ResetsText = status.Plan.Cycle == PlanCycle.OneOff
            ? "Ends " + UsageText.When(status.Period.End)
            : "Starts again " + UsageText.Date(DateOnly.FromDateTime(status.Period.End.LocalDateTime));
        BudgetText = status.State == PlanState.Running && outlook.DailyBudget is { } budget
            ? UsageText.Bytes(budget)
            : string.Empty;
        BudgetNote = BudgetText.Length > 0 ? "a day, to stay within it" : status.HasLimit ? string.Empty : "Set an allowance for a budget";
        ShortText = PlanWords.Left(status) ?? LeftText;

        ForecastText = status.State switch
        {
            PlanState.NotStarted => string.Format(CultureInfo.CurrentCulture, "Starts {0}.", UsageText.When(status.Period.Start)),
            PlanState.Ended => string.Format(CultureInfo.CurrentCulture, "Ended {0}, with {1} used.", UsageText.When(status.Period.End), UsedText),
            _ when outlook.TooEarly => "Too early in the cycle to say where it is heading. A forecast appears once half a day, or a tenth of the cycle, has gone.",
            _ => PlanWords.Forecast(status) ?? string.Empty,
        };

        RunsOutText = outlook.RunsOutAt is { } runsOut
            ? string.Format(CultureInfo.CurrentCulture, "At this pace the allowance runs out {0}.", UsageText.When(runsOut))
            : string.Empty;

        OffPeakText = plan.OffPeak.Enabled
            ? string.Format(
                CultureInfo.CurrentCulture,
                "{0} in the free hours, {1} to {2}{3}",
                UsageText.Bytes(tally.OffPeak),
                UsageText.Time(new TimeOnly(plan.OffPeak.Start / 60, plan.OffPeak.Start % 60)),
                UsageText.Time(new TimeOnly(plan.OffPeak.End / 60, plan.OffPeak.End % 60)),
                status.OffPeakLeft is { } offLeft ? string.Format(CultureInfo.CurrentCulture, "; {0} of their own allowance left", UsageText.Bytes(offLeft)) : ", not counted")
            : string.Empty;

        RecordedText = RecordedNote(status);
        LocalText = tally.Local > 0
            ? string.Format(CultureInfo.CurrentCulture, "{0} stayed on your local network - a NAS, a TV - and is not counted.", UsageText.Bytes(tally.Local))
            : string.Empty;

        // Each day: what counted, what was free, and Windows' figure apart.
        Days = [.. tally.Days.Select(day => new ChartBar(
            day.Date.Day.ToString(CultureInfo.CurrentCulture),
            DayDetail(day),
            Math.Max(0, day.Counted - day.Estimated),
            day.OffPeak,
            day.Estimated,
            IsFuture: day.IsFuture))];

        DailyBudget = status.State == PlanState.Running ? outlook.DailyBudget : null;

        long counted = tally.Counted + tally.OffPeak;
        List<PlanNetworkRow> networks = [];

        foreach (PlanNetworkUse use in tally.Networks)
        {
            string name = use.NetworkId == 0 ? "Network not known" : networkName(use.NetworkId) ?? "A network since erased";
            networks.Add(new PlanNetworkRow(
                name,
                UsageText.Bytes(use.Counted),
                use.OffPeak > 0 ? UsageText.Bytes(use.OffPeak) + " free" : string.Empty,
                counted > 0 ? (double)(use.Counted + use.OffPeak) / counted : 0));
        }

        Networks = networks;
        LeftOutText = LeftOut(tally);
    }

    /// <summary>The status this was made from.</summary>
    public PlanStatus Status { get; }

    /// <summary>The plan's id.</summary>
    public string Id => Status.Plan.Id;

    /// <summary>Whether this is the plan the network in use falls under.</summary>
    public bool IsActive { get; }

    /// <summary>Whether it is the plan the page leads with.</summary>
    [ObservableProperty]
    public partial bool IsChosen { get; set; }

    public string Name { get; }

    /// <summary>The networks it covers, named.</summary>
    public string NetworksText { get; }

    /// <summary>How often it starts again, and the current cycle's dates.</summary>
    public string CycleText { get; }

    public string UsedText { get; }

    /// <summary>"41.0 GB left", "Over by 2.00 GB", "Ended".</summary>
    public string LeftText { get; }

    /// <summary>What was used of what, and anything carried in.</summary>
    public string AllowanceText { get; }

    public string PercentText { get; }

    /// <summary>"17 days left", "9 hours left".</summary>
    public string TimeLeftText { get; }

    /// <summary>What the first plate holds: "LEFT", "OVER BY", "USED".</summary>
    public string LeftLabel { get; }

    /// <summary>The first plate's figure, without its words: "41.0 GB".</summary>
    public string LeftValue { get; }

    /// <summary>The second plate's figure: "17 days", "9 hours".</summary>
    public string TimeLeftValue { get; }

    /// <summary>When the allowance starts again, or the plan ends.</summary>
    public string ResetsText { get; }

    /// <summary>What can be used each day left; empty without an allowance.</summary>
    public string BudgetText { get; }

    public string BudgetNote { get; }

    /// <summary>"41.0 GB left · 17 days": the line for a list or a tile.</summary>
    public string ShortText { get; }

    /// <summary>The forecast as a sentence.</summary>
    public string ForecastText { get; }

    /// <summary>When the allowance runs out at this pace, or empty.</summary>
    public string RunsOutText { get; }

    /// <summary>What the free hours held, or empty when the plan has none.</summary>
    public string OffPeakText { get; }

    /// <summary>A warning when Windsock missed part of the cycle, or empty.</summary>
    public string RecordedText { get; }

    /// <summary>What stayed on the local network and was left out, or empty.</summary>
    public string LocalText { get; }

    /// <summary>Used over allowance, for the bar.</summary>
    public double Fraction { get; }

    /// <summary>Where the forecast puts the cycle's end, for the bar.</summary>
    public double? ProjectedFraction { get; }

    /// <summary>The alert levels, for the bar's ticks.</summary>
    public IReadOnlyList<int> Levels { get; }

    /// <summary>Whether the plan has an allowance, so the bar means something.</summary>
    public bool HasLimit => Status.HasLimit;

    /// <summary>Whether the cycle is past its allowance.</summary>
    public bool IsOver => Status.Over > 0;

    /// <summary>Each day of the cycle.</summary>
    public IReadOnlyList<ChartBar> Days { get; }

    /// <summary>The daily budget, drawn across the days; null without one.</summary>
    public double? DailyBudget { get; }

    /// <summary>Each network's part.</summary>
    public IReadOnlyList<PlanNetworkRow> Networks { get; }

    /// <summary>Whether more than one network shares the cycle, so the split is worth showing.</summary>
    public bool HasNetworkSplit => Networks.Count > 1 || LeftOutText.Length > 0;

    /// <summary>What was on networks the plan does not cover, or empty.</summary>
    public string LeftOutText { get; }

    /// <summary>The plan in a sentence for a screen reader: what its tile shows.</summary>
    public string AutomationName => string.Join(". ", new[] { Name, ShortText, NetworksText, IsActive ? "The plan for the network in use" : string.Empty }.Where(part => part.Length > 0));

    /// <summary>
    /// What a screen reader says for the plan's place in the list, which is
    /// the item around its tile; without it the item was read by its type.
    /// </summary>
    public override string ToString() => AutomationName;

    private static string CycleOf(DataPlan plan, BillingPeriod period)
    {
        string dates = string.Format(
            CultureInfo.CurrentCulture,
            "{0} to {1}",
            UsageText.Date(DateOnly.FromDateTime(period.Start.LocalDateTime)),
            UsageText.Date(DateOnly.FromDateTime(period.End.AddMinutes(-1).LocalDateTime)));

        return plan.Cycle switch
        {
            PlanCycle.Weekly => string.Format(CultureInfo.CurrentCulture, "Weekly, from {0} · {1}", CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(plan.ResetWeekday), dates),
            PlanCycle.Daily => "Daily, from midnight",
            PlanCycle.OneOff => "One-off · " + dates,
            _ => string.Format(CultureInfo.CurrentCulture, "Monthly, from the {0} · {1}", PlanWords.Ordinal(Math.Clamp(plan.ResetDay, 1, 31)), dates),
        };
    }

    private static string DayDetail(PlanDay day)
    {
        if (day.IsFuture)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0}: still to come", UsageText.Day(day.Date));
        }

        List<string> parts = [string.Format(CultureInfo.CurrentCulture, "{0}: {1} counted", UsageText.Day(day.Date), UsageText.Bytes(day.Counted))];

        if (day.Estimated > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, "{0} of it counted by Windows while Windsock was closed", UsageText.Bytes(day.Estimated)));
        }

        if (day.OffPeak > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, "{0} in the free hours", UsageText.Bytes(day.OffPeak)));
        }

        return string.Join("; ", parts);
    }

    private static string RecordedNote(PlanStatus status)
    {
        if (status.State != PlanState.Running || status.RecordedShare >= 0.9 || status.Outlook.TooEarly)
        {
            return string.Empty;
        }

        string missing = status.Plan.CoversEveryNetwork && status.Plan.CountWindowsEstimates
            ? "What Windows counted while Windsock was closed is included."
            : "What Windows counted while Windsock was closed cannot be counted here, since Windows does not say which network it was on, so these figures may be low.";

        return string.Format(
            CultureInfo.CurrentCulture,
            "Windsock recorded {0} of this cycle so far; the rest is time it was closed. {1}",
            UsageText.Percent(status.RecordedShare),
            missing);
    }

    private static string LeftOut(PlanTally tally)
    {
        List<string> parts = [];

        if (tally.OtherNetworks > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, "{0} on networks this plan does not cover", UsageText.Bytes(tally.OtherNetworks)));
        }

        if (tally.UnknownNetwork > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, "{0} recorded without knowing the network", UsageText.Bytes(tally.UnknownNetwork)));
        }

        return parts.Count == 0 ? string.Empty : "Not counted: " + string.Join("; ", parts) + ".";
    }
}
