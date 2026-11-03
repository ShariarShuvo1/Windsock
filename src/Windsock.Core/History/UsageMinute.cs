using Windsock.Core.Networking;

namespace Windsock.Core.History;

/// <summary>
/// One minute of recorded transfer: the unit history is stored in.
/// </summary>
public readonly record struct UsageMinute(
    long Minute,
    long BytesDown,
    long BytesUp,
    long PeakDown,
    long PeakUp)
{
    /// <summary>Bytes moved in both directions.</summary>
    public long BytesTotal => BytesDown + BytesUp;

    /// <summary>
    /// The network the minute was measured on - the one that carried most of
    /// its bytes - or null when that is not known.
    /// </summary>
    public NetworkTag? Network { get; init; }

    /// <summary>Bytes received that stayed on the local network, or null when that is not known.</summary>
    public long? LocalDown { get; init; }

    /// <summary>Bytes sent that stayed on the local network, or null when that is not known.</summary>
    public long? LocalUp { get; init; }
}
