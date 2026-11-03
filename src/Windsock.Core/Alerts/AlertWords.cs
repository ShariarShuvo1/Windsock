using System.Globalization;
using Windsock.Core.Formatting;

namespace Windsock.Core.Alerts;

/// <summary>What the list of alerts says about each one, in plain English.</summary>
public static class AlertWords
{
    /// <summary>
    /// When an alert was raised: the time alone today, "Yesterday" and the time,
    /// else the weekday and date - as Windows writes times.
    /// </summary>
    public static string When(DateTimeOffset at, DateTimeOffset now)
    {
        DateTime local = at.LocalDateTime;
        DateTime today = now.LocalDateTime.Date;

        if (local.Date == today)
        {
            return ClockFormat.Time(local);
        }

        return local.Date == today.AddDays(-1)
            ? string.Concat("Yesterday ", ClockFormat.Time(local))
            : ClockFormat.Day(local);
    }

    /// <summary>
    /// What became of an alert, or nothing for one simply shown: held, shown
    /// later in a summary, or not shown and why.
    /// </summary>
    public static string Outcome(AlertRecord record, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);

        return record.Outcome switch
        {
            AlertOutcome.Held => "Held until quiet hours or the snooze end",
            AlertOutcome.Summarised => record.ShownAt is { } shown
                ? string.Concat("Held, then shown in the summary at ", When(shown, now))
                : "Held, then shown in a summary",
            AlertOutcome.NotShown => record.Detail.Length > 0
                ? string.Concat("Not shown. ", record.Detail)
                : "Not shown",
            _ => string.Empty,
        };
    }

    /// <summary>Whether an alert was forwarded, in a few words, or nothing when forwarding had no part in it.</summary>
    public static string Forwarding(AlertRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return record.Forward switch
        {
            ForwardOutcome.Waiting => "Being forwarded",
            ForwardOutcome.Sent => "Forwarded",
            ForwardOutcome.Failed => record.ForwardDetail.Length > 0
                ? string.Concat("Not forwarded: ", record.ForwardDetail)
                : "Not forwarded",
            _ => string.Empty,
        };
    }

    /// <summary>How many alerts, in words: "No alerts", "1 alert", "12 alerts".</summary>
    public static string Count(int count) => count switch
    {
        0 => "No alerts",
        1 => "1 alert",
        _ => string.Create(CultureInfo.CurrentCulture, $"{count:N0} alerts"),
    };

    /// <summary>The unread count for the sidebar's badge: empty for none, "99+" past 99.</summary>
    public static string Badge(int unread) => unread switch
    {
        <= 0 => string.Empty,
        > 99 => "99+",
        _ => unread.ToString(CultureInfo.CurrentCulture),
    };
}
