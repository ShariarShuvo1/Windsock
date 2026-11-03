using Windsock.Core.Connectivity;
using Windsock.Core.Networking;

namespace Windsock.Core.History;

/// <summary>
/// Network use over a stretch of up to a quarter hour, on one network.
/// </summary>
/// <param name="StartMinute">The epoch minute the stretch starts at: a quarter hour's first, or a single minute at a range's edge.</param>
/// <param name="NetworkId">The network's row in history, or 0 when the network was not known.</param>
/// <param name="BytesDown">Bytes received.</param>
/// <param name="BytesUp">Bytes sent.</param>
/// <param name="PeakDown">The fastest recorded second in, bytes per second.</param>
/// <param name="PeakUp">The fastest recorded second out, bytes per second.</param>
/// <param name="Minutes">How many minutes of it were recorded.</param>
/// <param name="LocalDown">
/// Bytes received that stayed on the local network, in the minutes where that
/// is known (see <see cref="ILocalTrafficStore"/>); zero where it is not.
/// </param>
/// <param name="LocalUp">Bytes sent that stayed on the local network, likewise.</param>
/// <param name="LocalMinutes">How many of the minutes the local share is known for.</param>
public readonly record struct UsageQuarter(
    long StartMinute,
    long NetworkId,
    long BytesDown,
    long BytesUp,
    long PeakDown,
    long PeakUp,
    int Minutes,
    long LocalDown = 0,
    long LocalUp = 0,
    int LocalMinutes = 0)
{
    /// <summary>Both directions.</summary>
    public long BytesTotal => BytesDown + BytesUp;

    /// <summary>
    /// Bytes received from the internet: the whole figure less what is known to
    /// have stayed on the local network, which is the whole figure where that is
    /// not known.
    /// </summary>
    public long InternetDown => Math.Max(0, BytesDown - LocalDown);

    /// <summary>Bytes sent to the internet, likewise.</summary>
    public long InternetUp => Math.Max(0, BytesUp - LocalUp);

    /// <summary>Both directions to and from the internet.</summary>
    public long InternetTotal => InternetDown + InternetUp;
}

/// <summary>A network history has minutes for, with what was recorded on it.</summary>
/// <param name="Id">Its row in history; what <see cref="UsageQuarter.NetworkId"/> refers to.</param>
/// <param name="Key">Its stable id (see <see cref="NetworkTag.Key"/>).</param>
/// <param name="Name">What Windows last called it.</param>
/// <param name="Kind">The kind of link.</param>
/// <param name="Metered">Whether Windows last treated it as metered, or null when not known.</param>
/// <param name="FirstMinute">The first minute recorded on it, or null when none is left.</param>
/// <param name="LastMinute">The last minute recorded on it, or null.</param>
/// <param name="BytesDown">Everything received on it.</param>
/// <param name="BytesUp">Everything sent on it.</param>
/// <param name="Minutes">How many minutes were recorded on it.</param>
public sealed record NetworkRecord(
    long Id,
    string Key,
    string Name,
    NetworkKind Kind,
    bool? Metered,
    long? FirstMinute,
    long? LastMinute,
    long BytesDown,
    long BytesUp,
    long Minutes)
{
    /// <summary>Both directions.</summary>
    public long BytesTotal => BytesDown + BytesUp;

    /// <summary>The network as a tag.</summary>
    public NetworkTag Tag => new(Key, Name, Kind, Metered);
}

/// <summary>The fastest second recorded in a range, and the minute it was in.</summary>
public readonly record struct PeakMoment(long Minute, long BytesPerSecond);

/// <summary>
/// Network use read in quarter hours, which is what reports, plans and every
/// long chart are built from.
/// </summary>
/// <remarks>
/// The store keeps each quarter hour's totals beside the minutes, per network,
/// kept up by the database itself (triggers), so a year is some thirty-five
/// thousand rows rather than half a million. Every time zone's offset is a
/// whole number of quarter hours, so any local hour, day or month is made of
/// whole quarters.
/// </remarks>
public interface IUsageAggregates
{
    /// <summary>
    /// Network use in a range: whole quarter hours from their totals, and the
    /// minutes at either edge one by one, in no particular order.
    /// </summary>
    IReadOnlyList<UsageQuarter> ReadQuarters(long fromMinute, long untilMinute);

    /// <summary>Recorded minutes one by one, each with the network it was measured on, oldest first.</summary>
    IReadOnlyList<UsageMinute> ReadMinuteRows(long fromMinute, long untilMinute);

    /// <summary>Every network history holds minutes for, with its totals.</summary>
    IReadOnlyList<NetworkRecord> ReadNetworks();

    /// <summary>Each network's row by its key, without totals: cheap enough to ask every minute.</summary>
    IReadOnlyDictionary<string, long> ReadNetworkIds();

    /// <summary>Each network by its row, without totals, for naming the rows <see cref="ReadQuarters"/> gives.</summary>
    IReadOnlyDictionary<long, NetworkTag> ReadNetworkTags();

    /// <summary>The fastest recorded second in a range, in or out, or null when nothing was recorded.</summary>
    PeakMoment? ReadPeak(long fromMinute, long untilMinute, bool upload);

    /// <summary>
    /// Goes up whenever history changes other than by a new minute being
    /// recorded: an erase, an import, minutes an older version left to settle.
    /// Anything caching totals of the past drops them when it moves.
    /// </summary>
    long Revision { get; }
}
