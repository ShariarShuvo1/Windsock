using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Windsock.Core.Processes;

/// <summary>
/// Drives the per-process table: starts the traffic source and recalculates the
/// rows on a fixed interval, for as long as somebody is watching.
/// </summary>
public sealed partial class ProcessUsageMonitor : BackgroundService
{
    private readonly IProcessTrafficSource _traffic;
    private readonly ProcessAttributionHistory _attribution;
    private readonly ProcessUsageAggregator _aggregator;
    private readonly ILogger<ProcessUsageMonitor> _logger;
    private readonly ProcessUsageOptions _options;
    private readonly Lock _gate = new();

    private int _watchers;

    public ProcessUsageMonitor(
        IProcessTrafficSource traffic,
        IProcessConnectionSource connections,
        IProcessIdentityResolver identities,
        ProcessAttributionHistory attribution,
        IOptions<ProcessUsageOptions> options,
        ILogger<ProcessUsageMonitor> logger)
    {
        _traffic = traffic;
        _attribution = attribution;
        _logger = logger;
        _options = options.Value;
        _aggregator = new ProcessUsageAggregator(traffic, connections, identities)
        {
            Retention = _options.Retention,
        };
    }

    /// <summary>Raised on the sampling thread whenever the table is recalculated.</summary>
    public event EventHandler<IReadOnlyList<ProcessUsage>>? Updated;

    /// <summary>The most recent table contents.</summary>
    public IReadOnlyList<ProcessUsage> Latest { get; private set; } = [];

    /// <summary>Whether per-process rates are being collected, and if not, why not.</summary>
    public TrafficSourceState RateState => _traffic.State;

    /// <summary>Short reason to show when rates are unavailable.</summary>
    public string? RateDetail => _traffic.Detail;

    /// <summary>The longer version, for a tooltip.</summary>
    public string? RateExplanation => _traffic.Explanation;

    /// <summary>Raised once the traffic source has been started.</summary>
    public event EventHandler? RateStateChanged;

    /// <summary>
    /// Asks for the table to be kept up to date, until the token is let go.
    /// </summary>
    public IDisposable Watch()
    {
        lock (_gate)
        {
            _watchers++;
        }

        return new Watcher(this);
    }

    private void Release()
    {
        lock (_gate)
        {
            _watchers = Math.Max(0, _watchers - 1);
        }
    }

    /// <summary>Whether anything on screen still wants the rows.</summary>
    public bool IsWatched
    {
        get
        {
            lock (_gate)
            {
                return _watchers > 0;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _traffic.Start();
        LogStarted(_traffic.State, _options.Interval.TotalMilliseconds);
        RateStateChanged?.Invoke(this, EventArgs.Empty);

        using var timer = new PeriodicTimer(_options.Interval);
        long previous = Stopwatch.GetTimestamp();
        bool watchedLastTick = false;

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            bool watched = IsWatched;

            if (!watched)
            {
                previous = Stopwatch.GetTimestamp();
                watchedLastTick = false;
                continue;
            }

            if (!watchedLastTick)
            {
                _aggregator.Rebase();
            }

            watchedLastTick = true;

            long timestamp = Stopwatch.GetTimestamp();
            double seconds = Stopwatch.GetElapsedTime(previous, timestamp).TotalSeconds;
            previous = timestamp;

            DateTimeOffset now = DateTimeOffset.Now;
            IReadOnlyList<ProcessUsage> rows = _aggregator.Update(seconds, now);
            _attribution.Add(now, rows);

            Latest = rows;
            Updated?.Invoke(this, rows);
        }
    }

    public override void Dispose()
    {
        (_traffic as IDisposable)?.Dispose();
        base.Dispose();
    }

    private sealed class Watcher(ProcessUsageMonitor monitor) : IDisposable
    {
        private ProcessUsageMonitor? _monitor = monitor;

        public void Dispose()
        {
            _monitor?.Release();
            _monitor = null;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Process monitor started; rate collection is {State}, refreshing every {IntervalMs} ms")]
    private partial void LogStarted(TrafficSourceState state, double intervalMs);
}
