namespace Windsock.Core.History;

/// <summary>
/// Days, weeks, months and years on a time zone's wall clock, turned into
/// instants without being caught out by the clocks changing.
/// </summary>
/// <remarks>
/// <para>
/// A wall-clock time can be missing - the hour skipped when clocks go forward,
/// which in a few zones (Santiago, Beirut, until recently Havana) is midnight
/// itself - or can happen twice, in the hour repeated when they go back. A
/// missing time is taken as the first moment after the gap, so a day that
/// "starts at midnight" starts when the clock first shows that day; a repeated
/// one is taken at its first occurrence.
/// </para>
/// <para>
/// Every zone's offset is a whole number of quarter hours, so an epoch quarter
/// hour always lies inside one local hour, day and month; everything that buckets
/// history by the local clock relies on that.
/// </para>
/// </remarks>
public static class LocalCalendar
{
    /// <summary>The instant a wall-clock time happens in a zone.</summary>
    public static DateTimeOffset At(DateTime wall, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        DateTime unspecified = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(unspecified))
        {
            // Read with the offset in force before the gap, the time lands
            // after the jump: the first moment the clock shows past it.
            TimeSpan before = zone.GetUtcOffset(unspecified.AddHours(-3));
            DateTimeOffset instant = new(unspecified, before);
            return instant.ToOffset(zone.GetUtcOffset(instant));
        }

        if (zone.IsAmbiguousTime(unspecified))
        {
            TimeSpan[] offsets = zone.GetAmbiguousTimeOffsets(unspecified);
            return new DateTimeOffset(unspecified, offsets.Max());
        }

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    /// <summary>The wall-clock time of an instant in a zone.</summary>
    public static DateTime Wall(DateTimeOffset moment, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(moment, zone).DateTime;
    }

    /// <summary>The wall-clock time an epoch minute starts at in a zone.</summary>
    public static DateTime Wall(long minute, TimeZoneInfo zone) => Wall(EpochMinute.ToUtc(minute), zone);

    /// <summary>The date an instant falls on in a zone.</summary>
    public static DateOnly DateOf(DateTimeOffset moment, TimeZoneInfo zone) => DateOnly.FromDateTime(Wall(moment, zone));

    /// <summary>When a date begins in a zone.</summary>
    public static DateTimeOffset StartOf(DateOnly date, TimeZoneInfo zone) =>
        At(date.ToDateTime(TimeOnly.MinValue), zone);

    /// <summary>The first day of the week holding a date.</summary>
    public static DateOnly WeekOf(DateOnly date, DayOfWeek firstDay) =>
        date.AddDays(-(((int)date.DayOfWeek - (int)firstDay + 7) % 7));

    /// <summary>The first day of the month holding a date.</summary>
    public static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);

    /// <summary>
    /// The minutes after midnight a wall-clock time shows, which is what a
    /// nightly window is measured in.
    /// </summary>
    public static int MinuteOfDay(DateTime wall) => (wall.Hour * 60) + wall.Minute;

    /// <summary>
    /// The start of the bucket an epoch minute belongs to, for a step of an
    /// hour, a day, a week, a month or a year on the zone's clock.
    /// </summary>
    public static DateTimeOffset BucketStart(long minute, CalendarStep step, TimeZoneInfo zone, DayOfWeek firstDay = DayOfWeek.Monday)
    {
        DateTime wall = Wall(minute, zone);
        DateOnly date = DateOnly.FromDateTime(wall);

        return step switch
        {
            CalendarStep.Hour => At(new DateTime(wall.Year, wall.Month, wall.Day, wall.Hour, 0, 0, DateTimeKind.Unspecified), zone),
            CalendarStep.Day => StartOf(date, zone),
            CalendarStep.Week => StartOf(WeekOf(date, firstDay), zone),
            CalendarStep.Month => StartOf(MonthOf(date), zone),
            CalendarStep.Year => StartOf(new DateOnly(date.Year, 1, 1), zone),
            _ => EpochMinute.ToUtc(minute).ToOffset(zone.GetUtcOffset(EpochMinute.ToUtc(minute))),
        };
    }

    /// <summary>The start of the bucket after one beginning at a given instant.</summary>
    public static DateTimeOffset NextBucket(DateTimeOffset start, CalendarStep step, TimeZoneInfo zone)
    {
        DateTime wall = Wall(start, zone);
        DateOnly date = DateOnly.FromDateTime(wall);

        return step switch
        {
            CalendarStep.Minute => start.AddMinutes(1),
            CalendarStep.Hour => At(new DateTime(wall.Year, wall.Month, wall.Day, wall.Hour, 0, 0, DateTimeKind.Unspecified).AddHours(1), zone),
            CalendarStep.Day => StartOf(date.AddDays(1), zone),
            CalendarStep.Week => StartOf(date.AddDays(7), zone),
            CalendarStep.Month => StartOf(MonthOf(date).AddMonths(1), zone),
            CalendarStep.Year => StartOf(new DateOnly(date.Year + 1, 1, 1), zone),
            _ => start.AddMinutes(1),
        };
    }
}

/// <summary>A step of the wall clock that history is gathered in.</summary>
public enum CalendarStep
{
    Minute,

    Hour,

    Day,

    Week,

    Month,

    Year,
}
