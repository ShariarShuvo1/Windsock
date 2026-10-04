namespace Windsock.Core.Apps;

/// <summary>
/// How far app usage has been collected, and where the unbroken run of
/// minute-wide windows leading up to that point begins.
/// </summary>
/// <param name="Cursor">The first minute not yet collected, or null before the first collection.</param>
/// <param name="MinuteRunStart">
/// The first minute of the minute-wide windows ending at the cursor. Minutes
/// before it were counted inside wider windows and must not be read again.
/// </param>
/// <param name="MissingSince">
/// The earliest minute stored without a network Windows refused, which is read
/// again once that network answers; null when nothing is missing.
/// </param>
public readonly record struct AppUsagePosition(long? Cursor, long MinuteRunStart, long? MissingSince = null);

/// <summary>Keeps each app's use of the network, as Windows counted it.</summary>
public interface IAppUsageStore
{
    /// <summary>How far collection has got.</summary>
    AppUsagePosition ReadAppUsagePosition();

    /// <summary>
    /// Stores what was read and how far that takes collection, together, so the
    /// two can never disagree after a crash.
    /// </summary>
    /// <param name="windows">Every window that was read, whether or not anything was in it.</param>
    /// <param name="slices">What each app used in those windows.</param>
    /// <param name="position">How far collection now stands.</param>
    /// <remarks>
    /// A window read again replaces what was kept for it, every app of it,
    /// including apps that are no longer in it. Windows moves a record between
    /// minutes after publishing it (see <see cref="AppUsagePlan"/>), so keeping
    /// the larger of two readings would count a record that moved once in each
    /// minute; replacing counts it where Windows says it is now.
    /// <para>
    /// A window that touches a period erased since collection began is not
    /// stored at all, so an erase cannot be undone by a pass that was already
    /// reading, or by minutes read again.
    /// </para>
    /// </remarks>
    void WriteAppUsage(IReadOnlyList<UsageWindow> windows, IReadOnlyList<AppUsageSlice> slices, AppUsagePosition position);

    /// <summary>
    /// Moves collection past time that is not to be kept - history recording
    /// was off - and remembers it as such, together, so nothing that reads the
    /// time again later (an hour re-read after a gap, a network that answered
    /// again) can keep it after all.
    /// </summary>
    /// <param name="fromMinute">The first minute not to be kept.</param>
    /// <param name="position">Where collection stands afterwards; its cursor ends the span.</param>
    void PassOver(long fromMinute, AppUsagePosition position);

    /// <summary>Each app's use across windows starting within the period.</summary>
    IReadOnlyList<AppUsageTotal> ReadAppTotals(DateTimeOffset from, DateTimeOffset until);

    /// <summary>Every stored window starting within the period, app by app.</summary>
    IReadOnlyList<AppUsageSlice> ReadAppSlices(long fromMinute, long untilMinute);

    /// <summary>
    /// What Windows counted for the machine in windows wholly within the period
    /// that hold no minute Windsock itself recorded: the time Windsock was
    /// closed, as Windows saw it.
    /// </summary>
    IReadOnlyList<UsageSpan> ReadMissedSpans(DateTimeOffset from, DateTimeOffset until);
}
