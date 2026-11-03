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
    private readonly INetworkTagSource? _networks;
    private readonly ILogger<UsageRecorder> _logger;
    private readonly WriteFailureLog _failures = new();

    // The networks the open minute's bytes went over. Nearly always one; two
    // when the PC moved network inside the minute.
    private readonly List<(NetworkTag? Network, long Bytes)> _carriers = [];

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
        : this(monitor, store, settings, networks: null, logger)
    {
    }

    /// <summary>A recorder that tags each minute with the network it was measured on.</summary>
    public UsageRecorder(
        ThroughputMonitor monitor,
        IUsageHistoryStore store,
        WindsockSettings settings,
        INetworkTagSource? networks,
        ILogger<UsageRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _monitor = monitor;
        _store = store;
        _networks = networks;
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
                    Reset(-1);
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
        NetworkTag? network = _networks?.Current;
        UsageMinute? finished = null;

        lock (_gate)
        {
            if (!_recording)
            {
                return;
            }

            if (_minute < 0)
            {
                Reset(minute);
                _sessionStart = minute;
            }
            else if (minute != _minute)
            {
                finished = Snapshot();
                Reset(minute);
            }
            _bytesDown += delta.BytesReceived;
            _bytesUp += delta.BytesSent;
            Carry(network, delta.BytesReceived + delta.BytesSent);

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
        new(_minute, _bytesDown, _bytesUp, _peakDown, _peakUp) { Network = Carrier() };

    /// <summary>Counts bytes against the network they went over. Call with the gate held.</summary>
    private void Carry(NetworkTag? network, long bytes)
    {
        for (int each = 0; each < _carriers.Count; each++)
        {
            if (string.Equals(_carriers[each].Network?.Key, network?.Key, StringComparison.Ordinal))
            {
                // The latest word on the network, its name or cost, wins.
                _carriers[each] = (network, _carriers[each].Bytes + Math.Max(0, bytes));
                return;
            }
        }

        _carriers.Add((network, Math.Max(0, bytes)));
    }

    /// <summary>
    /// The network the open minute is kept under: the one that carried most of
    /// its bytes, the later on a tie, so a minute where nothing moved is the
    /// network it ended on. Null when that is not known: a minute is never
    /// put down to a network it may not have been on. Call with the gate held.
    /// </summary>
    private NetworkTag? Carrier()
    {
        NetworkTag? chosen = null;
        long most = -1;

        foreach ((NetworkTag? network, long bytes) in _carriers)
        {
            if (bytes >= most)
            {
                most = bytes;
                chosen = network;
            }
        }

        return chosen;
    }

    /// <summary>
    /// Moves on to a minute with nothing counted in it yet, or to no minute at
    /// all when given -1. Call with the gate held.
    /// </summary>
    /// <remarks>
    /// The counters go with the minute. Closing one by setting it to -1 alone
    /// left its bytes behind, and the next minute to open added to them, so
    /// the first minute after a pause repeated the one written before it.
    /// </remarks>
    private void Reset(long minute)
    {
        _minute = minute;
        _bytesDown = 0;
        _bytesUp = 0;
        _peakDown = 0;
        _peakUp = 0;
        _carriers.Clear();
    }

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
                Reset(-1);
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
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            // These minutes are lost; the next ones are still measured and tried.
            if (_failures.ShouldLog(ex))
            {
                LogWriteFailed(ex, batch.Count);
            }
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
