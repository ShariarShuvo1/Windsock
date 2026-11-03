using Windsock.Core.Settings;

namespace Windsock.Core.Alerts;

/// <summary>One stretch of quiet, as two instants.</summary>
/// <param name="Start">When it began.</param>
/// <param name="End">When it ends.</param>
public readonly record struct QuietSpan(DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>Whether a moment falls inside it.</summary>
    public bool Contains(DateTimeOffset moment) => moment >= Start && moment < End;
}

/// <summary>
/// Works out quiet hours and snoozes against the wall clock.
/// </summary>
/// <remarks>
/// <para>
/// A stretch is anchored to the day it begins on: 22:00 to 07:00 begun on
/// Friday is Friday 22:00 to Saturday 07:00, read on the wall clock of each
/// end and turned into instants. So a stretch spanning midnight is one
/// stretch, and a change of clock inside it moves neither end.
/// </para>
/// <para>
/// An end the clock skips - 02:30 on the night the clocks go forward an hour
/// at 02:00 - is the first moment after the skip; an end the clock passes
/// twice - 01:30 on the night they go back - is the first time it passes.
/// Either way the stretch is never longer than the wall clock says, and the
/// hour repeated after it ends is not quiet again.
/// </para>
/// </remarks>
public static class QuietHours
{
    /// <summary>The morning a snooze "until tomorrow" runs to when quiet hours do not say: 07:00.</summary>
    public const int DefaultMorning = 7 * 60;

    /// <summary>
    /// The quiet stretch a moment falls in, or null when it is not quiet or quiet
    /// hours are off.
    /// </summary>
    public static QuietSpan? StretchAt(QuietHoursSettings quiet, DateTimeOffset moment, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(quiet);
        ArgumentNullException.ThrowIfNull(zone);

        if (!quiet.Enabled)
        {
            return null;
        }

        DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, zone).DateTime);

        // A stretch holding this moment began today, or yesterday and runs past midnight.
        foreach (DateOnly day in new[] { today, today.AddDays(-1) })
        {
            if (Stretch(quiet, day, zone) is { } span && span.Contains(moment))
            {
                return span;
            }
        }

        return null;
    }

    /// <summary>
    /// The stretch that begins on a given day, or null when quiet hours do not
    /// begin that day. A stretch whose end is not after its start runs into the
    /// next day; one whose end equals its start lasts a whole day.
    /// </summary>
    public static QuietSpan? Stretch(QuietHoursSettings quiet, DateOnly day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(quiet);
        ArgumentNullException.ThrowIfNull(zone);

        if (!quiet.Days.Contains(day.DayOfWeek))
        {
            return null;
        }

        int start = quiet.Start;
        int end = quiet.End;
        DateOnly endDay = end <= start ? day.AddDays(1) : day;

        DateTimeOffset from = Instant(day, start, zone);
        DateTimeOffset until = Instant(endDay, end, zone);

        return until > from ? new QuietSpan(from, until) : null;
    }

    /// <summary>
    /// The instant a wall-clock time on a day stands for: the first moment after
    /// a skipped time, the first passing of a repeated one.
    /// </summary>
    public static DateTimeOffset Instant(DateOnly day, int minuteOfDay, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        DateTime wall = day.ToDateTime(TimeOnly.MinValue).AddMinutes(Math.Clamp(minuteOfDay, 0, QuietHoursSettings.Day));

        if (zone.IsInvalidTime(wall))
        {
            // The clock jumps over this time: the first moment it shows a time
            // at or after it is the moment it lands.
            DateTime landed = wall;

            for (int step = 0; step < QuietHoursSettings.Day && zone.IsInvalidTime(landed); step++)
            {
                landed = landed.AddMinutes(1);
            }

            return new DateTimeOffset(landed, zone.GetUtcOffset(landed));
        }

        if (zone.IsAmbiguousTime(wall))
        {
            TimeSpan[] offsets = zone.GetAmbiguousTimeOffsets(wall);
            TimeSpan earliest = offsets.Max();
            return new DateTimeOffset(wall, earliest);
        }

        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    /// <summary>
    /// Where a snooze "until tomorrow" ends: the morning, which is when quiet
    /// hours end if they are on, else 07:00 - today's, while that is still ahead
    /// (a snooze at one in the morning means until this morning), else
    /// tomorrow's.
    /// </summary>
    public static DateTimeOffset Morning(QuietHoursSettings quiet, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(quiet);
        ArgumentNullException.ThrowIfNull(zone);

        int morning = quiet.Enabled ? quiet.End : DefaultMorning;
        DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        DateTimeOffset todays = Instant(today, morning, zone);

        return todays > now ? todays : Instant(today.AddDays(1), morning, zone);
    }

    /// <summary>
    /// Whether alerts are being held now, and until when: during quiet hours or
    /// a snooze, whichever ends later when both apply.
    /// </summary>
    public static DateTimeOffset? HeldUntil(QuietHoursSettings quiet, DateTimeOffset? snoozedUntil, DateTimeOffset now, TimeZoneInfo zone)
    {
        DateTimeOffset? quietEnd = StretchAt(quiet, now, zone)?.End;
        DateTimeOffset? snoozeEnd = snoozedUntil is { } snooze && snooze > now ? snooze : null;

        return (quietEnd, snoozeEnd) switch
        {
            ({ } a, { } b) => a > b ? a : b,
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null,
        };
    }
}
