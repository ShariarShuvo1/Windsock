using System.Globalization;

namespace Windsock.Core.Connectivity;

/// <summary>
/// What to call an outage and its cause, in words somebody who has never heard
/// of a gateway can act on.
/// </summary>
public static class OutageWords
{
    /// <summary>A few words for the state, as a heading.</summary>
    public static string Headline(OutageCause cause) => cause switch
    {
        OutageCause.NoNetwork => "Not connected to a network",
        OutageCause.Router => "Router not answering",
        OutageCause.SignIn => "Sign-in required",
        _ => "Internet unreachable",
    };

    /// <summary>What it probably means and what to try, in a sentence or two.</summary>
    public static string Explain(OutageCause cause) => cause switch
    {
        OutageCause.NoNetwork => "This PC isn't connected to Wi-Fi or a cable.",
        OutageCause.Router => "Your router isn't answering. Check the Wi-Fi or the cable, or restart the router.",
        OutageCause.Provider => "Your router is answering, but the internet beyond it isn't. This is usually your internet provider.",
        OutageCause.SignIn => "This network wants you to sign in, usually on a page in your browser.",
        _ => "The internet isn't reachable. Windsock can't tell whether the router or the internet provider is at fault.",
    };

    /// <summary>Who or what was at fault, as a short label for a list.</summary>
    public static string Blame(OutageCause cause) => cause switch
    {
        OutageCause.NoNetwork => "No network",
        OutageCause.Router => "Router or Wi-Fi",
        OutageCause.Provider => "Internet provider",
        OutageCause.SignIn => "Sign-in",
        _ => "Not known",
    };

    /// <summary>
    /// A duration the way it is said aloud: "45 seconds", "7 minutes",
    /// "1 hour 12 minutes", "3 days 4 hours".
    /// </summary>
    public static string Spoken(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalSeconds < 60)
        {
            return Count((int)Math.Round(duration.TotalSeconds), "second");
        }

        if (duration.TotalMinutes < 60)
        {
            return Count((int)Math.Round(duration.TotalMinutes), "minute");
        }

        if (duration.TotalHours < 24)
        {
            int hours = (int)duration.TotalHours;
            int minutes = duration.Minutes;
            return minutes == 0 ? Count(hours, "hour") : string.Concat(Count(hours, "hour"), " ", Count(minutes, "minute"));
        }

        int days = (int)duration.TotalDays;
        int rest = duration.Hours;
        return rest == 0 ? Count(days, "day") : string.Concat(Count(days, "day"), " ", Count(rest, "hour"));
    }

    /// <summary>The same, shortened for a table: "45 s", "7 min", "1 h 12 min", "3 d 4 h".</summary>
    public static string Brief(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalSeconds < 60)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{(int)Math.Round(duration.TotalSeconds)} s");
        }

        if (duration.TotalMinutes < 60)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{(int)Math.Round(duration.TotalMinutes)} min");
        }

        if (duration.TotalHours < 24)
        {
            return duration.Minutes == 0
                ? string.Create(CultureInfo.CurrentCulture, $"{(int)duration.TotalHours} h")
                : string.Create(CultureInfo.CurrentCulture, $"{(int)duration.TotalHours} h {duration.Minutes} min");
        }

        return duration.Hours == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{(int)duration.TotalDays} d")
            : string.Create(CultureInfo.CurrentCulture, $"{(int)duration.TotalDays} d {duration.Hours} h");
    }

    /// <summary>
    /// When an outage happened, the way a list reads it: "Today, 21:04 – 21:11",
    /// "Yesterday, 08:15 – 08:16", "Tue 12 Sep, 21:04 – 21:11", or both dates when
    /// it ran past midnight. An outage still going ends in "now".
    /// </summary>
    public static string When(Outage outage, DateTimeOffset now, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(outage);

        culture ??= CultureInfo.CurrentCulture;

        DateTimeOffset start = outage.Start.ToLocalTime();
        DateTime today = now.ToLocalTime().Date;

        if (outage.End is not { } finished)
        {
            return string.Create(culture, $"{Day(start.Date, today, culture)}, {start:t} – now");
        }

        DateTimeOffset end = finished.ToLocalTime();

        return end.Date == start.Date
            ? string.Create(culture, $"{Day(start.Date, today, culture)}, {start:t} – {end:t}")
            : string.Create(culture, $"{Day(start.Date, today, culture)} {start:t} – {Day(end.Date, today, culture)} {end:t}");
    }

    /// <summary>
    /// A sentence totalling the outages in a period: "No outages in the last 7
    /// days." or "3 outages in the last 7 days, 14 minutes in all. The longest
    /// lasted 9 minutes."
    /// </summary>
    /// <param name="outages">The outages.</param>
    /// <param name="period">The period as it finishes "in the ...", such as "the last 7 days".</param>
    /// <param name="now">The moment an outage still going is counted up to.</param>
    public static string Summary(IReadOnlyCollection<Outage> outages, string period, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(outages);

        if (outages.Count == 0)
        {
            return string.Concat("No outages in ", period, ".");
        }

        TimeSpan total = TimeSpan.Zero;
        TimeSpan longest = TimeSpan.Zero;

        foreach (Outage outage in outages)
        {
            TimeSpan lasted = (outage.End ?? now) - outage.Start;

            if (lasted > TimeSpan.Zero)
            {
                total += lasted;
                longest = lasted > longest ? lasted : longest;
            }
        }

        string counted = string.Create(
            CultureInfo.CurrentCulture,
            $"{outages.Count} {(outages.Count == 1 ? "outage" : "outages")} in {period}, {Spoken(total)} in all.");

        return outages.Count == 1
            ? counted
            : string.Concat(counted, " The longest lasted ", Spoken(longest), ".");
    }

    private static string Day(DateTime day, DateTime today, IFormatProvider culture) =>
        day == today ? "Today"
        : day == today.AddDays(-1) ? "Yesterday"
        : day.ToString("ddd d MMM", culture);

    private static string Count(int value, string unit) =>
        string.Create(CultureInfo.CurrentCulture, $"{value} {unit}{(value == 1 ? string.Empty : "s")}");
}
