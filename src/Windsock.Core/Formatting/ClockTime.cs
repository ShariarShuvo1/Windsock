using System.Globalization;

namespace Windsock.Core.Formatting;

/// <summary>
/// Reads and writes a time of day, to the minute.
/// </summary>
public static class ClockTime
{
    private const int MinutesPerHour = 60;
    private const int HoursPerDay = 24;
    private const int HoursPerHalfDay = 12;

    /// <summary>Formats a time on a 24 hour clock, as <c>HH:mm</c>.</summary>
    public static string Format(TimeOnly time) =>
        string.Create(CultureInfo.InvariantCulture, $"{time.Hour:D2}:{time.Minute:D2}");

    /// <summary>
    /// Formats a time on a 12 hour clock, as <c>hh:mm</c>, without the half.
    /// </summary>
    public static string FormatClock(TimeOnly time)
    {
        int hour = time.Hour % HoursPerHalfDay;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(hour == 0 ? HoursPerHalfDay : hour):D2}:{time.Minute:D2}");
    }

    /// <summary>Whether a time is in the afternoon half of the day.</summary>
    public static bool IsAfternoon(TimeOnly time) => time.Hour >= HoursPerHalfDay;

    /// <summary>The half of the day a time falls in.</summary>
    public static string Meridiem(TimeOnly time) => IsAfternoon(time) ? "PM" : "AM";

    /// <summary>
    /// Moves a time into one half of the day, keeping the hour on the clock.
    /// </summary>
    public static TimeOnly WithMeridiem(TimeOnly time, bool afternoon)
    {
        int hour = (time.Hour % HoursPerHalfDay) + (afternoon ? HoursPerHalfDay : 0);
        return new TimeOnly(hour, time.Minute);
    }

    /// <summary>
    /// Reads a time on a 24 hour clock, accepting <c>9</c>, <c>930</c>,
    /// <c>9:30</c>, <c>09.30</c> and <c>0930</c>.
    /// </summary>
    public static bool TryParse(string? text, out TimeOnly time)
    {
        time = default;

        return TryRead(text, out int hours, out int minutes)
            && hours < HoursPerDay
            && Build(hours, minutes, out time);
    }

    /// <summary>
    /// Reads a time typed onto a 12 hour clock, alongside the half of the day
    /// the AM/PM button is currently showing.
    /// </summary>
    public static bool TryParseClock(string? text, bool afternoon, out TimeOnly time)
    {
        time = default;

        if (!TryRead(text, out int hours, out int minutes) || hours >= HoursPerDay)
        {
            return false;
        }
        int hour = hours > HoursPerHalfDay
            ? hours
            : (hours % HoursPerHalfDay) + (afternoon ? HoursPerHalfDay : 0);

        return Build(hour, minutes, out time);
    }

    private static bool TryRead(string? text, out int hours, out int minutes)
    {
        hours = 0;
        minutes = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = text.AsSpan().Trim();
        int separator = trimmed.IndexOfAny(':', '.');

        if (separator >= 0)
        {
            // "14:" is half-typed rather than wrong, but it is not a time yet.
            return TryNumber(trimmed[..separator], out hours)
                && TryNumber(trimmed[(separator + 1)..], out minutes);
        }

        if (!TryNumber(trimmed, out int value))
        {
            return false;
        }
        if (trimmed.Length <= 2)
        {
            hours = value;
            return true;
        }

        hours = value / 100;
        minutes = value % 100;
        return true;
    }

    private static bool TryNumber(ReadOnlySpan<char> digits, out int value)
    {
        value = 0;

        if (digits.Length is 0 or > 4)
        {
            return false;
        }

        foreach (char c in digits)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool Build(int hours, int minutes, out TimeOnly time)
    {
        time = default;

        if (hours >= HoursPerDay || minutes >= MinutesPerHour)
        {
            return false;
        }

        time = new TimeOnly(hours, minutes);
        return true;
    }
}
