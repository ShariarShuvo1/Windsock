using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Windsock.Core.Networking;

/// <summary>
/// Samples the network interface counters on a fixed interval and turns the
/// difference between reads into a transfer rate.
/// </summary>
public sealed partial class ThroughputMonitor : BackgroundService
{
    private readonly INetworkTotalsSource _source;
    private readonly ILogger<ThroughputMonitor> _logger;
    private readonly ThroughputMonitorOptions _options;
    private volatile PeriodicTimer? _timer;
    private volatile bool _rebase;
    private TimeSpan _interval;

    public ThroughputMonitor(
        INetworkTotalsSource source,
        IOptions<ThroughputMonitorOptions> options,
        ILogger<ThroughputMonitor> logger)
    {
        _source = source;
        _logger = logger;
        _options = options.Value;
        History = new ThroughputHistory(_options.HistoryCapacity);
        _interval = Clamp(_options.Interval);
        SeedHistory();
    }

    /// <summary>Raised on the sampling thread each time a rate is computed.</summary>
    public event EventHandler<ThroughputSample>? SampleTaken;

    /// <summary>
    /// Raised on the sampling thread with the bytes counted over the interval.
    /// </summary>
    public event EventHandler<ThroughputDelta>? DeltaMeasured;

    /// <summary>Rolling in-memory history of recent samples.</summary>
    public ThroughputHistory History { get; }

    /// <summary>Gap between samples. Change it with <see cref="SetInterval"/>.</summary>
    public TimeSpan Interval => _interval;

    /// <summary>Samples the chart shows before any zooming.</summary>
    public int InitialVisibleSamples => _options.InitialVisibleSamples;

    /// <summary>Shortest interval permitted.</summary>
    public static TimeSpan MinimumInterval { get; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Longest interval permitted.</summary>
    public static TimeSpan MaximumInterval { get; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Time span the full history covers, derived from the interval and capacity
    /// so callers never have to restate it.
    /// </summary>
    public TimeSpan Window => _interval * History.Capacity;

    /// <summary>Raised after <see cref="Interval"/> changes and the history resets.</summary>
    public event EventHandler? IntervalChanged;

    /// <summary>
    /// Discards the counters the next reading would have been measured against.
    /// </summary>
    public void Rebase() => _rebase = true;

    /// <summary>
    /// Changes the sampling cadence, clamped to
    /// <see cref="MinimumInterval"/>..<see cref="MaximumInterval"/>.
    /// </summary>
    public void SetInterval(TimeSpan interval)
    {
        TimeSpan clamped = Clamp(interval);
        if (clamped == _interval)
        {
            return;
        }

        _interval = clamped;

        if (_timer is not null)
        {
            _timer.Period = clamped;
        }

        SeedHistory();
        LogIntervalChanged(clamped.TotalMilliseconds);
        IntervalChanged?.Invoke(this, EventArgs.Empty);
    }

    private static TimeSpan Clamp(TimeSpan interval) =>
        interval < MinimumInterval ? MinimumInterval
        : interval > MaximumInterval ? MaximumInterval
        : interval;

    private void SeedHistory()
    {
        History.Clear();
        History.Prefill(_options.InitialVisibleSamples, DateTimeOffset.Now, _interval);
    }

    /// <summary>The most recent sample, or <see cref="ThroughputSample.Empty"/>.</summary>
    public ThroughputSample Latest => _latest;

    private volatile Box _latest = new(ThroughputSample.Empty);

    private sealed class Box(ThroughputSample sample)
    {
        public static implicit operator ThroughputSample(Box box) => box.Sample;

        public ThroughputSample Sample { get; } = sample;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        _timer = timer;

        NetworkTotals previous = _source.ReadTotals();
        long previousTimestamp = Stopwatch.GetTimestamp();

        LogStarted(_interval.TotalMilliseconds, History.Capacity);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            NetworkTotals current = _source.ReadTotals();
            long timestamp = Stopwatch.GetTimestamp();

            if (_rebase)
            {
                _rebase = false;
                previous = current;
                previousTimestamp = timestamp;
                continue;
            }
            double seconds = Stopwatch.GetElapsedTime(previousTimestamp, timestamp).TotalSeconds;

            if (seconds > 0)
            {
                long received = Counted(current.BytesReceived - previous.BytesReceived);
                long sent = Counted(current.BytesSent - previous.BytesSent);

                DateTimeOffset now = DateTimeOffset.Now;
                var sample = new ThroughputSample(now, received / seconds, sent / seconds);

                _latest = new Box(sample);
                History.Add(sample);
                SampleTaken?.Invoke(this, sample);
                DeltaMeasured?.Invoke(this, new ThroughputDelta(now, received, sent, seconds));
            }

            previous = current;
            previousTimestamp = timestamp;
        }
    }

    private static long Counted(long delta) => delta <= 0 ? 0 : delta;
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Throughput monitor started at {IntervalMs} ms, retaining {Capacity} samples")]
    private partial void LogStarted(double intervalMs, int capacity);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sampling interval changed to {IntervalMs} ms; history reset")]
    private partial void LogIntervalChanged(double intervalMs);
}
