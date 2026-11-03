using Windsock.Core.History;
using Windsock.Core.Networking;

namespace Windsock.Core.Reports;

/// <summary>What a stretch dragged across a chart holds.</summary>
/// <param name="From">The start of the first bucket taken.</param>
/// <param name="Until">The end of the last bucket taken.</param>
/// <param name="Down">Bytes received, as Windsock recorded them.</param>
/// <param name="Up">Bytes sent, as Windsock recorded them.</param>
/// <param name="EstimatedDown">Bytes received while Windsock was closed, as Windows counted them.</param>
/// <param name="EstimatedUp">Bytes sent in that time, as Windows counted them.</param>
/// <param name="RecordedMinutes">Minutes Windsock recorded in it.</param>
/// <param name="PeakDown">The fastest second in, bytes per second.</param>
/// <param name="PeakUp">The fastest second out.</param>
/// <param name="Buckets">How many buckets it took in.</param>
/// <param name="LocalDown">The part of <paramref name="Down"/> known to have stayed on the local network.</param>
/// <param name="LocalUp">The part of <paramref name="Up"/> known to have stayed on the local network.</param>
public readonly record struct MeasuredSpan(
    DateTimeOffset From,
    DateTimeOffset Until,
    long Down,
    long Up,
    long EstimatedDown,
    long EstimatedUp,
    long RecordedMinutes,
    long PeakDown,
    long PeakUp,
    int Buckets,
    long LocalDown = 0,
    long LocalUp = 0)
{
    /// <summary>Everything less what is known to have stayed on the local network.</summary>
    public long Internet => Math.Max(0, All - LocalDown - LocalUp);

    /// <summary>How long it lasts.</summary>
    public TimeSpan Duration => Until - From;

    /// <summary>Everything moved, whoever counted it.</summary>
    public long All => Down + Up + EstimatedDown + EstimatedUp;

    /// <summary>The average rate over the minutes Windsock recorded, in and out together, bytes per second.</summary>
    public double AverageRate => RecordedMinutes > 0 ? (Down + Up) / (RecordedMinutes * 60.0) : 0;
}

/// <summary>Totals the bytes in any stretch of a chart.</summary>
public static class UsageMeasure
{
    /// <summary>
    /// The buckets a dragged stretch covers - every bucket it touches, since a
    /// bucket cannot be cut - added up.
    /// </summary>
    /// <param name="buckets">The chart's buckets, in any order.</param>
    /// <param name="from">One end of the drag.</param>
    /// <param name="until">The other end; the two may come either way round.</param>
    /// <param name="width">How long a bucket lasts, where the chart knows; a bucket's end is the next one's start otherwise.</param>
    public static MeasuredSpan Of(IReadOnlyList<UsageBucket> buckets, DateTimeOffset from, DateTimeOffset until, TimeSpan? width = null)
    {
        ArgumentNullException.ThrowIfNull(buckets);

        if (until < from)
        {
            (from, until) = (until, from);
        }

        // A click rather than a drag takes the bucket it lands in.
        if (until == from)
        {
            until = from.AddTicks(1);
        }

        List<UsageBucket> sorted = [.. buckets.OrderBy(bucket => bucket.Start)];
        long down = 0, up = 0, estimatedDown = 0, estimatedUp = 0, minutes = 0, peakDown = 0, peakUp = 0, localDown = 0, localUp = 0;
        int taken = 0;
        DateTimeOffset? first = null;
        DateTimeOffset last = from;

        for (int each = 0; each < sorted.Count; each++)
        {
            UsageBucket bucket = sorted[each];
            DateTimeOffset end = width is { } span
                ? bucket.Start + span
                : each + 1 < sorted.Count ? sorted[each + 1].Start : bucket.Start + TimeSpan.FromMinutes(Math.Max(1, bucket.Minutes + bucket.EstimatedMinutes));

            if (end <= from || bucket.Start >= until)
            {
                continue;
            }

            first ??= bucket.Start;
            last = end > last ? end : last;
            down += bucket.BytesDown;
            up += bucket.BytesUp;
            estimatedDown += bucket.EstimatedDown;
            estimatedUp += bucket.EstimatedUp;
            minutes += bucket.Minutes;
            localDown += bucket.LocalDown;
            localUp += bucket.LocalUp;
            peakDown = Math.Max(peakDown, bucket.PeakDown);
            peakUp = Math.Max(peakUp, bucket.PeakUp);
            taken++;
        }

        return new MeasuredSpan(first ?? from, first is null ? from : last, down, up, estimatedDown, estimatedUp, minutes, peakDown, peakUp, taken, localDown, localUp);
    }
}

/// <summary>
/// Times a download: started and stopped by hand, it adds up what the meter
/// measured in between.
/// </summary>
/// <remarks>
/// Fed the meter's own readings (<see cref="ThroughputMonitor.DeltaMeasured"/>),
/// so it counts what the adapters counted, on every app, as the meter does. It
/// is not history: nothing is kept, and it measures whether or not recording is on.
/// </remarks>
public sealed class DownloadStopwatch : IDisposable
{
    private readonly ThroughputMonitor? _monitor;
    private readonly Lock _gate = new();
    private DateTimeOffset? _started;
    private DateTimeOffset? _stopped;
    private DateTimeOffset _last;
    private long _down;
    private long _up;
    private double _peak;

    /// <summary>A stopwatch fed by hand, through <see cref="Observe"/>.</summary>
    public DownloadStopwatch()
    {
    }

    /// <summary>A stopwatch fed by the meter.</summary>
    public DownloadStopwatch(ThroughputMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitor = monitor;
        _monitor.DeltaMeasured += OnDelta;
    }

    /// <summary>Whether it is running.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _started is not null && _stopped is null;
            }
        }
    }

    /// <summary>Starts it afresh.</summary>
    public void Start(DateTimeOffset now)
    {
        lock (_gate)
        {
            _started = now;
            _stopped = null;
            _last = now;
            _down = 0;
            _up = 0;
            _peak = 0;
        }
    }

    /// <summary>Stops it; what it holds stays until it is started again.</summary>
    public void Stop(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_started is not null && _stopped is null)
            {
                _stopped = now;
            }
        }
    }

    /// <summary>Takes in one of the meter's readings.</summary>
    public void Observe(ThroughputDelta delta)
    {
        lock (_gate)
        {
            if (_started is not { } started || _stopped is not null || delta.Timestamp <= started)
            {
                return;
            }

            _down += Math.Max(0, delta.BytesReceived);
            _up += Math.Max(0, delta.BytesSent);
            _last = delta.Timestamp;

            if (delta.Seconds > 0)
            {
                _peak = Math.Max(_peak, delta.BytesReceived / delta.Seconds);
            }
        }
    }

    /// <summary>What it has counted, as of a moment.</summary>
    public StopwatchReading Read(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_started is not { } started)
            {
                return default;
            }

            DateTimeOffset end = _stopped ?? now;
            TimeSpan elapsed = end > started ? end - started : TimeSpan.Zero;
            return new StopwatchReading(elapsed, _down, _up, _peak, _stopped is null);
        }
    }

    private void OnDelta(object? sender, ThroughputDelta delta) => Observe(delta);

    public void Dispose()
    {
        if (_monitor is not null)
        {
            _monitor.DeltaMeasured -= OnDelta;
        }
    }
}

/// <summary>What a stopwatch has counted.</summary>
/// <param name="Elapsed">How long it has run.</param>
/// <param name="Down">Bytes received.</param>
/// <param name="Up">Bytes sent.</param>
/// <param name="PeakDown">The fastest download reading, bytes per second.</param>
/// <param name="Running">Whether it is still running.</param>
public readonly record struct StopwatchReading(TimeSpan Elapsed, long Down, long Up, double PeakDown, bool Running)
{
    /// <summary>The average download rate, bytes per second.</summary>
    public double AverageDown => Elapsed > TimeSpan.Zero ? Down / Elapsed.TotalSeconds : 0;
}
