using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.Connectivity;
using Windsock.Core.Hardware;

namespace Windsock.Core.History;

/// <summary>
/// Keeps the machine's readings and the connection's checks a minute at a
/// time, alongside network use.
/// </summary>
/// <remarks>
/// <para>
/// The machine is read quietly for this - every five seconds while nothing on
/// screen asks for more (<see cref="SystemMonitor.WatchQuietly"/>) - and only
/// while history is being recorded. The connection is not checked for it at
/// all: its checks run while its section, or its alerts, want them, and what
/// they find is kept.
/// </para>
/// <para>
/// A minute is written as soon as a reading lands in the next one. One cut off
/// by closing or pausing is written as far as it went; a minute written in two
/// goes adds up, since the store keeps sums.
/// </para>
/// </remarks>
public sealed partial class MachineHistoryRecorder : BackgroundService
{
    private readonly SystemMonitor _system;
    private readonly ConnectionMonitor _connection;
    private readonly IMachineHistoryStore _store;
    private readonly UsageRecorder _recorder;
    private readonly ILogger<MachineHistoryRecorder> _logger;
    private readonly Lock _gate = new();
    private readonly SystemMinuteTally _systemTally = new();
    private readonly ConnectionMinuteTally _connectionTally = new();
    private readonly Channel<object> _finished = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private IDisposable? _watch;

    public MachineHistoryRecorder(
        SystemMonitor system,
        ConnectionMonitor connection,
        IMachineHistoryStore store,
        UsageRecorder recorder,
        ILogger<MachineHistoryRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(logger);

        _system = system;
        _connection = connection;
        _store = store;
        _recorder = recorder;
        _logger = logger;
    }

    /// <summary>Raised on the writer's thread once minutes have been kept.</summary>
    public event EventHandler? Recorded;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _system.Sampled += OnSystemSampled;
        _connection.Sampled += OnConnectionSampled;
        _recorder.RecordingChanged += OnRecordingChanged;
        Follow();

        try
        {
            await foreach (object minute in _finished.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                List<object> batch = [minute];

                while (_finished.Reader.TryRead(out object? more))
                {
                    batch.Add(more);
                }

                Persist(batch);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown: what the open minutes hold is still worth keeping.
        }
        finally
        {
            _system.Sampled -= OnSystemSampled;
            _connection.Sampled -= OnConnectionSampled;
            _recorder.RecordingChanged -= OnRecordingChanged;

            lock (_gate)
            {
                _watch?.Dispose();
                _watch = null;
            }

            List<object> rest = [];

            while (_finished.Reader.TryRead(out object? queued))
            {
                rest.Add(queued);
            }

            rest.AddRange(TakeOpen());
            Persist(rest);
        }
    }

    private void OnRecordingChanged(object? sender, EventArgs e) => Follow();

    /// <summary>Reads the machine quietly while recording, and not at all otherwise.</summary>
    private void Follow()
    {
        List<object> cut = [];

        lock (_gate)
        {
            if (_recorder.IsRecording)
            {
                _watch ??= _system.WatchQuietly();
                return;
            }

            _watch?.Dispose();
            _watch = null;
            cut.AddRange(TakeOpenHeld());
        }

        foreach (object minute in cut)
        {
            _finished.Writer.TryWrite(minute);
        }
    }

    private List<object> TakeOpen()
    {
        lock (_gate)
        {
            return TakeOpenHeld();
        }
    }

    /// <summary>The open minutes, taken. Call with the gate held.</summary>
    private List<object> TakeOpenHeld()
    {
        List<object> open = [];

        if (_systemTally.Take() is { } system)
        {
            open.Add(system);
        }

        if (_connectionTally.Take() is { } connection)
        {
            open.Add(connection);
        }

        _systemTally.Break();
        return open;
    }

    private void OnSystemSampled(object? sender, SystemSnapshot snapshot)
    {
        if (!_recorder.IsRecording)
        {
            return;
        }

        SystemMinute? finished;

        lock (_gate)
        {
            finished = _systemTally.Add(snapshot);
        }

        if (finished is { } minute)
        {
            _finished.Writer.TryWrite(minute);
        }
    }

    private void OnConnectionSampled(object? sender, ConnectionSample sample)
    {
        if (!_recorder.IsRecording)
        {
            return;
        }

        ConnectionMinute? finished;

        lock (_gate)
        {
            finished = _connectionTally.Add(sample, _connection.State.IsOffline);
        }

        if (finished is { } minute)
        {
            _finished.Writer.TryWrite(minute);
        }
    }

    private void Persist(List<object> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            _store.WriteSystem([.. batch.OfType<SystemMinute>()]);
            _store.WriteConnection([.. batch.OfType<ConnectionMinute>()]);
            Recorded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogStoreFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not keep the machine's or the connection's minutes")]
    private partial void LogStoreFailed(Exception exception);
}
