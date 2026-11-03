using System.Globalization;
using System.Text;
using Windsock.Core.Formatting;
using Windsock.Core.Notifications;

namespace Windsock.Core.Alerts;

/// <summary>The one notice that hands over what quiet hours or a snooze held.</summary>
public static class QuietSummary
{
    /// <summary>The section a click on it opens: the list of alerts.</summary>
    public const string Opens = "alerts";

    /// <summary>How many held alerts are named; the rest are counted.</summary>
    public const int Named = 3;

    /// <summary>
    /// The summary of held alerts, or null for none. The most serious are named
    /// first, each with the time it was raised, so "Internet connection lost"
    /// at 02:14 is not read as news at 07:00.
    /// </summary>
    public static Notice? Compose(IReadOnlyList<AlertRecord> held, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(zone);

        if (held.Count == 0)
        {
            return null;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        string title = held.Count == 1
            ? "1 alert while notifications were held"
            : string.Create(culture, $"{held.Count} alerts while notifications were held");

        // When more than fit, the most serious are named; those named are told in time order.
        IEnumerable<AlertRecord> named = held.Count <= Named
            ? held
            : held.OrderByDescending(each => each.Level).ThenBy(each => each.At).Take(Named - 1);

        StringBuilder message = new();

        foreach (AlertRecord each in named.OrderBy(each => each.At))
        {
            if (message.Length > 0)
            {
                message.Append('\n');
            }

            message.Append(ClockFormat.Time(TimeZoneInfo.ConvertTime(each.At, zone))).Append(' ').Append(each.Title);
        }

        int rest = held.Count <= Named ? 0 : held.Count - (Named - 1);

        if (rest > 0)
        {
            message.Append('\n').Append(culture, $"and {rest} more");
        }

        return new Notice(NoticeTopics.QuietSummary, title, message.ToString())
        {
            Tag = NoticeTopics.QuietSummary,
            Opens = Opens,
        };
    }
}
