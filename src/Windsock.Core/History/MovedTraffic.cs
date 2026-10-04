using Windsock.Core.Apps;

namespace Windsock.Core.History;

/// <summary>Traffic Windows moved out of a stretch Windsock missed, put back where it happened.</summary>
/// <param name="StartMinute">Where the missed stretch starts.</param>
/// <param name="Minutes">How long it is.</param>
/// <param name="AttributionId">The app, or null for every app together.</param>
/// <param name="BytesReceived">Bytes received in it, as far as can be told.</param>
/// <param name="BytesSent">Bytes sent in it.</param>
public readonly record struct MovedSpan(long StartMinute, int Minutes, string? AttributionId, long BytesReceived, long BytesSent);

/// <summary>
/// Finds what Windows moved out of time Windsock missed into time Windsock kept.
/// </summary>
/// <remarks>
/// <para>
/// Windows rewrites its recent records every few minutes, merging them. Run for
/// real on 2026-09-16: Windsock was closed from 02:14, and afterwards Windows
/// held no record at all for 02:14 to 02:28 and one of 1.46 GB at 02:29, where
/// Windsock measured about 150 MB. Everything that fills History and each app's
/// history from Windows' figures leaves out a record lying in time Windsock
/// kept, since there it is the same bytes counted again - so those ten minutes
/// showed as nothing, in the chart and in every app.
/// </para>
/// <para>
/// A record is taken to hold moved traffic only when all of this holds: it lies
/// wholly in the first <see cref="CarryMinutes"/> of a stretch Windsock kept;
/// Windows has not one record for the stretch just before, back to the last
/// stretch kept or <see cref="LookBehind"/> minutes; and it holds more than
/// <see cref="Headers"/> times what Windsock kept for the same minutes, plus
/// <see cref="Floor"/> - more than packet headers explain. What it holds beyond
/// what Windsock kept is put in the stretch before, as Windows' figure, where
/// History draws what Windows counted while Windsock was closed.
/// </para>
/// <para>
/// Everywhere else a kept minute's record stays out: records move between kept
/// minutes as well, and taking them there would count bytes twice.
/// </para>
/// </remarks>
public static class MovedTraffic
{
    /// <summary>How long after keeping resumes a record may hold the time before.</summary>
    public const int CarryMinutes = 20;

    /// <summary>How far back the missed stretch before is looked for.</summary>
    public const int LookBehind = 60;

    /// <summary>How far above Windsock's own figure headers alone can put Windows'.</summary>
    public const double Headers = 1.5;

    /// <summary>The least a record must hold beyond that to count as moved.</summary>
    public const long Floor = 256 * 1024;

    /// <summary>
    /// Everything moved into the stretches given, placed in the missed stretch
    /// before each.
    /// </summary>
    /// <param name="kept">The stretches Windsock kept, whole, in any order.</param>
    /// <param name="windowsBetween">Windows' records starting between two minutes.</param>
    /// <param name="keptBetween">
    /// What Windsock kept between two minutes, for one app, or every app
    /// together when the app is null; null when it did not keep those minutes
    /// whole, so the record is left to count as it already does.
    /// </param>
    /// <param name="perApp">Whether to find it app by app, or for every app together.</param>
    /// <param name="fromMinute">Only a missed stretch starting at or after this minute is wanted.</param>
    /// <param name="untilMinute">Only a missed stretch ending at or before this minute is wanted.</param>
    /// <remarks>
    /// Windows is asked only about stretches whose missed time is wanted, so a
    /// long period with many stretches costs a small read for each.
    /// </remarks>
    public static IReadOnlyList<MovedSpan> Find(
        IReadOnlyList<UsageWindow> kept,
        Func<long, long, IReadOnlyList<AppUsageSlice>> windowsBetween,
        Func<long, long, string?, (long Down, long Up)?> keptBetween,
        bool perApp,
        long fromMinute = long.MinValue,
        long untilMinute = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentNullException.ThrowIfNull(windowsBetween);
        ArgumentNullException.ThrowIfNull(keptBetween);

        List<UsageWindow> stretches = [.. kept.Where(stretch => stretch.Minutes > 0).OrderBy(stretch => stretch.StartMinute)];
        List<MovedSpan> moved = [];
        long previousEnd = long.MinValue;

        foreach (UsageWindow stretch in stretches)
        {
            long gapStart = Math.Max(previousEnd, stretch.StartMinute - LookBehind);
            previousEnd = Math.Max(previousEnd, stretch.EndMinute);

            if (gapStart >= stretch.StartMinute || gapStart < fromMinute || stretch.StartMinute > untilMinute)
            {
                continue;
            }

            long carryEnd = Math.Min(stretch.EndMinute, stretch.StartMinute + CarryMinutes);
            IReadOnlyList<AppUsageSlice> records = windowsBetween(gapStart, carryEnd);

            // Windows kept something for the stretch before: nothing was moved out of it.
            if (records.Any(record => record.StartMinute < stretch.StartMinute))
            {
                continue;
            }

            int gapMinutes = (int)Math.Min(int.MaxValue, stretch.StartMinute - gapStart);
            Dictionary<string, (long Down, long Up)> found = new(StringComparer.Ordinal);

            IEnumerable<IGrouping<(long Start, int Minutes, string? App), AppUsageSlice>> groups = records
                .Where(record => record.Minutes > 0
                    && record.StartMinute >= stretch.StartMinute
                    && record.StartMinute + record.Minutes <= carryEnd)
                .GroupBy(record => (record.StartMinute, record.Minutes, perApp ? record.AttributionId : null));

            foreach (IGrouping<(long Start, int Minutes, string? App), AppUsageSlice> group in groups)
            {
                long windowsDown = group.Sum(record => record.BytesReceived);
                long windowsUp = group.Sum(record => record.BytesSent);
                if (keptBetween(group.Key.Start, group.Key.Start + group.Key.Minutes, group.Key.App) is not (long keptDown, long keptUp))
                {
                    continue;
                }

                long down = Beyond(windowsDown, keptDown);
                long up = Beyond(windowsUp, keptUp);

                if (down <= 0 && up <= 0)
                {
                    continue;
                }

                string key = group.Key.App ?? string.Empty;
                (long d, long u) = found.GetValueOrDefault(key);
                found[key] = (d + down, u + up);
            }

            foreach ((string app, (long down, long up)) in found)
            {
                moved.Add(new MovedSpan(gapStart, gapMinutes, perApp ? app : null, down, up));
            }
        }

        return moved;
    }

    /// <summary>What Windows holds beyond what Windsock kept, when that is more than headers explain.</summary>
    public static long Beyond(long windows, long kept) =>
        windows > (kept * Headers) + Floor ? windows - kept : 0;

    /// <summary>
    /// History's fill: the windows that hold no recorded minute, and the
    /// traffic Windows moved out of the stretches Windsock was closed for,
    /// in the period, in order.
    /// </summary>
    public static IReadOnlyList<UsageSpan> Fill(IUsageHistoryStore recorded, IAppUsageStore windows, DateTimeOffset from, DateTimeOffset until)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(windows);

        IReadOnlyList<UsageSpan> missed = windows.ReadMissedSpans(from, until);
        long fromMinute = EpochMinute.From(from);
        long untilMinute = EpochMinute.From(until);

        if (untilMinute <= fromMinute)
        {
            return missed;
        }

        IReadOnlyList<MovedSpan> moved = Find(
            Recorded(recorded, fromMinute, untilMinute),
            windows.ReadAppSlices,
            (start, end, _) =>
            {
                UsageSummary summary = recorded.Summarise(EpochMinute.ToUtc(start), EpochMinute.ToUtc(end));
                return ((long, long)?)(summary.BytesDown, summary.BytesUp);
            },
            perApp: false,
            fromMinute,
            untilMinute);

        if (moved.Count == 0)
        {
            return missed;
        }

        // As for the rest of the fill, only a stretch wholly inside the period.
        List<UsageSpan> all = [.. missed, .. moved.Select(span => new UsageSpan(span.StartMinute, span.Minutes, span.BytesReceived, span.BytesSent))];
        all.Sort((left, right) => left.StartMinute.CompareTo(right.StartMinute));
        return all;
    }

    /// <summary>
    /// The stretches History recorded from <see cref="LookBehind"/> minutes
    /// before a period to the minute after it, each merged with any it runs
    /// straight on from.
    /// </summary>
    public static IReadOnlyList<UsageWindow> Recorded(IUsageHistoryStore recorded, long fromMinute, long untilMinute)
    {
        ArgumentNullException.ThrowIfNull(recorded);

        List<UsageWindow> stretches = [];

        foreach (CoverageWindow window in recorded.ReadCoverage(
            EpochMinute.ToUtc(fromMinute - LookBehind),
            EpochMinute.ToUtc(untilMinute + 1)))
        {
            long start = EpochMinute.From(window.Start);
            long end = EpochMinute.From(window.End) + 1;

            if (stretches.Count > 0 && start <= stretches[^1].EndMinute)
            {
                UsageWindow last = stretches[^1];
                stretches[^1] = new UsageWindow(last.StartMinute, (int)(Math.Max(last.EndMinute, end) - last.StartMinute));
            }
            else if (end > start)
            {
                stretches.Add(new UsageWindow(start, (int)(end - start)));
            }
        }

        return stretches;
    }
}
