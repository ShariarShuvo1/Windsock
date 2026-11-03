using System.Globalization;
using Windsock.Core.History;

namespace Windsock.Core.Feed;

/// <summary>A period of history asked for by name or by dates, in local time.</summary>
/// <param name="From">Its start.</param>
/// <param name="Until">Its end, exclusive.</param>
/// <param name="Granularity">How finely it is broken up.</param>
public sealed record UsagePeriod(DateTimeOffset From, DateTimeOffset Until, UsageGranularity Granularity)
{
    /// <summary>The longest period broken into minutes.</summary>
    public static readonly TimeSpan LongestByMinute = TimeSpan.FromDays(2);

    /// <summary>The longest period broken into hours.</summary>
    public static readonly TimeSpan LongestByHour = TimeSpan.FromDays(93);

    /// <summary>The longest period at all.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromDays(400);

    /// <summary>The words a period may be named by.</summary>
    public static IReadOnlyList<string> Names { get; } = ["today", "yesterday", "week", "last-week", "month", "last-month", "year"];

    /// <summary>
    /// Reads a period from its parts, any of which may be missing: a name
    /// (<see cref="Names"/>), a month ("2026-09"), a day ("2026-09-14"), or a
    /// from/until pair of dates (until inclusive) or ISO times; and how to break
    /// it up ("minute", "hour" or "day"; chosen from its length when not given).
    /// The problem, in a sentence, when it cannot be read or is too long.
    /// </summary>
    public static UsagePeriod? Read(
        string? name,
        string? month,
        string? day,
        string? from,
        string? until,
        string? by,
        DateTimeOffset now,
        TimeZoneInfo zone,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(zone);

        // Calendar arithmetic at the ends of time - the month after 9999-12, the
        // offset of 0001-01-01 - throws rather than answering. A period nobody
        // could have recorded is refused before it is worked out, and anything
        // that still throws is a refusal too, never an exception to the caller.
        foreach (string? text in new[] { month is null ? null : month + "-01", day, from, until })
        {
            if (text is { Length: >= 4 }
                && int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int year)
                && (year < EarliestYear || year > LatestYear))
            {
                problem = string.Create(CultureInfo.InvariantCulture, $"Dates are from {EarliestYear} to {LatestYear}.");
                return null;
            }
        }

        try
        {
            UsagePeriod? period = ReadChecked(name, month, day, from, until, by, now, zone, out problem);

            // A time written some other way than year first gets past the
            // check above, so the period worked out is held to it too, with a
            // day's slack for the time zone.
            if (period is not null
                && (period.From.UtcDateTime.Year < EarliestYear - 1 || period.Until.UtcDateTime.Year > LatestYear + 1))
            {
                problem = string.Create(CultureInfo.InvariantCulture, $"Dates are from {EarliestYear} to {LatestYear}.");
                return null;
            }

            return period;
        }
        catch (ArgumentOutOfRangeException)
        {
            problem = "That period is outside the calendar Windsock can work with.";
            return null;
        }
    }

    /// <summary>The first year a period may name.</summary>
    public const int EarliestYear = 1970;

    /// <summary>The last year a period may name.</summary>
    public const int LatestYear = 9000;

    private static UsagePeriod? ReadChecked(
        string? name,
        string? month,
        string? day,
        string? from,
        string? until,
        string? by,
        DateTimeOffset now,
        TimeZoneInfo zone,
        out string? problem)
    {
        problem = null;
        DateOnly today = LocalCalendar.DateOf(now, zone);
        DateTimeOffset start;
        DateTimeOffset end;

        int given = (name is null ? 0 : 1) + (month is null ? 0 : 1) + (day is null ? 0 : 1) + (from is null && until is null ? 0 : 1);

        if (given > 1)
        {
            problem = "Give one of a period name, a month, a day, or from and until - not several.";
            return null;
        }

        if (month is not null)
        {
            if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly first))
            {
                problem = "A month is written like 2026-09.";
                return null;
            }

            start = LocalCalendar.StartOf(first, zone);
            end = LocalCalendar.StartOf(first.AddMonths(1), zone);
        }
        else if (day is not null)
        {
            if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            {
                problem = "A day is written like 2026-09-14.";
                return null;
            }

            start = LocalCalendar.StartOf(date, zone);
            end = LocalCalendar.StartOf(date.AddDays(1), zone);
        }
        else if (from is not null || until is not null)
        {
            if (from is null || until is null)
            {
                problem = "Give both from and until.";
                return null;
            }

            if (Moment(from, zone, endOfDay: false) is not { } first || Moment(until, zone, endOfDay: true) is not { } last)
            {
                problem = "From and until are dates like 2026-09-01, or times like 2026-09-01T08:00:00+06:00.";
                return null;
            }

            start = first;
            end = last;
        }
        else
        {
            switch ((name ?? "today").ToLowerInvariant())
            {
                case "today":
                    start = LocalCalendar.StartOf(today, zone);
                    end = LocalCalendar.StartOf(today.AddDays(1), zone);
                    break;

                case "yesterday":
                    start = LocalCalendar.StartOf(today.AddDays(-1), zone);
                    end = LocalCalendar.StartOf(today, zone);
                    break;

                case "week":
                    start = LocalCalendar.StartOf(LocalCalendar.WeekOf(today, DayOfWeek.Monday), zone);
                    end = LocalCalendar.StartOf(LocalCalendar.WeekOf(today, DayOfWeek.Monday).AddDays(7), zone);
                    break;

                case "last-week":
                    start = LocalCalendar.StartOf(LocalCalendar.WeekOf(today, DayOfWeek.Monday).AddDays(-7), zone);
                    end = LocalCalendar.StartOf(LocalCalendar.WeekOf(today, DayOfWeek.Monday), zone);
                    break;

                case "month":
                    start = LocalCalendar.StartOf(LocalCalendar.MonthOf(today), zone);
                    end = LocalCalendar.StartOf(LocalCalendar.MonthOf(today).AddMonths(1), zone);
                    break;

                case "last-month":
                    start = LocalCalendar.StartOf(LocalCalendar.MonthOf(today).AddMonths(-1), zone);
                    end = LocalCalendar.StartOf(LocalCalendar.MonthOf(today), zone);
                    break;

                case "year":
                    start = LocalCalendar.StartOf(new DateOnly(today.Year, 1, 1), zone);
                    end = LocalCalendar.StartOf(new DateOnly(today.Year + 1, 1, 1), zone);
                    break;

                default:
                    problem = "A period is one of: " + string.Join(", ", Names) + ".";
                    return null;
            }
        }

        if (end <= start)
        {
            problem = "The period must end after it starts.";
            return null;
        }

        TimeSpan length = end - start;

        if (length > Longest)
        {
            problem = "That period is longer than 400 days; ask for less at a time.";
            return null;
        }

        UsageGranularity granularity;

        switch (by?.ToLowerInvariant())
        {
            case null:
                granularity = length <= TimeSpan.FromDays(1.1) ? UsageGranularity.Hour : UsageGranularity.Day;
                break;

            case "minute":
                granularity = UsageGranularity.Minute;
                break;

            case "hour":
                granularity = UsageGranularity.Hour;
                break;

            case "day":
                granularity = UsageGranularity.Day;
                break;

            default:
                problem = "Break a period up by minute, hour or day.";
                return null;
        }

        if ((granularity == UsageGranularity.Minute && length > LongestByMinute)
            || (granularity == UsageGranularity.Hour && length > LongestByHour))
        {
            problem = granularity == UsageGranularity.Minute
                ? "Minutes are given for at most two days at a time."
                : "Hours are given for at most 93 days at a time.";
            return null;
        }

        return new UsagePeriod(start, end, granularity);
    }

    private static DateTimeOffset? Moment(string text, TimeZoneInfo zone, bool endOfDay)
    {
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
        {
            return LocalCalendar.StartOf(endOfDay ? date.AddDays(1) : date, zone);
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset moment)
            ? moment
            : null;
    }
}
