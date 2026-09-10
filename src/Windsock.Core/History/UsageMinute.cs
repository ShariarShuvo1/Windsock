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
}
