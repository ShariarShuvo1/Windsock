using Windsock.Core.Apps;
using Windsock.Core.Processes;

namespace Windsock.Core.History;

/// <summary>One minute Windsock measured whole, and what each app moved in it.</summary>
public sealed record MeasuredMinute(long Minute, IReadOnlyList<AppBytes> Apps);

/// <summary>
/// Turns readings of the trace's running counters into whole minutes, each
/// app's bytes in each.
/// </summary>
/// <remarks>
/// <para>
/// The trace keeps a running total for every process. Each reading, a few
/// seconds apart, is set against the one before; what a process moved in
/// between goes to its app, and to the minute the stretch between the two
/// readings lies in.
/// </para>
/// <para>
/// A minute is handed over only when it was watched from its first second to
/// its last: the first minute of a run, a minute the trace stopped in, and a
/// stretch the machine slept through are dropped whole. Windows' own figures
/// fill those, which is better than a minute with a share of its bytes missing
/// being taken for all of it.
/// </para>
/// <para>
/// A process that ended between two readings is counted from the counters it
/// ended with, and a process id handed to a new process at once is started
/// again from nothing rather than set against the one before.
/// </para>
/// </remarks>
public sealed class AppMinuteTally
{
    /// <summary>The longest gap between readings that still counts as unbroken watching.</summary>
    public static readonly TimeSpan LongestGap = TimeSpan.FromSeconds(35);

    /// <summary>How close to a minute's start a reading must be to have watched all of it.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    /// <summary>How long watching must have gone on before a minute starting then is kept.</summary>
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(60);

    private readonly Func<int, string> _appOfRunning;
    private readonly Dictionary<int, ProcessTraffic> _last = [];
    private readonly Dictionary<int, string> _apps = [];
    private readonly Dictionary<string, (long Down, long Up)> _open = new(StringComparer.Ordinal);
    private readonly HashSet<int> _ended = [];

    private DateTimeOffset? _lastAt;
    private DateTimeOffset _watchingSince;
    private long _openMinute = long.MinValue;
    private bool _openWhole;

    /// <param name="appOfRunning">
    /// What Windows counts a running process under, asked once for each
    /// process (see <see cref="ProcessIdentityResolver.ReadCountedAs"/>).
    /// </param>
    public AppMinuteTally(Func<int, string> appOfRunning)
    {
        ArgumentNullException.ThrowIfNull(appOfRunning);
        _appOfRunning = appOfRunning;
    }

    /// <summary>
    /// Takes one reading. Returns the minute finished by it, if one was, and
    /// was watched whole.
    /// </summary>
    /// <param name="at">When the reading was taken.</param>
    /// <param name="measuring">Whether the trace is attributing and history is being kept.</param>
    /// <param name="running">Every running process's counters.</param>
    /// <param name="departed">Every process that ended since the last reading.</param>
    public MeasuredMinute? Observe(
        DateTimeOffset at,
        bool measuring,
        IReadOnlyDictionary<int, ProcessTraffic> running,
        IReadOnlyList<DepartedTraffic> departed)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(departed);

        if (!measuring || _lastAt is not { } lastAt || at - lastAt > LongestGap || at <= lastAt)
        {
            Restart(measuring ? at : null, running);
            return null;
        }

        // The stretch between the readings belongs to the minute its middle is in.
        long minute = EpochMinute.From(lastAt + ((at - lastAt) / 2));
        MeasuredMinute? finished = null;

        if (minute != _openMinute)
        {
            // The reading between two minutes must be close to the edge on
            // either side of it. One late by more than the slack put that many
            // seconds of the next minute into this one, and dropped the next
            // minute, whose Windows record then counted those seconds again.
            bool onTheEdge = (lastAt - EpochMinute.ToUtc(minute)).Duration() <= Slack;

            if (_openWhole && onTheEdge && minute == _openMinute + 1)
            {
                finished = Finish();
            }

            _open.Clear();
            _openMinute = minute;

            // Watched from its first second, and not in the first minute of
            // watching: the trace hands over what it had buffered as it starts,
            // late, and on 2026-09-16 that first minute read 128% of the
            // adapter's upload where every minute after read 97-99%.
            // With the same slack as the edges: watching that began at 02:48:00.004
            // otherwise lost 02:49 by four milliseconds.
            _openWhole = onTheEdge
                && EpochMinute.ToUtc(minute) >= _watchingSince + Warmup - Slack;
        }

        _ended.Clear();

        foreach (DepartedTraffic gone in departed)
        {
            ProcessTraffic before = _last.Remove(gone.ProcessId, out ProcessTraffic was) ? was : default;
            string app = _apps.Remove(gone.ProcessId, out string? known)
                ? known
                : CountedAsImage(gone.ProcessId, gone.Image);

            Add(app, Since(gone.Final, before));
            _ended.Add(gone.ProcessId);
        }

        foreach ((int processId, ProcessTraffic now) in running)
        {
            ProcessTraffic before = !_ended.Contains(processId) && _last.TryGetValue(processId, out ProcessTraffic was)
                ? was
                : default;

            // Counters that went backwards belong to another process with the id.
            if (now.BytesSent < before.BytesSent || now.BytesReceived < before.BytesReceived)
            {
                before = default;
                _apps.Remove(processId);
            }

            if (now.BytesSent == before.BytesSent && now.BytesReceived == before.BytesReceived)
            {
                continue;
            }

            if (!_apps.TryGetValue(processId, out string? app))
            {
                app = _appOfRunning(processId);
                _apps[processId] = app;
            }

            Add(app, Since(now, before));
        }

        Remember(running);
        _lastAt = at;

        return finished;
    }

    private void Restart(DateTimeOffset? at, IReadOnlyDictionary<int, ProcessTraffic> running)
    {
        _open.Clear();
        _openMinute = long.MinValue;
        _openWhole = false;
        _lastAt = at;
        _watchingSince = at ?? default;
        _apps.Clear();

        if (at is null)
        {
            _last.Clear();
            return;
        }

        Remember(running);
    }

    private void Remember(IReadOnlyDictionary<int, ProcessTraffic> running)
    {
        _last.Clear();

        foreach ((int processId, ProcessTraffic traffic) in running)
        {
            _last[processId] = traffic;
        }

        // A process the trace no longer lists has no app to keep either.
        foreach (int processId in _apps.Keys.Where(id => !running.ContainsKey(id)).ToList())
        {
            _apps.Remove(processId);
        }
    }

    private MeasuredMinute Finish() => new(
        _openMinute,
        [.. _open.Select(entry => new AppBytes(entry.Key, entry.Value.Down, entry.Value.Up))]);

    private void Add(string app, (long Down, long Up) moved)
    {
        if (moved.Down <= 0 && moved.Up <= 0)
        {
            return;
        }

        (long down, long up) = _open.GetValueOrDefault(app);
        _open[app] = (down + moved.Down, up + moved.Up);
    }

    private static (long Down, long Up) Since(ProcessTraffic now, ProcessTraffic before) =>
        (Math.Max(0, now.BytesReceived - before.BytesReceived), Math.Max(0, now.BytesSent - before.BytesSent));

    /// <summary>
    /// What Windows counts a process under, from what the trace knew of it:
    /// the System process by that name, anything else by its file's kernel path,
    /// lower-cased as Windows writes it.
    /// </summary>
    public static string CountedAsImage(int processId, string? image) =>
        processId == SystemProcessId ? SystemId
        : string.IsNullOrWhiteSpace(image) ? AppAttribution.UnattributedId
        : image.Trim().ToLowerInvariant();

    /// <summary>
    /// What Windows counts a running process under, from what the process
    /// says of itself, then what the trace knew.
    /// </summary>
    public static string CountedAs(int processId, string? packageFamily, string? nativePath, string? image) =>
        processId == SystemProcessId ? SystemId
        : !string.IsNullOrWhiteSpace(packageFamily) ? packageFamily.Trim()
        : !string.IsNullOrWhiteSpace(nativePath) ? nativePath.Trim().ToLowerInvariant()
        : CountedAsImage(processId, image);

    /// <summary>The kernel's own process, whose traffic Windows files under System.</summary>
    private const int SystemProcessId = 4;

    private const string SystemId = "System";
}
