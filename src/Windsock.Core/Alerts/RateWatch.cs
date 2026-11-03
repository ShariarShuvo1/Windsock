using Windsock.Core.Networking;
using Windsock.Core.Settings;

namespace Windsock.Core.Alerts;

/// <summary>
/// One rate a second over a stretch, oldest first, with the questions the speed
/// alerts ask of it.
/// </summary>
public sealed class RateWindow
{
    /// <summary>A longer silence between samples than this means the PC slept, or nothing was measured.</summary>
    public static readonly TimeSpan GapLimit = TimeSpan.FromSeconds(30);

    private readonly Queue<(DateTimeOffset At, double Rate, double Seconds)> _samples = new();
    private DateTimeOffset? _since;
    private DateTimeOffset _last;

    /// <summary>How many samples are held.</summary>
    public int Count => _samples.Count;

    /// <summary>
    /// Adds a sample, dropping those older than the stretch. A gap longer than
    /// <see cref="GapLimit"/> starts the stretch again.
    /// </summary>
    public void Add(DateTimeOffset at, double rate, TimeSpan window)
    {
        if (_since is not null && (at - _last > GapLimit || at < _last))
        {
            Clear();
        }

        double seconds = _since is null ? 1 : Math.Clamp((at - _last).TotalSeconds, 0, 2);

        _since ??= at;
        _last = at;
        _samples.Enqueue((at, double.IsFinite(rate) ? Math.Max(0, rate) : 0, seconds));

        while (_samples.Count > 0 && _samples.Peek().At <= at - window)
        {
            _samples.Dequeue();
        }
    }

    /// <summary>Whether the whole stretch up to the latest sample was watched.</summary>
    public bool Covers(TimeSpan window) => _since is { } since && _last - since >= window - TimeSpan.FromSeconds(1);

    /// <summary>The rates since a moment.</summary>
    public List<double> Since(DateTimeOffset from) => [.. _samples.Where(each => each.At > from).Select(each => each.Rate)];

    /// <summary>Every rate held.</summary>
    public List<double> All() => [.. _samples.Select(each => each.Rate)];

    /// <summary>The bytes moved over the samples held: each rate over the time it covered.</summary>
    public long Bytes() => (long)_samples.Sum(each => each.Rate * each.Seconds);

    /// <summary>The latest sample's time.</summary>
    public DateTimeOffset Latest => _last;

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        _samples.Clear();
        _since = null;
    }

    /// <summary>The median of some values, or nothing for none.</summary>
    public static double? Median(IReadOnlyList<double> values) => Percentile(values, 0.5);

    /// <summary>A percentile of some values, from 0 to 1, by the nearest rank; nothing for none.</summary>
    public static double? Percentile(IReadOnlyList<double> values, double share)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return null;
        }

        double[] sorted = [.. values];
        Array.Sort(sorted);

        if (Math.Abs(share - 0.5) < 1e-9)
        {
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        }

        int rank = (int)Math.Ceiling(Math.Clamp(share, 0, 1) * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}

/// <summary>A download that had been faster stayed slow for the whole stretch.</summary>
/// <param name="TypicalBytesPerSecond">The median download rate while it was downloading.</param>
/// <param name="Window">The stretch.</param>
/// <param name="Received">What came in over it.</param>
/// <param name="PeakBytesPerSecond">The fastest it went earlier in the same download.</param>
public sealed record SpeedDropAlarm(double TypicalBytesPerSecond, TimeSpan Window, long Received, double PeakBytesPerSecond);

/// <summary>
/// Watches for a download that drops below a level and stays there for
/// several minutes.
/// </summary>
/// <remarks>
/// <para>
/// "Something is downloading" is the hard part, because an idle line is also
/// below any level. So the line must be busy for the whole stretch: at least
/// <see cref="BusyShare"/> of its seconds moving at least
/// <see cref="ActivityFloor"/> (64 KB/s, or half the level when the level is
/// lower). An idle line, with its occasional burst, is below that floor most of
/// the time and never alerts; so is a video stream that fetches in bursts with
/// silence between. A download that finishes falls below the floor too.
/// </para>
/// <para>
/// Busy and below the level is still not enough: a video call or a stream that
/// sends steadily at 300 KB/s is exactly that, for as long as it lasts. So it
/// must be a <em>drop</em>: the same busy run - no more than
/// <see cref="RunBreak"/> of idle inside it - must have reached the level
/// before the slow stretch began. A download that was never faster is not one
/// that slowed down, and a call that a fast download follows is not either.
/// </para>
/// <para>
/// Slow means that while busy it stayed below the level nearly all the time:
/// nine seconds in ten under it. Once fired, it waits until the download ends
/// (a minute mostly idle) or recovers (a minute whose median reaches the level)
/// before it can fire again, and then needs a whole new stretch, so one long
/// slow download is one alert.
/// </para>
/// </remarks>
public sealed class SpeedDropWatch
{
    /// <summary>The share of a stretch's seconds that must be busy for it to count as downloading.</summary>
    public const double BusyShare = 0.9;

    /// <summary>The rate, in bytes a second, below which the line counts as idle.</summary>
    public const double ActivityFloor = 64 * 1024;

    /// <summary>How long recovery or the end of the download is judged over.</summary>
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromMinutes(1);

    /// <summary>How long idle ends a busy run: the download is over, and a new one starts afresh.</summary>
    public static readonly TimeSpan RunBreak = TimeSpan.FromSeconds(30);

    private readonly RateWindow _window = new();
    private DateTimeOffset? _idleSince;
    private DateTimeOffset _last;
    private double? _runPeak;
    private DateTimeOffset? _runFirstFast;

    /// <summary>Whether the alert has fired and not yet been re-armed.</summary>
    public bool HasFired { get; private set; }

    /// <summary>The idle floor for a level: 64 KB/s, or half the level when that is lower.</summary>
    public static double FloorFor(long level) => Math.Min(ActivityFloor, level / 2.0);

    /// <summary>Takes one sample, and says when the alert should fire.</summary>
    public SpeedDropAlarm? Observe(in ThroughputSample sample, SpeedDropSettings rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!rule.Enabled || rule.BelowBytesPerSecond <= 0)
        {
            Reset();
            HasFired = false;
            return null;
        }

        TimeSpan window = rule.Window;
        double level = rule.BelowBytesPerSecond;
        double floor = FloorFor(rule.BelowBytesPerSecond);
        double rate = double.IsFinite(sample.DownloadBytesPerSecond) ? Math.Max(0, sample.DownloadBytesPerSecond) : 0;

        // A silence between samples - asleep, or not measured - ends the run.
        if (sample.Timestamp - _last > RateWindow.GapLimit || sample.Timestamp < _last)
        {
            _runPeak = null;
            _runFirstFast = null;
            _idleSince = null;
        }

        _last = sample.Timestamp;
        _window.Add(sample.Timestamp, rate, window);

        if (rate >= floor)
        {
            _idleSince = null;

            // The run's best so far: what it reached, if it ever reached the level.
            if (rate >= level)
            {
                _runPeak = Math.Max(_runPeak ?? 0, rate);
                _runFirstFast ??= sample.Timestamp;
            }
        }
        else
        {
            _idleSince ??= sample.Timestamp;

            if (sample.Timestamp - _idleSince >= RunBreak)
            {
                _runPeak = null;
                _runFirstFast = null;
            }
        }

        if (HasFired)
        {
            List<double> recent = _window.Since(sample.Timestamp - RecoveryWindow);

            if (recent.Count >= RecoveryWindow.TotalSeconds * 0.5
                && (recent.Count(each => each < floor) >= recent.Count * 0.8 || RateWindow.Median(recent) >= level))
            {
                // Re-armed, the stretch starts again: another alert needs a whole new one.
                HasFired = false;
                _window.Clear();
            }

            return null;
        }

        // The drop comes after the speed: the run must have reached the level
        // before the slow stretch began, give or take its first tenth.
        if (_runPeak is not { } peak
            || _runFirstFast is not { } firstFast
            || firstFast > sample.Timestamp - (window * 0.9)
            || !_window.Covers(window))
        {
            return null;
        }

        List<double> all = _window.All();
        List<double> busy = [.. all.Where(each => each >= floor)];

        if (busy.Count < all.Count * BusyShare)
        {
            return null;
        }

        if (RateWindow.Percentile(busy, 0.9) is not { } high || high >= level)
        {
            return null;
        }

        HasFired = true;
        return new SpeedDropAlarm(RateWindow.Median(busy) ?? 0, window, _window.Bytes(), peak);
    }

    /// <summary>Starts again, as after waking; a fired alert stays fired.</summary>
    public void Reset()
    {
        _window.Clear();
        _runPeak = null;
        _runFirstFast = null;
        _idleSince = null;
    }
}

/// <summary>An upload stayed high for the whole stretch.</summary>
/// <param name="TypicalBytesPerSecond">The median upload rate over it.</param>
/// <param name="Window">The stretch.</param>
/// <param name="Sent">What went out over it.</param>
public sealed record UploadAlarm(double TypicalBytesPerSecond, TimeSpan Window, long Sent);

/// <summary>
/// Watches for an upload that stays above a level for a whole stretch: a
/// runaway backup or sync, or something worse.
/// </summary>
/// <remarks>
/// Sustained means the median over the stretch reaches the level - more than
/// half its seconds - so a burst, however large, never fires it. Once fired it
/// is re-armed when the median over the last two minutes falls below half the
/// level, and the stretch starts again, so one long upload is one alert and the
/// next takes a whole stretch of its own.
/// </remarks>
public sealed class UploadWatch
{
    /// <summary>How long the fall back to normal is judged over.</summary>
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromMinutes(2);

    private readonly RateWindow _window = new();

    /// <summary>Whether the alert has fired and not yet been re-armed.</summary>
    public bool HasFired { get; private set; }

    /// <summary>Takes one sample, and says when the alert should fire.</summary>
    public UploadAlarm? Observe(in ThroughputSample sample, UploadAlertSettings rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!rule.Enabled || rule.AboveBytesPerSecond <= 0)
        {
            _window.Clear();
            HasFired = false;
            return null;
        }

        TimeSpan window = rule.Window;
        _window.Add(sample.Timestamp, sample.UploadBytesPerSecond, window);

        if (HasFired)
        {
            List<double> recent = _window.Since(sample.Timestamp - RecoveryWindow);

            if (recent.Count >= RecoveryWindow.TotalSeconds * 0.5 && RateWindow.Median(recent) < rule.AboveBytesPerSecond / 2.0)
            {
                // Re-armed, the stretch starts again: another alert needs a whole new one.
                HasFired = false;
                _window.Clear();
            }

            return null;
        }

        if (!_window.Covers(window) || RateWindow.Median(_window.All()) is not { } typical || typical < rule.AboveBytesPerSecond)
        {
            return null;
        }

        HasFired = true;
        return new UploadAlarm(typical, window, _window.Bytes());
    }

    /// <summary>Starts again, as after waking; a fired alert stays fired.</summary>
    public void Reset() => _window.Clear();
}
