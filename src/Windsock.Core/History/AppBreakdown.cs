using Windsock.Core.Apps;

namespace Windsock.Core.History;

/// <summary>One app's share of a period: what Windsock measured of it, and what Windows counted where Windsock did not measure.</summary>
/// <param name="AttributionId">The app, named as Windows names it.</param>
/// <param name="MeasuredDown">Bytes received, as Windsock measured them.</param>
/// <param name="MeasuredUp">Bytes sent, as Windsock measured them.</param>
/// <param name="WindowsDown">Bytes received, as Windows counted them for time Windsock did not measure.</param>
/// <param name="WindowsUp">Bytes sent, as Windows counted them for time Windsock did not measure.</param>
public readonly record struct AppShare(string AttributionId, long MeasuredDown, long MeasuredUp, long WindowsDown, long WindowsUp)
{
    /// <summary>Everything the app received, from either source.</summary>
    public long Down => MeasuredDown + WindowsDown;

    /// <summary>Everything the app sent, from either source.</summary>
    public long Up => MeasuredUp + WindowsUp;

    /// <summary>Everything the app moved.</summary>
    public long Total => Down + Up;

    /// <summary>What Windsock measured of it.</summary>
    public long Measured => MeasuredDown + MeasuredUp;

    /// <summary>What Windows counted of it.</summary>
    public long Windows => WindowsDown + WindowsUp;
}

/// <summary>
/// Puts Windsock's own measurements and Windows' figures together, app by app,
/// with Windsock's standing wherever it measured.
/// </summary>
public static class AppBreakdown
{
    /// <summary>
    /// Each app's share of a period, busiest first.
    /// </summary>
    /// <param name="measured">Each app's bytes Windsock measured in the period.</param>
    /// <param name="measuredSpans">The stretches of the period Windsock measured whole.</param>
    /// <param name="windows">Windows' records starting in the period.</param>
    /// <param name="fromMinute">The period's first minute.</param>
    /// <param name="untilMinute">The minute after its last.</param>
    /// <remarks>
    /// A Windows record lying in time Windsock measured is left out: those are
    /// the same bytes, counted again and still moving. One partly measured - an
    /// hour Windsock started halfway through - keeps the share of it Windsock
    /// did not see. As in History's fill, only records wholly inside the period
    /// count: an hour Windows keeps as one record cannot be cut at a minute.
    /// Traffic Windows moved out of unmeasured time into a measured minute is
    /// found by <see cref="MovedTraffic"/> and handed in as a record of the
    /// unmeasured time it came from.
    /// </remarks>
    public static IReadOnlyList<AppShare> Combine(
        IReadOnlyList<AppUsageTotal> measured,
        IReadOnlyList<UsageWindow> measuredSpans,
        IReadOnlyList<AppUsageSlice> windows,
        long fromMinute,
        long untilMinute)
    {
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(measuredSpans);
        ArgumentNullException.ThrowIfNull(windows);

        Dictionary<string, (long MeasuredDown, long MeasuredUp, double WindowsDown, double WindowsUp)> apps = new(StringComparer.Ordinal);

        foreach (AppUsageTotal total in measured)
        {
            (long md, long mu, double wd, double wu) = apps.GetValueOrDefault(total.AttributionId);
            apps[total.AttributionId] = (md + total.BytesReceived, mu + total.BytesSent, wd, wu);
        }

        foreach (AppUsageSlice slice in windows)
        {
            if (slice.Minutes <= 0 || slice.StartMinute < fromMinute || slice.StartMinute + slice.Minutes > untilMinute)
            {
                continue;
            }

            long seen = 0;

            foreach (UsageWindow span in measuredSpans)
            {
                seen += Math.Max(0, Math.Min(slice.StartMinute + slice.Minutes, span.EndMinute) - Math.Max(slice.StartMinute, span.StartMinute));
            }

            long unseen = slice.Minutes - Math.Min(seen, slice.Minutes);

            if (unseen <= 0)
            {
                continue;
            }

            double share = (double)unseen / slice.Minutes;
            (long md, long mu, double wd, double wu) = apps.GetValueOrDefault(slice.AttributionId);
            apps[slice.AttributionId] = (md, mu, wd + (slice.BytesReceived * share), wu + (slice.BytesSent * share));
        }

        List<AppShare> shares = new(apps.Count);

        foreach ((string id, (long md, long mu, double wd, double wu)) in apps)
        {
            AppShare share = new(id, md, mu, (long)Math.Round(wd), (long)Math.Round(wu));

            if (share.Total > 0)
            {
                shares.Add(share);
            }
        }

        shares.Sort(static (left, right) => right.Total.CompareTo(left.Total));
        return shares;
    }
}
