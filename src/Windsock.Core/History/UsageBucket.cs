namespace Windsock.Core.History;

/// <summary>
/// A span of history rolled up for display: a minute, an hour or a day.
/// </summary>
public readonly record struct UsageBucket(
    DateTimeOffset Start,
    long BytesDown,
    long BytesUp,
    long PeakDown,
    long PeakUp,
    int Minutes)
{
    /// <summary>Bytes moved in both directions.</summary>
    public long BytesTotal => BytesDown + BytesUp;
}

/// <summary>Totals across a whole range, without breaking it into buckets.</summary>
public readonly record struct UsageSummary(
    long BytesDown,
    long BytesUp,
    long PeakDown,
    long PeakUp,
    int Minutes)
{
    /// <summary>Bytes moved in both directions.</summary>
    public long BytesTotal => BytesDown + BytesUp;

    /// <summary>A range with nothing recorded in it.</summary>
    public static UsageSummary Empty => default;
}
