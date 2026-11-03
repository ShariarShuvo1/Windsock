using System.Globalization;
using Windsock.Core.Formatting;

namespace Windsock.Core.Plans;

/// <summary>A plan's status in a few words, for the taskbar, a tooltip or a notice.</summary>
public static class PlanWords
{
    /// <summary>
    /// What is left and for how long: "41 GB left · 17 days", "Over by 2.1 GB ·
    /// 3 days", "12.4 GB used · 9 hours" for a plan without an allowance; null
    /// when the cycle is not under way.
    /// </summary>
    public static string? Left(PlanStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (status.State != PlanState.Running)
        {
            return null;
        }

        string amount = status switch
        {
            { HasLimit: false } => SizeFormatter.Format(status.Used) + " used",
            { Over: > 0 } => "Over by " + SizeFormatter.Format(status.Over),
            _ => SizeFormatter.Format(status.Left ?? 0) + " left",
        };

        return string.Concat(amount, " · ", TimeLeft(status.Outlook));
    }

    /// <summary>
    /// Time left in a cycle: whole days, today counted, or hours and minutes
    /// on its last day.
    /// </summary>
    public static string TimeLeft(PlanOutlook outlook)
    {
        ArgumentNullException.ThrowIfNull(outlook);

        if (outlook.TimeLeft >= TimeSpan.FromDays(1) || outlook.DaysLeft > 1)
        {
            return outlook.DaysLeft == 1 ? "1 day" : string.Create(CultureInfo.CurrentCulture, $"{outlook.DaysLeft} days");
        }

        int hours = (int)outlook.TimeLeft.TotalHours;

        return hours >= 1
            ? (hours == 1 ? "1 hour" : string.Create(CultureInfo.CurrentCulture, $"{hours} hours"))
            : string.Create(CultureInfo.CurrentCulture, $"{Math.Max(1, (int)Math.Ceiling(outlook.TimeLeft.TotalMinutes))} min");
    }

    /// <summary>
    /// The forecast as a sentence: "At this pace, 312 GB by the 30th. You can
    /// use 3.4 GB a day and stay under." Null while it is too early to say.
    /// </summary>
    public static string? Forecast(PlanStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        PlanOutlook outlook = status.Outlook;

        if (status.State != PlanState.Running || outlook.Projected is not { } projected)
        {
            return null;
        }

        string pace = string.Create(
            CultureInfo.CurrentCulture,
            $"At this pace, {SizeFormatter.Format(projected)} by the {Ordinal(outlook.LastDay.Day)}.");

        if (!status.HasLimit || outlook.DailyBudget is not { } budget)
        {
            return pace;
        }

        if (status.Over > 0)
        {
            return string.Concat(pace, " The allowance is already used up.");
        }

        return string.Concat(pace, " You can use ", SizeFormatter.Format(budget), " a day and stay under.");
    }

    /// <summary>A day of the month as an ordinal: 1st, 2nd, 3rd, 11th, 22nd, 30th.</summary>
    public static string Ordinal(int day)
    {
        string suffix = (day % 100) is 11 or 12 or 13
            ? "th"
            : (day % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th",
            };

        return string.Concat(day.ToString(CultureInfo.InvariantCulture), suffix);
    }
}
