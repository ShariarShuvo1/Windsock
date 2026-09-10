namespace Windsock.Core.History;

/// <summary>
/// Converts between moments in time and the minute index history is keyed by.
/// </summary>
public static class EpochMinute
{
    /// <summary>Seconds in one minute bucket.</summary>
    public const long SecondsPerMinute = 60;

    /// <summary>The minute bucket a moment falls in.</summary>
    public static long From(DateTimeOffset moment)
    {
        long seconds = moment.ToUnixTimeSeconds();
        return seconds >= 0
            ? seconds / SecondsPerMinute
            : (seconds - (SecondsPerMinute - 1)) / SecondsPerMinute;
    }

    /// <summary>The instant a minute bucket starts, in UTC.</summary>
    public static DateTimeOffset ToUtc(long minute) =>
        DateTimeOffset.FromUnixTimeSeconds(minute * SecondsPerMinute);

    /// <summary>The instant a minute bucket starts, in the machine's time zone.</summary>
    public static DateTimeOffset ToLocal(long minute) => ToUtc(minute).ToLocalTime();
}
