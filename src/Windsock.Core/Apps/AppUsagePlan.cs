namespace Windsock.Core.Apps;

/// <summary>
/// Decides which windows of time to ask Windows about next.
/// </summary>
/// <remarks>
/// <para>
/// Everything here rests on how Windows keeps its data usage, which was
/// measured on 2026-09-15 rather than taken from the documentation, because the
/// documentation does not say.
/// </para>
/// <para>
/// Windows keeps a record per app for each minute, and after about two hours
/// rolls those up into records covering roughly an hour, each stamped at one
/// moment inside it. A query returns the records stamped inside its window.
/// So any run of windows laid end to end counts every record exactly once: a
/// day four days back read 154,271,856,385 bytes as one window and as the sum
/// of its 24 hours, to the byte, and each recent hour matched the sum of its
/// sixty minutes. That is what lets collection simply walk forward from where
/// it stopped, with no bookkeeping about what was already counted.
/// </para>
/// <para>
/// What the windows' width decides is only where the bytes are placed. A minute
/// window within reach of the per-minute records places them to the minute,
/// and was checked against the adapter's own counters to within one percent.
/// Further back there is nothing finer than about an hour to place, so windows
/// are an hour wide, on the hour of the machine's clock. Records reached back
/// about a week; three weeks back there were none.
/// </para>
/// <para>
/// The newest minutes are not yet settled, and that is why <see cref="Settle"/>
/// is five minutes rather than one. Windows publishes a minute within seconds
/// of its end, but the newest record is provisional: watched every twenty
/// seconds on 2026-09-15, 04:01 read 9.7 MB, then 1.5 MB once 04:02 appeared
/// holding what had moved; 04:02 read 18.8 MB, then 0.2 MB a minute later.
/// While a record is moving, reading each minute separately counted it in both
/// places (121.8 MB against 102.3 MB for the same stretch read whole). Nothing
/// older than about two and a half minutes moved. So a minute is read only once
/// it has had five, and minutes read again replace what was kept for them
/// rather than adding to it.
/// </para>
/// </remarks>
public static class AppUsagePlan
{
    /// <summary>
    /// How far back per-minute records can be relied on. Windows kept them for
    /// a little over two hours; this leaves a margin.
    /// </summary>
    public const int MinuteDetail = 100;

    /// <summary>
    /// Minutes a minute is given, after it ends, before it is read. Windows'
    /// newest records settled within about two and a half minutes; this is that
    /// with a margin.
    /// </summary>
    public const int Settle = 5;

    /// <summary>
    /// Settled minutes read again on every pass, replacing what was kept for
    /// them, to take in a record Windows adjusted late.
    /// </summary>
    public const int Revisit = 10;

    /// <summary>
    /// How much of the recent past the live per-program figure covers. Read as
    /// one window, not minute by minute, because a record still moving between
    /// minutes is counted once in a single window and can be counted twice
    /// across several.
    /// </summary>
    public const int RecentSpan = 5;

    /// <summary>How far back a first collection reaches.</summary>
    public const int Backfill = 14 * 24 * 60;

    /// <summary>The most windows handed out in one plan, so a long catch-up goes in steps.</summary>
    public const int Batch = 240;

    /// <summary>The first minute that may not be read yet. Exclusive.</summary>
    public static long SafeEnd(long nowMinute) => nowMinute - Settle;

    /// <summary>The window the live per-program figure is read from: the last few whole minutes.</summary>
    public static UsageWindow RecentWindow(long nowMinute) => new(nowMinute - RecentSpan, RecentSpan);

    /// <summary>
    /// Where collection should start when nothing has been collected, or when
    /// what was collected is older than Windows still keeps.
    /// </summary>
    public static long Start(long? cursor, long nowMinute, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        long earliest = HourStart(nowMinute - Backfill, zone);
        long safeEnd = SafeEnd(nowMinute);

        if (cursor is not { } at || at < earliest)
        {
            return earliest;
        }

        // A clock put back leaves the cursor in the future. Carry on from now
        // rather than waiting for the clock to catch up with it.
        at = Math.Min(at, safeEnd);

        // Stopped part-way through an hour and back after Windows has rolled
        // that hour's minutes into one record, the record is stamped somewhere
        // in the hour: before the cursor its bytes would be lost, after it they
        // would be counted again on top of the minutes already kept. So the
        // whole hour is read again, replacing what was kept for its first part.
        return at < nowMinute - MinuteDetail ? HourStart(at, zone) : at;
    }

    /// <summary>
    /// The next windows to read, laid end to end from <paramref name="cursor"/>.
    /// </summary>
    public static IReadOnlyList<UsageWindow> Next(long cursor, long nowMinute, TimeZoneInfo zone, int limit = Batch)
    {
        ArgumentNullException.ThrowIfNull(zone);

        List<UsageWindow> windows = [];
        long safeEnd = SafeEnd(nowMinute);
        long detail = nowMinute - MinuteDetail;
        long at = cursor;

        while (at < safeEnd && windows.Count < limit)
        {
            if (at >= detail)
            {
                windows.Add(new UsageWindow(at, 1));
                at++;
                continue;
            }

            long end = Math.Min(Math.Min(NextHour(at, zone), detail), safeEnd);
            windows.Add(new UsageWindow(at, (int)(end - at)));
            at = end;
        }

        return windows;
    }

    /// <summary>
    /// The recent minutes to read again, never reaching back past
    /// <paramref name="minuteRunStart"/>, where the minute-wide windows begin.
    /// </summary>
    /// <remarks>
    /// Reading a minute again that was first counted inside a wider window
    /// would count its records twice; the run start is what prevents that.
    /// </remarks>
    public static IReadOnlyList<UsageWindow> Recent(long cursor, long minuteRunStart, long nowMinute)
    {
        long from = Math.Max(Math.Max(cursor - Revisit, minuteRunStart), nowMinute - MinuteDetail);
        List<UsageWindow> windows = [];

        for (long at = from; at < cursor; at++)
        {
            windows.Add(new UsageWindow(at, 1));
        }

        return windows;
    }

    /// <summary>The start of the local hour a minute falls in.</summary>
    public static long HourStart(long minute, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        TimeSpan offset = zone.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(minute * 60));
        long local = minute + (long)offset.TotalMinutes;
        long floored = local - Mod(local, 60);
        return floored - (long)offset.TotalMinutes;
    }

    /// <summary>The first minute of the next local hour after a minute.</summary>
    /// <remarks>
    /// Always later than the minute and never more than an hour on, even across
    /// a change of clocks, so a window can neither be empty nor swallow a day.
    /// </remarks>
    public static long NextHour(long minute, TimeZoneInfo zone)
    {
        long next = HourStart(minute, zone) + 60;

        if (next <= minute)
        {
            next = minute + 60 - Mod(minute, 60);
        }

        return Math.Min(next, minute + 60);
    }

    private static long Mod(long value, long divisor)
    {
        long remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }
}
