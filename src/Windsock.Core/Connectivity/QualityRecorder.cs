using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.History;
using Windsock.Core.Settings;

namespace Windsock.Core.Connectivity;

/// <summary>
/// Adds each connection check into quarter-hour sums in the connection log,
/// so a report can give jitter and loss for any period, not only the last hour
/// the monitor holds.
/// </summary>
/// <remarks>
/// It keeps only what the checks already measured: nothing extra is sent, and
/// nothing is kept while nobody watches the connection. Sums are written once
/// a minute and when Windsock stops. Two checks are consecutive for jitter
/// only when no more than three check intervals apart, so a gap while nobody
/// watched does not count as variation.
/// </remarks>
public sealed partial class QualityRecorder : IHostedService, IDisposable
{
    private static readonly TimeSpan FlushEvery = TimeSpan.FromMinutes(1);

    private readonly ConnectionMonitor _monitor;
    private readonly IQualityStore _store;
    private readonly WindsockSettings _settings;
    private readonly ILogger<QualityRecorder> _logger;
    private readonly Lock _gate = new();
    private readonly QualityTally _tally = new();
    private Timer? _flush;
    private bool _failed;

    public QualityRecorder(ConnectionMonitor monitor, IQualityStore store, WindsockSettings settings, ILogger<QualityRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);

        _monitor = monitor;
        _store = store;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _monitor.Sampled += OnSampled;
        _flush = new Timer(_ => Flush(), null, FlushEvery, FlushEvery);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _monitor.Sampled -= OnSampled;
        _flush?.Dispose();
        _flush = null;
        Flush();
        return Task.CompletedTask;
    }

    private void OnSampled(object? sender, ConnectionSample sample)
    {
        lock (_gate)
        {
            TimeSpan gap = _settings.Connection.CheckEvery * 3;
            _tally.Add(sample, _monitor.State.IsOffline, gap < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : gap);
        }
    }

    private void Flush()
    {
        IReadOnlyList<QualityQuarter> quarters;

        lock (_gate)
        {
            quarters = _tally.Take();
        }

        if (quarters.Count == 0)
        {
            return;
        }

        try
        {
            _store.AddQuality(quarters);
            _failed = false;
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            if (!_failed)
            {
                _failed = true;
                LogStoreFailed(ex);
            }
        }
    }

    public void Dispose() => _flush?.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection quality could not be written to the connection log")]
    private partial void LogStoreFailed(Exception error);
}

/// <summary>Turns connection checks into quarter-hour sums. Pure, so it can be tested a check at a time.</summary>
public sealed class QualityTally
{
    private readonly Dictionary<(DateTimeOffset Quarter, CheckTarget Target), Sums> _open = [];
    private readonly Dictionary<CheckTarget, (DateTimeOffset At, double Ms)> _last = [];

    /// <summary>Adds one check.</summary>
    /// <param name="sample">The check.</param>
    /// <param name="offline">Whether an outage was in progress: checks then measure the outage, not the line.</param>
    /// <param name="longestGap">The longest gap between two answers that still counts them as consecutive.</param>
    public void Add(in ConnectionSample sample, bool offline, TimeSpan longestGap)
    {
        if (offline)
        {
            _last.Clear();
            return;
        }

        DateTimeOffset quarter = QualityQuarter.QuarterOf(sample.At);

        if (sample.RouterChecked)
        {
            Count(quarter, CheckTarget.Router, sample.At, sample.Router, longestGap);
        }

        if (sample.InternetChecked)
        {
            Count(quarter, CheckTarget.Internet, sample.At, sample.Internet, longestGap);
        }
    }

    /// <summary>Hands over everything counted since the last time, and starts again.</summary>
    public IReadOnlyList<QualityQuarter> Take()
    {
        List<QualityQuarter> taken = [.. _open.Select(pair => new QualityQuarter(
            pair.Key.Quarter,
            pair.Key.Target,
            pair.Value.Sent,
            pair.Value.Answered,
            pair.Value.SumMs,
            pair.Value.WorstMs,
            pair.Value.JitterSumMs,
            pair.Value.JitterPairs))];

        _open.Clear();
        return taken;
    }

    private void Count(DateTimeOffset quarter, CheckTarget target, DateTimeOffset at, TimeSpan? answer, TimeSpan longestGap)
    {
        if (!_open.TryGetValue((quarter, target), out Sums? sums))
        {
            sums = new Sums();
            _open[(quarter, target)] = sums;
        }

        sums.Sent++;

        if (answer is not { } took)
        {
            return;
        }

        double ms = took.TotalMilliseconds;
        sums.Answered++;
        sums.SumMs += ms;
        sums.WorstMs = Math.Max(sums.WorstMs, ms);

        if (_last.TryGetValue(target, out (DateTimeOffset At, double Ms) previous) && at - previous.At <= longestGap && at >= previous.At)
        {
            sums.JitterSumMs += Math.Abs(ms - previous.Ms);
            sums.JitterPairs++;
        }

        _last[target] = (at, ms);
    }

    private sealed class Sums
    {
        public int Sent { get; set; }

        public int Answered { get; set; }

        public double SumMs { get; set; }

        public double WorstMs { get; set; }

        public double JitterSumMs { get; set; }

        public int JitterPairs { get; set; }
    }
}
