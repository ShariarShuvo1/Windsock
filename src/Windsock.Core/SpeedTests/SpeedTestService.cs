using Microsoft.Extensions.Logging;
using Windsock.Core.Connectivity;
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Settings;
using Windsock.Core.Wifi;

namespace Windsock.Core.SpeedTests;

/// <summary>Where a speed test is.</summary>
public enum SpeedTestPhase
{
    Idle,

    /// <summary>Finding a server.</summary>
    Locating,

    /// <summary>Timing the quiet line.</summary>
    QuietLatency,

    Download,

    Upload,

    Done,

    Failed,
}

/// <summary>Why a speed test will not start.</summary>
public enum SpeedTestRefusal
{
    /// <summary>It can start.</summary>
    None,

    /// <summary>M-Lab is chosen and the person has not agreed to its publishing the address.</summary>
    NotAgreed,

    /// <summary>LibreSpeed is chosen and no server address is set.</summary>
    NoServer,

    /// <summary>A test is already running.</summary>
    Running,

    /// <summary>The internet is down.</summary>
    Offline,

    /// <summary>A Wi-Fi scan is running, which would slow the test and be slowed by it.</summary>
    Scanning,
}

/// <summary>A test's progress, for the page.</summary>
/// <param name="Phase">Where it is.</param>
/// <param name="BitsPerSecond">The speed so far in the current direction.</param>
/// <param name="LatencyMs">The latest response time measured.</param>
/// <param name="Server">The server, once known.</param>
/// <param name="Scheduled">Whether the schedule started it rather than the person.</param>
public sealed record SpeedTestProgress(SpeedTestPhase Phase, double? BitsPerSecond, double? LatencyMs, string? Server, bool Scheduled = false)
{
    /// <summary>Nothing running.</summary>
    public static SpeedTestProgress Idle { get; } = new(SpeedTestPhase.Idle, null, null, null);

    /// <summary>Whether a test is under way: any phase but idle, done or failed.</summary>
    public bool IsActive => Phase is not (SpeedTestPhase.Idle or SpeedTestPhase.Done or SpeedTestPhase.Failed);
}

/// <summary>
/// Runs a speed test - download, upload, and response time quiet and under
/// load - against M-Lab or the person's LibreSpeed server, and keeps every
/// result.
/// </summary>
/// <remarks>
/// <para>
/// Only one test at a time, never without the switch for its server: M-Lab
/// needs the person's agreement that it publishes this PC's public address
/// with every result, for good; LibreSpeed needs the server's address. The
/// page names the server before the button does anything.
/// </para>
/// <para>
/// Response time under load is measured the best way available: pings to the
/// connection's chosen internet address when there is one (it is already
/// pinged every few seconds, so this adds no new destination); otherwise the
/// NDT7 server's own TCP round-trip readings, or timed empty requests to the
/// LibreSpeed server. Pings to the router would not do: the queue that causes
/// bufferbloat is on the router's far side.
/// </para>
/// </remarks>
public sealed partial class SpeedTestService : IDisposable
{
    private static readonly TimeSpan QuietGap = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan LoadedGap = TimeSpan.FromMilliseconds(400);
    private const int QuietSamples = 10;

    private readonly WindsockSettings _settings;
    private readonly ISpeedTestStore _store;
    private readonly ConnectionMonitor _connection;
    private readonly INetworkPathProbe _probe;
    private readonly TimeProvider _time;
    private readonly ILogger<SpeedTestService> _logger;
    private readonly HttpClient _http;
    private readonly WifiScanner? _scanner;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Lock _gate = new();
    private CancellationTokenSource? _current;
    private bool _currentScheduled;

    public SpeedTestService(
        WindsockSettings settings,
        ISpeedTestStore store,
        ConnectionMonitor connection,
        INetworkPathProbe probe,
        TimeProvider time,
        ILogger<SpeedTestService> logger,
        HttpMessageHandler? handler = null,
        WifiScanner? scanner = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(probe);

        _settings = settings;
        _store = store;
        _connection = connection;
        _probe = probe;
        _time = time;
        _logger = logger;
        _scanner = scanner;
        _http = Lookups.WindsockAgent.Client(TimeSpan.FromSeconds(30), handler);
    }

    /// <summary>Raised, on the test's thread, as a test moves along.</summary>
    public event EventHandler<SpeedTestProgress>? Progressed;

    /// <summary>Raised, on the test's thread, when a test finishes, kept or not.</summary>
    public event EventHandler<SpeedTestResult>? Finished;

    /// <summary>Where the running test is.</summary>
    public SpeedTestProgress Progress { get; private set; } = SpeedTestProgress.Idle;

    /// <summary>Whether a test is running.</summary>
    public bool IsRunning => _one.CurrentCount == 0;

    /// <summary>Who a test would contact, for saying so before it runs.</summary>
    public string Destination()
    {
        ConnectionToolsSettings tools = _settings.ConnectionTools;

        if (tools.SpeedTestServer == SpeedTestServer.LibreSpeed)
        {
            return LibreSpeedClient.ParseRoot(tools.LibreSpeedUrl) is { } root
                ? string.Concat("your LibreSpeed server at ", root.Host)
                : "your LibreSpeed server (no address set)";
        }

        return "Measurement Lab (locate.measurementlab.net and its nearest NDT7 server), which publishes every result with this PC's public address";
    }

    /// <summary>Why a test would not start now, or <see cref="SpeedTestRefusal.None"/>.</summary>
    public SpeedTestRefusal CanRun() => IsRunning ? SpeedTestRefusal.Running : Refusal();

    /// <summary>Stops whichever test is running, the schedule's included; a stopped test is not kept.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _current?.Cancel();
        }
    }

    private SpeedTestRefusal Refusal()
    {
        ConnectionToolsSettings tools = _settings.ConnectionTools;

        if (tools.SpeedTestServer == SpeedTestServer.MeasurementLab && !tools.MeasurementLabAgreed)
        {
            return SpeedTestRefusal.NotAgreed;
        }

        if (tools.SpeedTestServer == SpeedTestServer.LibreSpeed && LibreSpeedClient.ParseRoot(tools.LibreSpeedUrl) is null)
        {
            return SpeedTestRefusal.NoServer;
        }

        if (_connection.State.IsOffline)
        {
            return SpeedTestRefusal.Offline;
        }

        return _scanner?.IsScanning == true ? SpeedTestRefusal.Scanning : SpeedTestRefusal.None;
    }

    /// <summary>Runs a test and keeps its result. Nothing when it would not start.</summary>
    /// <param name="scheduled">Whether the schedule started it rather than the person.</param>
    /// <param name="cancellationToken">Stops it; a stopped test is not kept.</param>
    public async Task<SpeedTestResult?> RunAsync(bool scheduled, CancellationToken cancellationToken = default)
    {
        // The test's own lock first, then the scan's: a scan takes its lock and
        // then asks whether a test runs, so one of the two always sees the other.
        if (!await _one.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            if (Refusal() != SpeedTestRefusal.None)
            {
                return null;
            }

            using CancellationTokenSource current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            lock (_gate)
            {
                _current = current;
                _currentScheduled = scheduled;
            }

            try
            {
                ConnectionToolsSettings tools = _settings.ConnectionTools;
                DateTimeOffset at = _time.GetLocalNow();
                string? network = _connection.Latest?.Facts.Network?.Describe();

                SpeedTestResult result = tools.SpeedTestServer == SpeedTestServer.LibreSpeed
                    ? await LibreSpeedAsync(tools, at, scheduled, network, current.Token).ConfigureAwait(false)
                    : await MeasurementLabAsync(at, scheduled, network, current.Token).ConfigureAwait(false);

                result = Keep(result);
                Report(new SpeedTestProgress(result.Succeeded ? SpeedTestPhase.Done : SpeedTestPhase.Failed, null, null, result.Server));
                Finished?.Invoke(this, result);
                return result;
            }
            catch (OperationCanceledException) when (current.IsCancellationRequested)
            {
                Report(SpeedTestProgress.Idle);
                return null;
            }
            finally
            {
                lock (_gate)
                {
                    _current = null;
                }
            }
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<SpeedTestResult> MeasurementLabAsync(DateTimeOffset at, bool scheduled, string? network, CancellationToken cancellationToken)
    {
        Report(new SpeedTestProgress(SpeedTestPhase.Locating, null, null, null));

        IReadOnlyList<Ndt7Server> servers;

        try
        {
            servers = await new Ndt7Client(_http).LocateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SpeedTestRefusedException refused)
        {
            return Failed(SpeedTestBackend.MeasurementLab, "M-Lab", at, scheduled, network, refused.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Failed(SpeedTestBackend.MeasurementLab, "M-Lab", at, scheduled, network, "M-Lab could not be reached.");
        }

        if (servers.Count == 0)
        {
            return Failed(SpeedTestBackend.MeasurementLab, "M-Lab", at, scheduled, network, "M-Lab offered no server.");
        }

        Ndt7Server server = servers[0];
        string name = server.Describe();
        LatencyMeter meter = new(this, InternetTarget(), null);

        Report(new SpeedTestProgress(SpeedTestPhase.QuietLatency, null, null, name));
        List<double> quiet = await meter.QuietAsync(cancellationToken).ConfigureAwait(false);

        Report(new SpeedTestProgress(SpeedTestPhase.Download, null, null, name));
        (TransferResult down, List<double> loadedDown) = await meter.UnderLoadAsync(
            token => Ndt7Client.DownloadAsync(server, speed => Report(new SpeedTestProgress(SpeedTestPhase.Download, speed, null, name)), token),
            cancellationToken).ConfigureAwait(false);

        Report(new SpeedTestProgress(SpeedTestPhase.Upload, null, null, name));
        (TransferResult up, List<double> loadedUp) = await meter.UnderLoadAsync(
            token => Ndt7Client.UploadAsync(server, speed => Report(new SpeedTestProgress(SpeedTestPhase.Upload, speed, null, name)), token),
            cancellationToken).ConfigureAwait(false);

        LatencySource source = meter.Pinging ? LatencySource.InternetTarget : LatencySource.ServerTcp;

        if (!meter.Pinging)
        {
            // The server's kernel saw the connection's least round trip as the
            // quiet figure and its smoothed round trip throughout as the loaded one.
            quiet = down.ServerMinRttMs is { } least ? [least] : [];
            loadedDown = [.. down.ServerRttMs];
            loadedUp = [.. up.ServerRttMs];
        }

        return new SpeedTestResult(
            0,
            at,
            SpeedTestBackend.MeasurementLab,
            name,
            scheduled,
            down.BitsPerSecond,
            up.BitsPerSecond,
            Median(quiet),
            Median(loadedDown),
            Median(loadedUp),
            quiet.Count >= 3 ? SteadinessMath.MeanAbsoluteDifference(quiet) : null,
            quiet.Count > 0 ? source : LatencySource.None,
            network,
            down.Problem ?? up.Problem);
    }

    private async Task<SpeedTestResult> LibreSpeedAsync(ConnectionToolsSettings tools, DateTimeOffset at, bool scheduled, string? network, CancellationToken cancellationToken)
    {
        Uri root = LibreSpeedClient.ParseRoot(tools.LibreSpeedUrl)!;
        LibreSpeedClient client = new(_http, root);
        string name = root.Host;

        Report(new SpeedTestProgress(SpeedTestPhase.Locating, null, null, name));

        if (await client.FindAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return Failed(SpeedTestBackend.LibreSpeed, name, at, scheduled, network, "The LibreSpeed server did not answer at that address.");
        }

        LatencyMeter meter = new(this, InternetTarget(), client);

        Report(new SpeedTestProgress(SpeedTestPhase.QuietLatency, null, null, name));
        List<double> quiet = await meter.QuietAsync(cancellationToken).ConfigureAwait(false);

        Report(new SpeedTestProgress(SpeedTestPhase.Download, null, null, name));
        (TransferResult down, List<double> loadedDown) = await meter.UnderLoadAsync(
            token => client.DownloadAsync(speed => Report(new SpeedTestProgress(SpeedTestPhase.Download, speed, null, name)), token),
            cancellationToken).ConfigureAwait(false);

        Report(new SpeedTestProgress(SpeedTestPhase.Upload, null, null, name));
        (TransferResult up, List<double> loadedUp) = await meter.UnderLoadAsync(
            token => client.UploadAsync(speed => Report(new SpeedTestProgress(SpeedTestPhase.Upload, speed, null, name)), token),
            cancellationToken).ConfigureAwait(false);

        return new SpeedTestResult(
            0,
            at,
            SpeedTestBackend.LibreSpeed,
            name,
            scheduled,
            down.BitsPerSecond,
            up.BitsPerSecond,
            Median(quiet),
            Median(loadedDown),
            Median(loadedUp),
            quiet.Count >= 3 ? SteadinessMath.MeanAbsoluteDifference(quiet) : null,
            quiet.Count == 0 ? LatencySource.None : meter.Pinging ? LatencySource.InternetTarget : LatencySource.ServerRequests,
            network,
            down.Problem ?? up.Problem);
    }

    private string? InternetTarget() => _settings.Connection.InternetTarget;

    private SpeedTestResult Keep(SpeedTestResult result)
    {
        try
        {
            long id = _store.AddSpeedTest(result);
            return result with { Id = id };
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogStoreFailed(ex);
            return result;
        }
    }

    private static SpeedTestResult Failed(SpeedTestBackend backend, string server, DateTimeOffset at, bool scheduled, string? network, string problem) =>
        new(0, at, backend, server, scheduled, null, null, null, null, null, null, LatencySource.None, network, problem);

    private static double? Median(List<double> values) => LatencyScale.Median(values.Select(each => (double?)each));

    private void Report(SpeedTestProgress progress)
    {
        progress = progress with { Scheduled = _currentScheduled };
        Progress = progress;
        Progressed?.Invoke(this, progress);
    }

    public void Dispose()
    {
        _http.Dispose();
        _one.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A speed test's result could not be kept")]
    private partial void LogStoreFailed(Exception error);

    /// <summary>Times the line quiet and under load, by pings or by the server.</summary>
    private sealed class LatencyMeter(SpeedTestService owner, string? target, LibreSpeedClient? server)
    {
        /// <summary>Whether pings to the internet target are being used.</summary>
        public bool Pinging => target is not null;

        private bool CanMeasure => target is not null || server is not null;

        public async Task<List<double>> QuietAsync(CancellationToken cancellationToken)
        {
            List<double> samples = [];

            if (!CanMeasure)
            {
                return samples;
            }

            for (int each = 0; each < QuietSamples; each++)
            {
                if (await OnceAsync(cancellationToken).ConfigureAwait(false) is { } ms)
                {
                    samples.Add(ms);
                }

                await Task.Delay(QuietGap, owner._time, cancellationToken).ConfigureAwait(false);
            }

            return samples;
        }

        public async Task<(TransferResult Transfer, List<double> Loaded)> UnderLoadAsync(
            Func<CancellationToken, Task<TransferResult>> transfer,
            CancellationToken cancellationToken)
        {
            List<double> loaded = [];
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<TransferResult> running = transfer(cancellationToken);

            if (CanMeasure)
            {
                // Give the transfer a moment to fill the line before timing it.
                try
                {
                    await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(1), owner._time, stop.Token)).ConfigureAwait(false);

                    while (!running.IsCompleted)
                    {
                        if (await OnceAsync(stop.Token).ConfigureAwait(false) is { } ms && !running.IsCompleted)
                        {
                            loaded.Add(ms);
                            owner.Report(owner.Progress with { LatencyMs = ms });
                        }

                        await Task.WhenAny(running, Task.Delay(LoadedGap, owner._time, stop.Token)).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }

            TransferResult result = await running.ConfigureAwait(false);
            return (result, loaded);
        }

        private async Task<double?> OnceAsync(CancellationToken cancellationToken)
        {
            if (target is not null)
            {
                try
                {
                    TimeSpan? took = await owner._probe.PingAsync(target, cancellationToken).ConfigureAwait(false);
                    return took?.TotalMilliseconds;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return null;
                }
            }

            return server is null ? null : await server.PingAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
