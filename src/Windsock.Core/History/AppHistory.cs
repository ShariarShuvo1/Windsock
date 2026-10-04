using Windsock.Core.Apps;

namespace Windsock.Core.History;

/// <summary>
/// Each app's use of a period, from both sources: Windsock's own measurements
/// where it measured, Windows' figures elsewhere.
/// </summary>
public sealed class AppHistory
{
    private readonly IAppDetailStore _measured;
    private readonly IAppUsageStore _windows;
    private readonly IUsageHistoryStore _recorded;

    public AppHistory(IAppDetailStore measured, IAppUsageStore windows, IUsageHistoryStore recorded)
    {
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(recorded);

        _measured = measured;
        _windows = windows;
        _recorded = recorded;
    }

    /// <summary>
    /// Each app's share of one bucket of History, busiest first. A Windows
    /// record counts where History's own fill draws it: in the bucket it
    /// starts in, when it lies wholly inside the period shown.
    /// </summary>
    /// <param name="from">The bucket's start.</param>
    /// <param name="until">The bucket's end.</param>
    /// <param name="periodUntil">The end of the period shown; the bucket's end when not given.</param>
    public IReadOnlyList<AppShare> Breakdown(DateTimeOffset from, DateTimeOffset until, DateTimeOffset? periodUntil = null) =>
        Read(EpochMinute.From(from), EpochMinute.From(until), EpochMinute.From(periodUntil is { } end && end > until ? end : until));

    /// <summary>
    /// Each app's total over a period for the Apps page, busiest first. A
    /// Windows record starting in the period counts whole, as it always has
    /// there, so "Today" has the hour that is still going on.
    /// </summary>
    public IReadOnlyList<AppShare> Totals(DateTimeOffset from, DateTimeOffset until) =>
        Read(EpochMinute.From(from), EpochMinute.From(until), long.MaxValue);

    /// <summary>Whether per-minute app detail from before a moment has been compacted away.</summary>
    public bool IsCompacted(DateTimeOffset at) =>
        _measured.ReadAppDetailCompactedBefore() is { } before && EpochMinute.From(at) < before;

    /// <param name="fromMinute">The first minute.</param>
    /// <param name="untilMinute">The minute after the last; a record must start before it.</param>
    /// <param name="recordsUntil">The minute a record must end by.</param>
    private IReadOnlyList<AppShare> Read(long fromMinute, long untilMinute, long recordsUntil)
    {
        if (untilMinute <= fromMinute)
        {
            return [];
        }

        IReadOnlyList<AppUsageTotal> measured = _measured.ReadMeasuredTotals(fromMinute, untilMinute);
        IReadOnlyList<AppUsageSlice> windows = _windows.ReadAppSlices(fromMinute, untilMinute);

        // Measured time as far as a record starting in the period can reach, so
        // the share of it Windsock did not see is told right.
        long reach = recordsUntil == long.MaxValue
            ? windows.Select(slice => slice.StartMinute + slice.Minutes).DefaultIfEmpty(untilMinute).Max()
            : recordsUntil;
        IReadOnlyList<UsageWindow> spans = _measured.ReadMeasured(fromMinute, Math.Max(untilMinute, reach));

        windows = [.. windows, .. Moved(fromMinute, untilMinute, recordsUntil)];

        return AppBreakdown.Combine(measured, spans, windows, fromMinute, recordsUntil);
    }

    /// <summary>
    /// What Windows moved out of time Windsock was closed for, as records of
    /// that time starting in the period, placed where History's fill draws it.
    /// </summary>
    /// <remarks>
    /// The stretches are the chart's, not measuring's: measuring warms up a
    /// minute after recording starts, and Windows often has a record for that
    /// minute, which would look like Windows had kept the time before. Each
    /// app's own measured bytes decide how much was moved, and a record whose
    /// minutes were not all measured is left to count as it already does.
    /// </remarks>
    private List<AppUsageSlice> Moved(long fromMinute, long untilMinute, long recordsUntil)
    {
        long reach = Math.Min(recordsUntil, untilMinute + MovedTraffic.LookBehind) + MovedTraffic.CarryMinutes;
        IReadOnlyList<UsageWindow> measuredSpans = _measured.ReadMeasured(fromMinute, reach);
        Dictionary<(long, long), IReadOnlyList<AppUsageTotal>> totals = [];

        IReadOnlyList<MovedSpan> moved = MovedTraffic.Find(
            MovedTraffic.Recorded(_recorded, fromMinute, reach),
            _windows.ReadAppSlices,
            (start, end, app) =>
            {
                long seen = measuredSpans.Sum(span => Math.Max(0, Math.Min(end, span.EndMinute) - Math.Max(start, span.StartMinute)));

                if (seen < end - start)
                {
                    return null;
                }

                if (!totals.TryGetValue((start, end), out IReadOnlyList<AppUsageTotal>? each))
                {
                    each = _measured.ReadMeasuredTotals(start, end);
                    totals[(start, end)] = each;
                }

                AppUsageTotal found = each.FirstOrDefault(total => string.Equals(total.AttributionId, app, StringComparison.Ordinal));
                return (found.BytesReceived, found.BytesSent);
            },
            perApp: true,
            fromMinute,
            recordsUntil);

        // Only the stretches starting in the period, as for Windows' own records.
        return
        [
            .. moved
                .Where(span => span.StartMinute < untilMinute)
                .Select(span => new AppUsageSlice(span.StartMinute, span.Minutes, span.AttributionId ?? string.Empty, span.BytesReceived, span.BytesSent)),
        ];
    }
}