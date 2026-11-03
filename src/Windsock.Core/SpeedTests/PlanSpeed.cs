using Windsock.Core.History;

namespace Windsock.Core.SpeedTests;

/// <summary>One hour of the day, over a period: what speed tests and the busiest seconds showed against the plan.</summary>
/// <param name="Hour">The hour, 0 to 23, in local time.</param>
/// <param name="Tests">How many tests ran in this hour.</param>
/// <param name="DownloadMbps">The tests' median download.</param>
/// <param name="UploadMbps">The tests' median upload.</param>
/// <param name="DownloadShare">That median as a share of the plan's download, when a plan is given.</param>
/// <param name="UploadShare">The same for upload.</param>
/// <param name="PeakDownloadMbps">The fastest second downloaded in this hour on any day, from history.</param>
/// <param name="PeakUploadMbps">The fastest second uploaded.</param>
/// <param name="PeakDownloadShare">That peak as a share of the plan.</param>
public sealed record HourOfDay(
    int Hour,
    int Tests,
    double? DownloadMbps,
    double? UploadMbps,
    double? DownloadShare,
    double? UploadShare,
    double? PeakDownloadMbps,
    double? PeakUploadMbps,
    double? PeakDownloadShare);

/// <summary>The comparison as a whole.</summary>
/// <param name="Hours">All 24 hours.</param>
/// <param name="Tests">Tests counted.</param>
/// <param name="DownloadShare">The median test's download as a share of the plan.</param>
/// <param name="UploadShare">The same for upload.</param>
/// <param name="SlowestHour">The hour whose tests came furthest below the plan, when there were tests.</param>
public sealed record PlanSpeedSummary(IReadOnlyList<HourOfDay> Hours, int Tests, double? DownloadShare, double? UploadShare, int? SlowestHour);

/// <summary>Holds the plan's promised speed against what arrives, by hour of day.</summary>
/// <remarks>
/// Two kinds of evidence, kept apart because they mean different things: speed
/// tests measure what the line can do at that moment; the busiest second in
/// history is only what was asked of it, so a low peak may just be a quiet
/// hour. Only the tests can say a plan is not delivered; the peaks can say it
/// is, when they reach it.
/// </remarks>
public static class PlanSpeed
{
    /// <summary>Compares.</summary>
    /// <param name="planDownloadMbps">The plan's download, or zero when not given.</param>
    /// <param name="planUploadMbps">The plan's upload, or zero when not given.</param>
    /// <param name="tests">Speed tests in the period.</param>
    /// <param name="hourly">History in the period, by hour, read through History's own read API.</param>
    public static PlanSpeedSummary Compare(
        double planDownloadMbps,
        double planUploadMbps,
        IEnumerable<SpeedTestResult> tests,
        IEnumerable<UsageBucket> hourly)
    {
        ArgumentNullException.ThrowIfNull(tests);
        ArgumentNullException.ThrowIfNull(hourly);

        SpeedTestResult[] good = [.. tests.Where(each => each.Succeeded)];
        Dictionary<int, (long Down, long Up)> peaks = [];

        foreach (UsageBucket bucket in hourly)
        {
            if (bucket.Minutes <= 0)
            {
                continue;
            }

            int hour = bucket.Start.Hour;
            (long down, long up) = peaks.GetValueOrDefault(hour);
            peaks[hour] = (Math.Max(down, bucket.PeakDown), Math.Max(up, bucket.PeakUp));
        }

        List<HourOfDay> hours = new(24);

        for (int hour = 0; hour < 24; hour++)
        {
            SpeedTestResult[] these = [.. good.Where(each => each.At.Hour == hour)];
            double? down = Median(these.Select(each => each.DownloadMbps));
            double? up = Median(these.Select(each => each.UploadMbps));
            double? peakDown = peaks.TryGetValue(hour, out (long Down, long Up) peak) && peak.Down > 0 ? peak.Down * 8 / 1e6 : null;
            double? peakUp = peak.Up > 0 ? peak.Up * 8 / 1e6 : null;

            hours.Add(new HourOfDay(
                hour,
                these.Length,
                down,
                up,
                Share(down, planDownloadMbps),
                Share(up, planUploadMbps),
                peakDown,
                peakUp,
                Share(peakDown, planDownloadMbps)));
        }

        int? slowest = hours.Where(each => each.DownloadShare is not null)
            .OrderBy(each => each.DownloadShare)
            .Select(each => (int?)each.Hour)
            .FirstOrDefault();

        return new PlanSpeedSummary(
            hours,
            good.Length,
            Share(Median(good.Select(each => each.DownloadMbps)), planDownloadMbps),
            Share(Median(good.Select(each => each.UploadMbps)), planUploadMbps),
            slowest);
    }

    private static double? Share(double? measured, double plan) =>
        measured is { } value && plan > 0 ? value / plan : null;

    private static double? Median(IEnumerable<double?> values) => Connectivity.LatencyScale.Median(values);
}
