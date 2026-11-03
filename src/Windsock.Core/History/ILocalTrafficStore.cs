namespace Windsock.Core.History;

/// <summary>
/// How much of one recorded minute stayed on the local network: a copy from a
/// NAS, a game streamed to a TV. Bytes a data plan never counts.
/// </summary>
/// <param name="Minute">The epoch minute.</param>
/// <param name="BytesDown">Bytes received from addresses on the local network.</param>
/// <param name="BytesUp">Bytes sent to addresses on the local network.</param>
public readonly record struct LocalMinute(long Minute, long BytesDown, long BytesUp);

/// <summary>
/// Keeps the local-network share of recorded minutes, which only the kernel
/// trace can tell apart from internet traffic (the full edition, as
/// administrator).
/// </summary>
/// <remarks>
/// <para>
/// Each minute's share is two nullable columns beside its figures, reserved in
/// schema 6: NULL, the usual, means "not known", and everything that reads them
/// - plans, forecasts, reports, exports - takes the whole minute as internet
/// traffic then. A known share is taken off: <see cref="UsageQuarter.InternetDown"/>.
/// </para>
/// <para>
/// This is the only way in for whoever fills them; nothing else in the store
/// need be touched. Erasing a minute takes its share with it, and quarter-hour
/// totals follow by themselves.
/// </para>
/// </remarks>
public interface ILocalTrafficStore
{
    /// <summary>
    /// Sets the local share of minutes history already holds, replacing any set
    /// before, and returns the minutes it was set for. A share larger than the
    /// minute's own figure is cut to it. A minute not recorded (yet) is left
    /// out - the network minute is written when the next one begins - so the
    /// caller keeps it and offers it again.
    /// </summary>
    IReadOnlyList<long> WriteLocalTraffic(IReadOnlyList<LocalMinute> minutes);
}
