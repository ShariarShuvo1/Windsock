using Windsock.Core.Apps;

namespace Windsock.Core.History;

/// <summary>One app's bytes over some stretch, as Windsock itself measured them.</summary>
/// <param name="AttributionId">The app, named the way Windows' data usage names it (see <see cref="AppAttribution"/>).</param>
/// <param name="BytesReceived">Bytes the app received.</param>
/// <param name="BytesSent">Bytes the app sent.</param>
public readonly record struct AppBytes(string AttributionId, long BytesReceived, long BytesSent);

/// <summary>
/// Each app's traffic a minute at a time, as Windsock measured it with the
/// kernel trace, kept for as long as history is.
/// </summary>
/// <remarks>
/// <para>
/// Beside the per-minute rows the store keeps each app's quarter-hour totals,
/// written with them. A long period reads the quarters; compaction removes old
/// minutes and leaves the quarters, so nothing is lost but the detail. Every
/// time zone's offset is a whole number of quarter hours, so a local hour or day
/// is always made of whole quarters.
/// </para>
/// <para>
/// A minute is kept only when Windsock watched all of it. Where it did, what it
/// measured stands and Windows' own figures for that time are not used: they
/// are the same bytes counted again, later, and Windows goes on adjusting them.
/// </para>
/// </remarks>
public interface IAppDetailStore
{
    /// <summary>
    /// Keeps one minute Windsock measured whole: each app's bytes, and that the
    /// minute was measured even if nothing moved. A minute already kept, or one
    /// inside an erased period, is left alone.
    /// </summary>
    void WriteMeasuredMinute(long minute, IReadOnlyList<AppBytes> apps);

    /// <summary>The stretches Windsock measured, clipped to the range.</summary>
    IReadOnlyList<UsageWindow> ReadMeasured(long fromMinute, long untilMinute);

    /// <summary>
    /// Each app's measured bytes over a range: whole quarter hours from their
    /// totals, and the edges from the minutes where those are still kept.
    /// </summary>
    IReadOnlyList<AppUsageTotal> ReadMeasuredTotals(long fromMinute, long untilMinute);

    /// <summary>
    /// Removes per-minute app detail from before a minute, a week at a time;
    /// the quarter-hour totals stay. Returns how many rows went.
    /// </summary>
    int CompactAppDetail(long beforeMinute);

    /// <summary>The minute before which per-minute app detail was compacted away, or null when none was.</summary>
    long? ReadAppDetailCompactedBefore();

    /// <summary>Rewrites the file so space freed by compacting or erasing goes back to the disk.</summary>
    void Reclaim();

    /// <summary>How much disk the history file takes now, its write-ahead log included.</summary>
    long FileBytes { get; }
}
