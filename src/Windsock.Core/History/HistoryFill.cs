using Windsock.Core.Apps;

namespace Windsock.Core.History;

/// <summary>
/// Puts what Windows counted while Windsock was closed beside what Windsock
/// recorded, without ever mixing the two into one figure.
/// </summary>
/// <remarks>
/// Only windows with not one recorded minute in them are brought in (see
/// <see cref="IAppUsageStore.ReadMissedSpans"/>), so a bucket's recorded part
/// and its estimated part never cover the same minute. Windows' figures are
/// kept in fields of their own all the way to the screen, where they are drawn
/// and labelled as Windows', and they never reach an export: a CSV holds only
/// what Windsock measured.
/// </remarks>
public static class HistoryFill
{
    /// <summary>
    /// The recorded buckets with Windows' figures added to the bucket each
    /// window starts in, and buckets of their own where nothing was recorded.
    /// </summary>
    public static IReadOnlyList<UsageBucket> Merge(
        IReadOnlyList<UsageBucket> recorded,
        IReadOnlyList<UsageSpan> missed,
        UsageGranularity granularity)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(missed);

        if (missed.Count == 0)
        {
            return recorded;
        }

        Dictionary<long, UsageBucket> buckets = new(recorded.Count + missed.Count);

        foreach (UsageBucket bucket in recorded)
        {
            buckets[bucket.Start.UtcTicks] = bucket;
        }

        foreach (UsageSpan span in missed)
        {
            DateTimeOffset start = BucketStart(span.StartMinute, granularity);
            long key = start.UtcTicks;

            UsageBucket bucket = buckets.TryGetValue(key, out UsageBucket existing)
                ? existing
                : new UsageBucket(start, 0, 0, 0, 0, 0);

            buckets[key] = bucket with
            {
                EstimatedDown = bucket.EstimatedDown + span.BytesReceived,
                EstimatedUp = bucket.EstimatedUp + span.BytesSent,
                EstimatedMinutes = bucket.EstimatedMinutes + span.Minutes,
            };
        }

        List<UsageBucket> merged = [.. buckets.Values];
        merged.Sort((x, y) => x.Start.CompareTo(y.Start));
        return merged;
    }

    /// <summary>A range's totals with Windows' figures for the time Windsock missed.</summary>
    public static UsageSummary Add(UsageSummary summary, IReadOnlyList<UsageSpan> missed)
    {
        ArgumentNullException.ThrowIfNull(missed);

        foreach (UsageSpan span in missed)
        {
            summary = summary with
            {
                EstimatedDown = summary.EstimatedDown + span.BytesReceived,
                EstimatedUp = summary.EstimatedUp + span.BytesSent,
                EstimatedMinutes = summary.EstimatedMinutes + span.Minutes,
            };
        }

        return summary;
    }

    /// <summary>
    /// The start of the bucket a minute belongs to, worked out the way the
    /// history store works out its own: by the local clock.
    /// </summary>
    public static DateTimeOffset BucketStart(long minute, UsageGranularity granularity)
    {
        DateTimeOffset local = EpochMinute.ToLocal(minute);

        if (granularity == UsageGranularity.Minute)
        {
            return local;
        }

        DateTime wall = granularity == UsageGranularity.Hour
            ? new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified)
            : new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified);

        return new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall));
    }
}
