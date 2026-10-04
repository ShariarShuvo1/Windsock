namespace Windsock.Core.History;

/// <summary>
/// A span of history rolled up for display: a minute, an hour or a day.
/// </summary>
/// <param name="Start">When the bucket begins, in local time.</param>
/// <param name="BytesDown">Bytes received in the minutes Windsock recorded.</param>
/// <param name="BytesUp">Bytes sent in the minutes Windsock recorded.</param>
/// <param name="PeakDown">The fastest recorded second in, in bytes per second.</param>
/// <param name="PeakUp">The fastest recorded second out, in bytes per second.</param>
/// <param name="Minutes">How many minutes of the bucket Windsock recorded.</param>
/// <param name="EstimatedDown">
/// Bytes Windows counted as received in time Windsock was not recording. Kept
/// apart from what Windsock measured, because Windows measures differently.
/// </param>
/// <param name="EstimatedUp">Bytes Windows counted as sent in that time.</param>
/// <param name="EstimatedMinutes">How many minutes Windows' figures cover.</param>
public readonly record struct UsageBucket(
    DateTimeOffset Start,
    long BytesDown,
    long BytesUp,
    long PeakDown,
    long PeakUp,
    int Minutes,
    long EstimatedDown = 0,
    long EstimatedUp = 0,
    int EstimatedMinutes = 0)
{
    /// <summary>Bytes moved in both directions, in the minutes Windsock recorded.</summary>
    public long BytesTotal => BytesDown + BytesUp;

    /// <summary>Everything received, whoever counted it.</summary>
    public long AllDown => BytesDown + EstimatedDown;

    /// <summary>Everything sent, whoever counted it.</summary>
    public long AllUp => BytesUp + EstimatedUp;

    /// <summary>Everything moved, both directions, whoever counted it.</summary>
    public long AllTotal => AllDown + AllUp;

    /// <summary>Whether any of the bucket comes from Windows rather than Windsock.</summary>
    public bool HasEstimate => EstimatedMinutes > 0;
}

/// <summary>Totals across a whole range, without breaking it into buckets.</summary>
public readonly record struct UsageSummary(
    long BytesDown,
    long BytesUp,
    long PeakDown,
    long PeakUp,
    int Minutes,
    long EstimatedDown = 0,
    long EstimatedUp = 0,
    int EstimatedMinutes = 0)
{
    /// <summary>Bytes moved in both directions, in the minutes Windsock recorded.</summary>
    public long BytesTotal => BytesDown + BytesUp;

    /// <summary>Everything received, whoever counted it.</summary>
    public long AllDown => BytesDown + EstimatedDown;

    /// <summary>Everything sent, whoever counted it.</summary>
    public long AllUp => BytesUp + EstimatedUp;

    /// <summary>Everything moved, whoever counted it.</summary>
    public long AllTotal => AllDown + AllUp;

    /// <summary>A range with nothing recorded in it.</summary>
    public static UsageSummary Empty => default;
}
