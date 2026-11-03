using System.Globalization;
using Windsock.Core.Formatting;

namespace Windsock.App.ViewModels;

/// <summary>
/// How the usage pages - Data plan, Reports, an app over time - write dates,
/// times and amounts, in one place.
/// </summary>
/// <remarks>
/// Times go through <see cref="ClockFormat"/>, Windows' own 12 or 24 hour choice.
/// </remarks>
internal static class UsageText
{
    /// <summary>An amount of data: "41.0 GB".</summary>
    public static string Bytes(long bytes) => SizeFormatter.Format(bytes);

    /// <summary>A time of day on the region's clock.</summary>
    public static string Time(TimeOnly time) => ClockFormat.Time(DateTime.MinValue.Add(time.ToTimeSpan()));

    /// <summary>A time of day on the region's clock.</summary>
    public static string Time(DateTimeOffset moment) => ClockFormat.Time(moment.ToLocalTime());

    /// <summary>An hour alone, as an axis marks it: "21:00", or "9 PM".</summary>
    public static string Hour(int hour) => ClockFormat.Current.Hour(DateTime.MinValue.AddHours(hour % 24));

    /// <summary>A date, short: "5 Oct".</summary>
    public static string Date(DateOnly date) => date.ToString("d MMM", CultureInfo.CurrentCulture);

    /// <summary>A date with its day: "Mon 5 Oct".</summary>
    public static string Day(DateOnly date) => date.ToString("ddd d MMM", CultureInfo.CurrentCulture);

    /// <summary>A date with its year: "5 Oct 2026".</summary>
    public static string FullDate(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>A moment as a date and a time: "Mon 5 Oct, 14:00".</summary>
    public static string When(DateTimeOffset moment)
    {
        DateTime local = moment.ToLocalTime().DateTime;
        return string.Concat(Day(DateOnly.FromDateTime(local)), ", ", Time(TimeOnly.FromDateTime(local)));
    }

    /// <summary>A share as a whole percentage: "62%".</summary>
    public static string Percent(double share) =>
        share.ToString("P0", CultureInfo.CurrentCulture);

    /// <summary>A count with its noun, singular or plural: "1 day", "17 days".</summary>
    public static string Count(long count, string one, string many) =>
        string.Create(CultureInfo.CurrentCulture, $"{count:N0} {(count == 1 ? one : many)}");
}
