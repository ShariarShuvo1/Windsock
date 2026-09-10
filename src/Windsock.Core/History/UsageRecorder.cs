using System.Data.Common;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.Networking;
using Windsock.Core.Settings;

namespace Windsock.Core.History;

/// <summary>
/// Turns the sampler's twice-a-second readings into one history row a minute.
/// </summary>
public sealed partial class UsageRecorder : BackgroundService
{
    private readonly ThroughputMonitor _monitor;
    private readonly IUsageHistoryStore _store;
    private readonly ILogger<UsageRecorder> _logger;

    private readonly Channel<UsageMinute> _completed = Channel.CreateUnbounded<UsageMinute>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly Lock _gate = new();

    private long _minute = -1;
    private long _bytesDown;
    private long _bytesUp;
    private long _peakDown;
    private long _peakUp;
    private long _sessionStart = -1;
    private long _sessionEnd = -1;
    private bool _recording;

    public UsageRecorder(
        ThroughputMonitor monitor,
        IUsageHistoryStore store,
        WindsockSettings settings,
        ILogger<UsageRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _monitor = monitor;
        _store = store;
        _logger = logger;
        _recording = settings.RecordHistory;
    }

    /// <summary>
    /// Raised on the writer thread once minutes have been stored.
    /// </summary>
    public event EventHandler? Recorded;

    /// <summary>
    /// Raised when recording is switched on or off, whoever switched it.
    /// </summary>
    public event EventHandler? RecordingChanged;

    /// <summary>Whether measurements are being written to history.</summary>
    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _recording;
            }
        }

        set
        {
            UsageMinute? closing = null;

            lock (_gate)
            {
                if (_recording == value)
                {
                    return;
                }

                _recording = value;

                if (value)
                {
                    _sessionStart = -1;
                    _sessionEnd = -1;
                }
                else if (_minute >= 0)
                {
                    closing = Snapshot();
                    _minute = -1;
                }
            }

            if (closing is { } row)
            {
                _completed.Writer.TryWrite(row);
            }

            LogRecordingChanged(value);
            RecordingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _monitor.DeltaMeasured += OnDeltaMeasured;
        LogStarted(IsRecording);

        try
        {
            await foreach (UsageMinute minute in _completed.Reader
                .ReadAllAsync(stoppingToken)
                .ConfigureAwait(false))
            {
                List<UsageMinute> batch = [minute];

                while (_completed.Reader.TryRead(out UsageMinute queued))
                {
                    batch.Add(queued);
                }

                Persist(batch);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. The open minute is still worth keeping.
        }
        finally
        {
            _monitor.DeltaMeasured -= OnDeltaMeasured;
            FlushOnShutdown();
        }
    }

    private void OnDeltaMeasured(object? sender, ThroughputDelta delta) => Observe(delta);

    internal void Observe(ThroughputDelta delta)
    {
        long minute = EpochMinute.From(delta.Timestamp);
        UsageMinute? finished = null;

        lock (_gate)
        {
            if (!_recording)
            {
                return;
            }

            if (_minute < 0)
            {
                _minute = minute;
                _sessionStart = minute;
            }
            else if (minute != _minute)
            {
                finished = Snapshot();
                _minute = minute;
                _bytesDown = 0;
                _bytesUp = 0;
                _peakDown = 0;
                _peakUp = 0;
            }
            _bytesDown += delta.BytesReceived;
            _bytesUp += delta.BytesSent;

            if (delta.Seconds > 0)
            {
                _peakDown = Math.Max(_peakDown, (long)(delta.BytesReceived / delta.Seconds));
                _peakUp = Math.Max(_peakUp, (long)(delta.BytesSent / delta.Seconds));
            }

            _sessionEnd = minute;
        }

        if (finished is { } row)
        {
            _completed.Writer.TryWrite(row);
        }
    }

    private UsageMinute Snapshot() =>
        new(_minute, _bytesDown, _bytesUp, _peakDown, _peakUp);

    internal void FlushOnShutdown()
    {
        List<UsageMinute> remaining = [];

        while (_completed.Reader.TryRead(out UsageMinute queued))
        {
            remaining.Add(queued);
        }

        lock (_gate)
        {
            if (_minute >= 0)
            {
                remaining.Add(Snapshot());
                _minute = -1;
            }
        }

        if (remaining.Count > 0)
        {
            Persist(remaining);
        }
    }

    private void Persist(List<UsageMinute> batch)
    {
        try
        {
            _store.Write(batch);

            long start;
            long end;

            lock (_gate)
            {
                start = _sessionStart;
                end = _sessionEnd;
            }

            if (start >= 0)
            {
                _store.WriteCoverage(start, end);
            }

            Recorded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is DbException or IOException or InvalidOperationException)
        {
            LogWriteFailed(ex, batch.Count);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Usage recorder started; one history row per minute, recording is {Recording}")]
    private partial void LogStarted(bool recording);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not write {Count} minute(s) of history")]
    private partial void LogWriteFailed(Exception exception, int count);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "History recording turned {Recording}")]
    private partial void LogRecordingChanged(bool recording);
}
