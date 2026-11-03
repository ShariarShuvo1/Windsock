using System.Globalization;
using Windsock.Core.Formatting;

namespace Windsock.App.ViewModels;

/// <summary>How the home network's pages write moments and rates, in one place.</summary>
/// <remarks>Times go through <see cref="ClockFormat"/>, Windows' own 12 or 24 hour choice.</remarks>
internal static class HomeText
{
    /// <summary>How long ago something was: "just now", "4 min ago", "today at 09:12", "yesterday at 21:40", "3 days ago", "12 Sep 2026".</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        TimeSpan gone = now - at;
        DateTimeOffset local = at.ToLocalTime();
        DateTimeOffset today = now.ToLocalTime();

        if (gone < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (gone < TimeSpan.FromHours(1))
        {
            return string.Create(CultureInfo.CurrentCulture, $"{(int)gone.TotalMinutes} min ago");
        }

        if (local.Date == today.Date)
        {
            return string.Concat("today at ", ClockFormat.Time(local));
        }

        if (local.Date == today.Date.AddDays(-1))
        {
            return string.Concat("yesterday at ", ClockFormat.Time(local));
        }

        int days = (int)(today.Date - local.Date).TotalDays;

        return days < 30
            ? string.Create(CultureInfo.CurrentCulture, $"{days} days ago")
            : local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>A rate in the network chart's own family, or a dash when it is not known.</summary>
    public static string Rate(double? bytesPerSecond, RateFamily family) =>
        bytesPerSecond is { } rate && double.IsFinite(rate) ? RateFormatter.Format(rate, family, RateScale.Auto) : "—";
}
