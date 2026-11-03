using System.Globalization;

namespace Windsock.Core.Formatting;

/// <summary>
/// How times and dates are written, from the patterns of one culture: the
/// 12 or 24-hour clock and the order of day and month that culture carries.
/// </summary>
/// <remarks>
/// Built from Windows' own choices through <see cref="ClockFormat.Current"/>;
/// built from any culture's patterns in tests.
/// </remarks>
public sealed class ClockPatterns
{
    private const string WeekdayAndDate = "ddd d MMM";

    private readonly string[] _written;

    /// <summary>Takes the patterns of one culture's date and time format.</summary>
    public ClockPatterns(DateTimeFormatInfo format)
    {
        ArgumentNullException.ThrowIfNull(format);

        Format = format;
        Is24Hour = Uses24Hour(format.ShortTimePattern);
        ShortTime = Tidy(format.ShortTimePattern, Is24Hour ? "HH:mm" : "h:mm tt");
        LongTime = Tidy(format.LongTimePattern, Is24Hour ? "HH:mm:ss" : "h:mm:ss tt");

        // Kept as they were now: the format object is the caller's, and Windows
        // lets the words for the halves of the day and the separators change
        // while the patterns stay the same.
        _written = [ShortTime, LongTime, format.ShortDatePattern, format.AMDesignator, format.PMDesignator, format.TimeSeparator, format.DateSeparator];
    }

    /// <summary>The culture's date and time format the patterns come from.</summary>
    public DateTimeFormatInfo Format { get; }

    /// <summary>Whether the clock is the 24-hour one.</summary>
    public bool Is24Hour { get; }

    /// <summary>A time to the minute: "21:04", or "9:04 PM".</summary>
    public string ShortTime { get; }

    /// <summary>A time to the second: "21:04:31", or "9:04:31 PM".</summary>
    public string LongTime { get; }

    /// <summary>The weekday, the day and the month, then a time to the minute: "Mon 5 Oct, 21:04".</summary>
    public string DayAndTime => string.Concat(WeekdayAndDate, ", ", ShortTime);

    /// <summary>The day and the month, then a time to the minute: "5 Oct, 21:04".</summary>
    public string DateAndTime => string.Concat("d MMM, ", ShortTime);

    /// <summary>A time to the minute, as this clock writes it.</summary>
    public string Time(DateTime when) => when.ToString(ShortTime, Format);

    /// <summary>A time to the minute, as this clock writes it.</summary>
    public string Time(DateTimeOffset when) => when.ToString(ShortTime, Format);

    /// <summary>A time to the second, as this clock writes it.</summary>
    public string TimeWithSeconds(DateTime when) => when.ToString(LongTime, Format);

    /// <summary>A time to the second, as this clock writes it.</summary>
    public string TimeWithSeconds(DateTimeOffset when) => when.ToString(LongTime, Format);

    /// <summary>The weekday, the date and a time to the minute.</summary>
    public string Day(DateTime when) => when.ToString(DayAndTime, Format);

    /// <summary>The weekday, the date and a time to the minute.</summary>
    public string Day(DateTimeOffset when) => when.ToString(DayAndTime, Format);

    /// <summary>The hour alone, as an axis marks it: "21:00", or "9 PM".</summary>
    public string Hour(DateTime when) => when.ToString(Is24Hour ? "HH:mm" : "h tt", Format);

    /// <summary>
    /// Whether two sets of patterns write every time and date the same way:
    /// the patterns, the words for the halves of the day ("AM", "a.m.") and
    /// the separators.
    /// </summary>
    public bool WritesLike(ClockPatterns other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return _written.AsSpan().SequenceEqual(other._written);
    }

    /// <summary>
    /// Whether a time pattern is on the 24-hour clock: it writes hours with
    /// <c>H</c>, and never with <c>h</c> or a half of the day.
    /// </summary>
    public static bool Uses24Hour(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        bool upper = false;
        bool quoted = false;

        foreach (char each in pattern)
        {
            if (each is '\'' or '"')
            {
                quoted = !quoted;
                continue;
            }

            if (quoted)
            {
                continue;
            }

            if (each is 'h' or 't')
            {
                return false;
            }

            upper |= each == 'H';
        }

        return upper;
    }

    /// <summary>
    /// A pattern as given, or a plain one of the same clock when it is empty
    /// or carries nothing that writes an hour.
    /// </summary>
    private static string Tidy(string? pattern, string fallback) =>
        string.IsNullOrWhiteSpace(pattern) || pattern.IndexOfAny(['h', 'H']) < 0 ? fallback : pattern;
}

/// <summary>
/// Windows' own choice of clock and date format, for every time and date the
/// app writes, kept up to date when the choice changes.
/// </summary>
/// <remarks>
/// <para>
/// .NET reads the user's Region settings into the current culture once and
/// keeps them: "The CultureInfo class does not automatically detect changes in
/// the system settings." So a change made while Windsock runs is taken by
/// <see cref="Refresh"/>, which the app calls when Windows broadcasts
/// <c>WM_SETTINGCHANGE</c> with "intl" (seen in .NET as the Locale category of
/// a user preference change). It clears the cached culture data and builds the
/// patterns again from a fresh culture with the user's overrides.
/// </para>
/// <para>
/// Never a hand-written "h:mm tt": somebody with a 24-hour clock in Windows was
/// shown AM and PM by every list that did that.
/// </para>
/// </remarks>
public static class ClockFormat
{
    private static ClockPatterns _current = Read();

    /// <summary>
    /// Raised after <see cref="Refresh"/> has found anything that writes a
    /// time or a date changed (<see cref="ClockPatterns.WritesLike"/>).
    /// </summary>
    public static event EventHandler? Changed;

    /// <summary>The patterns in use: Windows' choice as last read.</summary>
    public static ClockPatterns Current => Volatile.Read(ref _current);

    /// <summary>Whether Windows' clock is the 24-hour one.</summary>
    public static bool Is24Hour => Current.Is24Hour;

    /// <summary>A time to the minute.</summary>
    public static string Time(DateTime when) => Current.Time(when);

    /// <summary>A time to the minute.</summary>
    public static string Time(DateTimeOffset when) => Current.Time(when);

    /// <summary>A time to the second.</summary>
    public static string TimeWithSeconds(DateTime when) => Current.TimeWithSeconds(when);

    /// <summary>A time to the second.</summary>
    public static string TimeWithSeconds(DateTimeOffset when) => Current.TimeWithSeconds(when);

    /// <summary>The weekday, the date and a time to the minute.</summary>
    public static string Day(DateTime when) => Current.Day(when);

    /// <summary>The weekday, the date and a time to the minute.</summary>
    public static string Day(DateTimeOffset when) => Current.Day(when);

    /// <summary>
    /// Reads Windows' choice again. Returns whether it changed, and raises
    /// <see cref="Changed"/> when it did.
    /// </summary>
    public static bool Refresh()
    {
        CultureInfo.CurrentCulture.ClearCachedData();

        ClockPatterns fresh = Read();
        ClockPatterns was = Interlocked.Exchange(ref _current, fresh);

        bool changed = !was.WritesLike(fresh);

        if (changed)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }

        return changed;
    }

    private static ClockPatterns Read()
    {
        try
        {
            // A fresh object, not the thread's: the current culture of a thread
            // that already exists keeps what it read at the start.
            return new ClockPatterns(new CultureInfo(CultureInfo.CurrentCulture.Name, useUserOverride: true).DateTimeFormat);
        }
        catch (CultureNotFoundException)
        {
            return new ClockPatterns(CultureInfo.CurrentCulture.DateTimeFormat);
        }
    }
}
