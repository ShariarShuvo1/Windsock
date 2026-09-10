namespace Windsock.Core.Formatting;

/// <summary>Unit a duration is entered and displayed in.</summary>
public enum TimeUnit
{
    Milliseconds,
    Seconds,
    Minutes,
    Hours,
}

/// <summary>Converts durations between a number and a unit.</summary>
public static class TimeUnits
{
    /// <summary>Units offered by a picker, shortest first.</summary>
    public static IReadOnlyList<TimeUnit> All { get; } =
        [TimeUnit.Milliseconds, TimeUnit.Seconds, TimeUnit.Minutes, TimeUnit.Hours];

    /// <summary>Short label, for example <c>ms</c>.</summary>
    public static string Label(TimeUnit unit) => unit switch
    {
        TimeUnit.Milliseconds => "ms",
        TimeUnit.Seconds => "s",
        TimeUnit.Minutes => "m",
        TimeUnit.Hours => "h",
        _ => "ms",
    };

    /// <summary>Builds a duration from a number and a unit.</summary>
    public static TimeSpan ToTimeSpan(double value, TimeUnit unit) => unit switch
    {
        TimeUnit.Milliseconds => TimeSpan.FromMilliseconds(value),
        TimeUnit.Seconds => TimeSpan.FromSeconds(value),
        TimeUnit.Minutes => TimeSpan.FromMinutes(value),
        TimeUnit.Hours => TimeSpan.FromHours(value),
        _ => TimeSpan.FromMilliseconds(value),
    };

    /// <summary>
    /// Expresses a duration in the largest unit that still leaves a whole number.
    /// </summary>
    public static (double Value, TimeUnit Unit) Split(TimeSpan duration)
    {
        double milliseconds = duration.TotalMilliseconds;
        if (milliseconds <= 0)
        {
            return (0, TimeUnit.Milliseconds);
        }

        if (IsWhole(duration.TotalHours))
        {
            return (Math.Round(duration.TotalHours), TimeUnit.Hours);
        }

        if (IsWhole(duration.TotalMinutes))
        {
            return (Math.Round(duration.TotalMinutes), TimeUnit.Minutes);
        }

        if (IsWhole(duration.TotalSeconds))
        {
            return (Math.Round(duration.TotalSeconds), TimeUnit.Seconds);
        }

        return (Math.Round(milliseconds), TimeUnit.Milliseconds);
    }

    private static bool IsWhole(double value) =>
        value >= 1 && Math.Abs(value - Math.Round(value)) < 1e-9;
}
