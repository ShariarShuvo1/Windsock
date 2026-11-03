using Windsock.Core.Connectivity;
using Windsock.Core.History;

namespace Windsock.Core.Reports;

/// <summary>
/// Totals over some stretch: what Windsock recorded, and apart from it what
/// Windows counted while Windsock was closed.
/// </summary>
/// <param name="Down">Bytes received, as Windsock recorded them.</param>
/// <param name="Up">Bytes sent, as Windsock recorded them.</param>
/// <param name="EstimatedDown">Bytes received in time Windsock was closed, as Windows counted them.</param>
/// <param name="EstimatedUp">Bytes sent in that time, as Windows counted them.</param>
/// <param name="RecordedMinutes">Minutes Windsock recorded.</param>
/// <param name="EstimatedMinutes">Minutes Windows' figures cover.</param>
/// <param name="LocalDown">
/// The part of <paramref name="Down"/> known to have stayed on the local
/// network; zero where that is not known.
/// </param>
/// <param name="LocalUp">The part of <paramref name="Up"/> known to have stayed on the local network.</param>
public readonly record struct UsageTotals(
    long Down,
    long Up,
    long EstimatedDown,
    long EstimatedUp,
    long RecordedMinutes,
    long EstimatedMinutes,
    long LocalDown = 0,
    long LocalUp = 0)
{
    /// <summary>Nothing at all.</summary>
    public static UsageTotals Empty => default;

    /// <summary>What Windsock recorded, both directions.</summary>
    public long Recorded => Down + Up;

    /// <summary>What Windows counted while Windsock was closed, both directions.</summary>
    public long Estimated => EstimatedDown + EstimatedUp;

    /// <summary>Everything received, whoever counted it.</summary>
    public long AllDown => Down + EstimatedDown;

    /// <summary>Everything sent, whoever counted it.</summary>
    public long AllUp => Up + EstimatedUp;

    /// <summary>Everything, whoever counted it.</summary>
    public long All => Recorded + Estimated;

    /// <summary>Whether anything at all is known for the stretch.</summary>
    public bool HasData => RecordedMinutes > 0 || EstimatedMinutes > 0;

    /// <summary>What is known to have stayed on the local network.</summary>
    public long Local => LocalDown + LocalUp;

    /// <summary>
    /// Everything less what is known to have stayed on the local network: the
    /// internet's part, which is everything where the local share is not known.
    /// </summary>
    public long Internet => Math.Max(0, All - Local);

    /// <summary>The figure a report ranks and compares by: everything, or the internet's part.</summary>
    public long Measure(bool internetOnly) => internetOnly ? Internet : All;

    /// <summary>Both added together.</summary>
    public static UsageTotals operator +(UsageTotals left, UsageTotals right) => new(
        left.Down + right.Down,
        left.Up + right.Up,
        left.EstimatedDown + right.EstimatedDown,
        left.EstimatedUp + right.EstimatedUp,
        left.RecordedMinutes + right.RecordedMinutes,
        left.EstimatedMinutes + right.EstimatedMinutes,
        left.LocalDown + right.LocalDown,
        left.LocalUp + right.LocalUp);

    /// <summary>Both added together.</summary>
    public static UsageTotals Add(UsageTotals left, UsageTotals right) => left + right;
}

/// <summary>One bucket of a report's chart.</summary>
public readonly record struct ReportPoint(DateTimeOffset Start, UsageTotals Totals);

/// <summary>The fastest second of a stretch.</summary>
public readonly record struct PeakRate(DateTimeOffset At, long BytesPerSecond);

/// <summary>What one network carried in a stretch.</summary>
/// <param name="NetworkId">Its row in history; 0 for minutes whose network was not known.</param>
/// <param name="Key">Its stable id, or null when not known.</param>
/// <param name="Name">What Windows called it, or null when not known.</param>
/// <param name="Kind">The kind of link, or null when not known.</param>
/// <param name="Down">Bytes received on it.</param>
/// <param name="Up">Bytes sent on it.</param>
/// <param name="Minutes">Minutes recorded on it.</param>
public readonly record struct NetworkShare(long NetworkId, string? Key, string? Name, NetworkKind? Kind, long Down, long Up, long Minutes)
{
    /// <summary>Both directions.</summary>
    public long Total => Down + Up;

    /// <summary>Whether the network is known.</summary>
    public bool IsKnown => NetworkId != 0;
}

/// <summary>Outages in a stretch, as far as they lay inside it.</summary>
public readonly record struct OutageSummary(int Count, TimeSpan Offline);

/// <summary>How a stretch compares with the one before.</summary>
/// <param name="Before">The stretch before's totals.</param>
/// <param name="Difference">What moved now less what moved then (everything, or the internet's part; see <see cref="UsageTotals.Measure"/>).</param>
/// <param name="Percent">The difference as a share of before, or null when nothing was known before.</param>
/// <param name="SameElapsed">
/// Whether the stretch before was cut to as much of it as has gone of this
/// one, so a month under way is set against the same days of the month before.
/// </param>
public sealed record PeriodChange(UsageTotals Before, long Difference, double? Percent, bool SameElapsed)
{
    /// <summary>The change between two totals.</summary>
    public static PeriodChange Between(UsageTotals before, UsageTotals now, bool sameElapsed, bool internetOnly = false)
    {
        long then = before.Measure(internetOnly);
        long difference = now.Measure(internetOnly) - then;
        return new(before, difference, then > 0 ? (double)difference / then : null, sameElapsed);
    }
}

/// <summary>A report on a day, week, month, year or any stretch.</summary>
/// <param name="Period">The stretch.</param>
/// <param name="At">When the report was made; a stretch still going is reported up to here.</param>
/// <param name="Totals">Everything in it.</param>
/// <param name="Elapsed">How much of the stretch has gone.</param>
/// <param name="PeakDown">The fastest second in, or null when nothing was recorded.</param>
/// <param name="PeakUp">The fastest second out, or null.</param>
/// <param name="BusiestDay">The day that moved most, for a stretch longer than a day.</param>
/// <param name="BusiestHour">The clock hour that moved most.</param>
/// <param name="Series">The stretch in steps (see <see cref="ReportPeriod.Step"/>), every step included.</param>
/// <param name="HourOfDay">Everything moved in each hour of the day, 0 to 23, across the stretch.</param>
/// <param name="DayOfWeek">Everything moved on each day of the week, by <see cref="System.DayOfWeek"/>.</param>
/// <param name="Networks">Each network's part, busiest first; unknown minutes have id 0.</param>
/// <param name="TopApps">The busiest apps.</param>
/// <param name="Outages">Outages in the stretch.</param>
/// <param name="Change">How it compares with the stretch before, or null when that was not asked for.</param>
/// <param name="InternetOnly">
/// Whether busiest times, the hour and day profiles and the change were worked
/// out on internet bytes alone, leaving out what is known to have stayed on the
/// local network; the totals hold both either way.
/// </param>
public sealed record PeriodReport(
    ReportPeriod Period,
    DateTimeOffset At,
    UsageTotals Totals,
    TimeSpan Elapsed,
    PeakRate? PeakDown,
    PeakRate? PeakUp,
    ReportPoint? BusiestDay,
    ReportPoint? BusiestHour,
    IReadOnlyList<ReportPoint> Series,
    IReadOnlyList<long> HourOfDay,
    IReadOnlyList<long> DayOfWeek,
    IReadOnlyList<NetworkShare> Networks,
    IReadOnlyList<AppShare> TopApps,
    OutageSummary Outages,
    PeriodChange? Change,
    bool InternetOnly = false)
{
    /// <summary>Whether the stretch is still going.</summary>
    public bool IsUnderWay => At < Period.Until;

    /// <summary>Everything moved, per day of the stretch gone so far.</summary>
    public long DailyAverage
    {
        get
        {
            double days = Elapsed.TotalDays;
            long value = Totals.Measure(InternetOnly);
            return days < 1 ? value : (long)(value / days);
        }
    }
}

/// <summary>One app's change between two stretches.</summary>
public readonly record struct AppChange(string AttributionId, long Before, long After)
{
    /// <summary>After less before.</summary>
    public long Difference => After - Before;
}

/// <summary>Two stretches side by side, with the difference stated.</summary>
/// <param name="Baseline">The stretch compared against: last month, the same week last year.</param>
/// <param name="Compared">The stretch being looked at.</param>
/// <param name="Difference">Everything moved in <paramref name="Compared"/> less in <paramref name="Baseline"/>.</param>
/// <param name="Percent">The difference as a share of the baseline, or null when the baseline holds nothing.</param>
/// <param name="Aligned">
/// Both charts step by step from their starts - day 1 against day 1 - as long
/// as the longer has steps; a step one of them does not have is null.
/// </param>
/// <param name="Apps">Each app's change, the largest either way first.</param>
/// <param name="SameElapsed">
/// Whether <paramref name="Compared"/> is still under way, so the difference,
/// the percentage and the apps' changes are against the same length of time
/// from the baseline's start, not the whole baseline.
/// </param>
public sealed record PeriodComparison(
    PeriodReport Baseline,
    PeriodReport Compared,
    long Difference,
    double? Percent,
    IReadOnlyList<(ReportPoint? Baseline, ReportPoint? Compared)> Aligned,
    IReadOnlyList<AppChange> Apps,
    bool SameElapsed = false);

/// <summary>One day of a year's calendar.</summary>
/// <param name="Date">The date.</param>
/// <param name="Totals">What it moved.</param>
/// <param name="Shade">How dark to draw it, 0 for nothing to 4 for the busiest days, by quartiles of the days that moved anything.</param>
public readonly record struct CalendarDay(DateOnly Date, UsageTotals Totals, int Shade);

/// <summary>Every day of a year, shaded by how much it used.</summary>
public sealed record YearCalendar(int Year, IReadOnlyList<CalendarDay> Days, UsageTotals Totals, CalendarDay? Busiest);

/// <summary>
/// When in the week the network is busiest: seven rows of 24 hours.
/// </summary>
/// <param name="From">Where the stretch looked at starts.</param>
/// <param name="Until">Where it ends.</param>
/// <param name="Totals">Everything moved in each hour of the week, [day of week][hour].</param>
/// <param name="Hours">How many such hours had anything recorded, [day of week][hour].</param>
public sealed record WeekHeat(DateTimeOffset From, DateTimeOffset Until, long[][] Totals, int[][] Hours)
{
    /// <summary>The average an hour moved, over the hours with anything recorded; 0 where none had.</summary>
    public double Average(DayOfWeek day, int hour) =>
        Hours[(int)day][hour] > 0 ? (double)Totals[(int)day][hour] / Hours[(int)day][hour] : 0;

    /// <summary>The busiest hour of the week on average, for scaling the shades.</summary>
    public double Highest
    {
        get
        {
            double highest = 0;

            for (int day = 0; day < 7; day++)
            {
                for (int hour = 0; hour < 24; hour++)
                {
                    highest = Math.Max(highest, Average((DayOfWeek)day, hour));
                }
            }

            return highest;
        }
    }
}

/// <summary>One year's totals.</summary>
public readonly record struct YearTotal(int Year, UsageTotals Totals);

/// <summary>Everything since recording began.</summary>
/// <param name="First">The first minute recorded, or null when nothing is.</param>
/// <param name="Last">The last minute recorded, or null.</param>
/// <param name="Totals">Everything.</param>
/// <param name="Years">Each year, oldest first.</param>
/// <param name="BusiestDay">The busiest day ever, or null.</param>
/// <param name="PeakDown">The fastest second in ever, or null.</param>
/// <param name="PeakUp">The fastest second out ever, or null.</param>
public sealed record LifetimeReport(
    DateTimeOffset? First,
    DateTimeOffset? Last,
    UsageTotals Totals,
    IReadOnlyList<YearTotal> Years,
    ReportPoint? BusiestDay,
    PeakRate? PeakDown,
    PeakRate? PeakUp);

/// <summary>One step of one app's history.</summary>
/// <param name="Start">Where the step starts.</param>
/// <param name="MeasuredDown">Bytes in, as Windsock measured them.</param>
/// <param name="MeasuredUp">Bytes out, as Windsock measured them.</param>
/// <param name="WindowsDown">Bytes in, as Windows counted them where Windsock did not measure.</param>
/// <param name="WindowsUp">Bytes out, as Windows counted them.</param>
public readonly record struct AppPoint(DateTimeOffset Start, long MeasuredDown, long MeasuredUp, long WindowsDown, long WindowsUp)
{
    /// <summary>Everything, from either source.</summary>
    public long Total => MeasuredDown + MeasuredUp + WindowsDown + WindowsUp;
}
