using System.Globalization;
using Windsock.Core.Formatting;
using Windsock.Core.History;

namespace Windsock.Core.Reports;

/// <summary>What kind of stretch a report covers.</summary>
public enum PeriodKind
{
    Day,

    Week,

    Month,

    Year,

    /// <summary>Any two moments.</summary>
    Custom,
}

/// <summary>
/// A stretch of history on the local clock: a day, a week, a month, a year, or
/// any two moments.
/// </summary>
/// <param name="Kind">What kind of stretch it is.</param>
/// <param name="From">When it starts.</param>
/// <param name="Until">When it ends. Exclusive.</param>
/// <param name="First">Its first local date.</param>
/// <param name="FirstDayOfWeek">The day weeks start on, for a week and for stepping by weeks.</param>
public sealed record ReportPeriod(PeriodKind Kind, DateTimeOffset From, DateTimeOffset Until, DateOnly First, DayOfWeek FirstDayOfWeek)
{
    /// <summary>A local day.</summary>
    public static ReportPeriod Day(DateOnly date, TimeZoneInfo zone) =>
        new(PeriodKind.Day, LocalCalendar.StartOf(date, zone), LocalCalendar.StartOf(date.AddDays(1), zone), date, DayOfWeek.Monday);

    /// <summary>The local week holding a date.</summary>
    public static ReportPeriod Week(DateOnly date, DayOfWeek firstDay, TimeZoneInfo zone)
    {
        DateOnly first = LocalCalendar.WeekOf(date, firstDay);
        return new(PeriodKind.Week, LocalCalendar.StartOf(first, zone), LocalCalendar.StartOf(first.AddDays(7), zone), first, firstDay);
    }

    /// <summary>A local month.</summary>
    public static ReportPeriod Month(int year, int month, TimeZoneInfo zone)
    {
        DateOnly first = new(year, month, 1);
        return new(PeriodKind.Month, LocalCalendar.StartOf(first, zone), LocalCalendar.StartOf(first.AddMonths(1), zone), first, DayOfWeek.Monday);
    }

    /// <summary>A local year.</summary>
    public static ReportPeriod Year(int year, TimeZoneInfo zone)
    {
        DateOnly first = new(year, 1, 1);
        return new(PeriodKind.Year, LocalCalendar.StartOf(first, zone), LocalCalendar.StartOf(first.AddYears(1), zone), first, DayOfWeek.Monday);
    }

    /// <summary>Any stretch.</summary>
    public static ReportPeriod Between(DateTimeOffset from, DateTimeOffset until, TimeZoneInfo zone)
    {
        if (until <= from)
        {
            throw new ArgumentException("A period must end after it starts.", nameof(until));
        }

        return new(PeriodKind.Custom, from, until, LocalCalendar.DateOf(from, zone), DayOfWeek.Monday);
    }

    /// <summary>The same kind of stretch holding a moment: today, this week, this month, this year.</summary>
    public static ReportPeriod Holding(PeriodKind kind, DateTimeOffset moment, TimeZoneInfo zone, DayOfWeek firstDay)
    {
        DateOnly date = LocalCalendar.DateOf(moment, zone);

        return kind switch
        {
            PeriodKind.Day => Day(date, zone),
            PeriodKind.Week => Week(date, firstDay, zone),
            PeriodKind.Month => Month(date.Year, date.Month, zone),
            PeriodKind.Year => Year(date.Year, zone),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A custom period has no stretch of its own."),
        };
    }

    /// <summary>How long it lasts.</summary>
    public TimeSpan Length => Until - From;

    /// <summary>The epoch minute it starts at.</summary>
    public long FromMinute => EpochMinute.From(From);

    /// <summary>The epoch minute it ends at. Exclusive.</summary>
    public long UntilMinute => EpochMinute.From(Until);

    /// <summary>The stretch before: yesterday, last week, last month, last year, or as long again before.</summary>
    public ReportPeriod Previous(TimeZoneInfo zone) => Kind switch
    {
        PeriodKind.Day => Day(First.AddDays(-1), zone),
        PeriodKind.Week => Week(First.AddDays(-7), FirstDayOfWeek, zone),
        PeriodKind.Month => Month(First.AddMonths(-1).Year, First.AddMonths(-1).Month, zone),
        PeriodKind.Year => Year(First.Year - 1, zone),
        _ => Between(From - Length, From, zone),
    };

    /// <summary>The stretch after.</summary>
    public ReportPeriod Next(TimeZoneInfo zone) => Kind switch
    {
        PeriodKind.Day => Day(First.AddDays(1), zone),
        PeriodKind.Week => Week(First.AddDays(7), FirstDayOfWeek, zone),
        PeriodKind.Month => Month(First.AddMonths(1).Year, First.AddMonths(1).Month, zone),
        PeriodKind.Year => Year(First.Year + 1, zone),
        _ => Between(Until, Until + Length, zone),
    };

    /// <summary>
    /// The same stretch a year before: this month last year, or the week of the
    /// same number last year (the week holding the same date a year back).
    /// </summary>
    public ReportPeriod SameLastYear(TimeZoneInfo zone) => Kind switch
    {
        PeriodKind.Day => Day(First.AddYears(-1), zone),
        PeriodKind.Week => Week(First.AddYears(-1), FirstDayOfWeek, zone),
        PeriodKind.Month => Month(First.Year - 1, First.Month, zone),
        PeriodKind.Year => Year(First.Year - 1, zone),
        _ => Between(From.AddYears(-1), Until.AddYears(-1), zone),
    };

    /// <summary>The steps a chart of the period is drawn in: hours for a day, days for a week or month, months for a year.</summary>
    public CalendarStep Step => Kind switch
    {
        PeriodKind.Day => CalendarStep.Hour,
        PeriodKind.Year => CalendarStep.Month,
        PeriodKind.Custom when Length <= TimeSpan.FromDays(2) => CalendarStep.Hour,
        PeriodKind.Custom when Length > TimeSpan.FromDays(120) => CalendarStep.Month,
        _ => CalendarStep.Day,
    };

    /// <summary>A short name for the period: "5 October 2026", "Week of 29 September", "October 2026", "2026".</summary>
    public string Describe(CultureInfo? culture = null)
    {
        CultureInfo format = culture ?? CultureInfo.CurrentCulture;
        ClockPatterns clock = culture is null ? ClockFormat.Current : new ClockPatterns(culture.DateTimeFormat);

        return Kind switch
        {
            PeriodKind.Day => First.ToString("d MMMM yyyy", format),
            PeriodKind.Week => "Week of " + First.ToString("d MMMM yyyy", format),
            PeriodKind.Month => First.ToString("MMMM yyyy", format),
            PeriodKind.Year => First.Year.ToString(format),
            _ => string.Concat(From.ToString("d MMM yyyy", format), ", ", clock.Time(From), " to ", Until.ToString("d MMM yyyy", format), ", ", clock.Time(Until)),
        };
    }
}
