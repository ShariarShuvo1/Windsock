using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Windsock.Core.Wifi;

/// <summary>Where the Wi-Fi stands.</summary>
public enum WifiState
{
    /// <summary>Not read yet.</summary>
    Unknown,

    /// <summary>This PC has no Wi-Fi adapter.</summary>
    NoAdapter,

    /// <summary>The WLAN AutoConfig service is not running.</summary>
    ServiceOff,

    /// <summary>The Wi-Fi radio is switched off.</summary>
    RadioOff,

    /// <summary>Not joined to a network.</summary>
    Disconnected,

    /// <summary>Joining one.</summary>
    Connecting,

    /// <summary>Joined.</summary>
    Connected,
}

/// <summary>Whether the network's name can be shown.</summary>
public enum NameAccess
{
    /// <summary>Not asked for: nobody opened Wi-Fi details.</summary>
    NotAsked,

    /// <summary>Windows gave it.</summary>
    Allowed,

    /// <summary>Windows refused: location permission is off for desktop apps, or for Windsock.</summary>
    Denied,

    /// <summary>Windows could not give it for another reason.</summary>
    Unavailable,
}

/// <summary>One reading of the Wi-Fi.</summary>
/// <param name="At">When it was read.</param>
/// <param name="State">Where the Wi-Fi stands.</param>
/// <param name="Adapter">The Wi-Fi adapter read, when there is one.</param>
/// <param name="Quality">The link, from the realtime quality opcode, when connected.</param>
/// <param name="Association">The connection's name and access point, only when location permission allows and it was asked for.</param>
/// <param name="Name">Whether the name could be read.</param>
/// <param name="Fallback">
/// Whether the link figures came from the older current-connection opcode,
/// on a Windows without the realtime one (before 24H2), where it needs no permission.
/// </param>
public sealed record WifiReading(
    DateTimeOffset At,
    WifiState State,
    WlanInterface? Adapter,
    WifiLinkQuality? Quality,
    WifiAssociation? Association,
    NameAccess Name,
    bool Fallback)
{
    /// <summary>Nothing read yet.</summary>
    public static WifiReading Empty { get; } = new(DateTimeOffset.MinValue, WifiState.Unknown, null, null, null, NameAccess.NotAsked, false);

    /// <summary>Whether Wi-Fi is joined to a network.</summary>
    public bool IsConnected => State == WifiState.Connected;

    /// <summary>The physical layer.</summary>
    public WifiPhy Phy => Quality?.Phy ?? Association?.Phy ?? WifiPhy.Unknown;

    /// <summary>The band of the strongest link.</summary>
    public WifiBand Band => Quality?.Best?.Band ?? WifiBand.Unknown;

    /// <summary>The strongest link's channel, or 0.</summary>
    public int Channel => Quality?.Best?.Channel ?? 0;

    /// <summary>The strongest link's width in MHz, or 0.</summary>
    public int WidthMhz => Quality?.Best?.WidthMhz ?? 0;

    /// <summary>The signal in dBm, when connected.</summary>
    public int? Rssi => !IsConnected ? null
        : Quality is { } quality ? quality.Rssi
        : Association is { } association ? WifiBands.RssiFromQuality(association.SignalQuality)
        : null;

    /// <summary>Windows' signal quality, 0 to 100, when connected.</summary>
    public uint? SignalPercent => !IsConnected ? null : Quality?.LinkQuality ?? Association?.SignalQuality;

    /// <summary>Bars of signal, 0 to 4, when connected.</summary>
    public int? Bars => Rssi is { } rssi ? WifiBands.Bars(rssi) : null;

    /// <summary>The band in a few characters for a taskbar slot: "2.4G", "5G", "6G", or empty.</summary>
    public string ShortBand => IsConnected ? WifiBands.Abbreviated(Band) : string.Empty;

    /// <summary>The receive link rate in Mbit/s.</summary>
    public double? ReceiveMbps => (Quality?.ReceiveKbps ?? Association?.ReceiveKbps) / 1000d;

    /// <summary>The transmit link rate in Mbit/s.</summary>
    public double? TransmitMbps => (Quality?.TransmitKbps ?? Association?.TransmitKbps) / 1000d;

    /// <summary>"Wi-Fi 7", "Wi-Fi 6E" and so on.</summary>
    public string Generation => WifiBands.Generation(Phy, Band);

    /// <summary>Whether this is a Wi-Fi 7 multi-link connection.</summary>
    public bool IsMultiLink => Quality?.IsMultiLink == true && Quality.Links.Count > 1;

    /// <summary>The network's name, only when Windows allowed it to be read.</summary>
    public string? NetworkName => Name == NameAccess.Allowed ? Association?.Name : null;
}

/// <summary>
/// Reads the Wi-Fi once a second while anyone is watching: generation, band,
/// channel, width, signal and link rates, and the signal's recent history.
/// </summary>
/// <remarks>
/// <para>
/// The figures come from the realtime connection quality opcode, which
/// Microsoft documents as leaving out location-sensitive information and so
/// needing no location permission. The network's name and access point come
/// only from the current-connection opcode, which since Windows 11 24H2 does
/// need it, and which may make Windows ask the person the first time. So the
/// name is read only while a watcher asks for it - Wi-Fi details open on
/// screen - and never at startup.
/// </para>
/// <para>
/// On a Windows without the realtime opcode (before 24H2) the current-connection
/// opcode is used for the figures instead; there, the location rule does not
/// exist yet, so nothing is asked of anybody. Which Windows it is decides that,
/// by build, and only after the realtime opcode has been refused three times in
/// a row. On 24H2 and later the current-connection opcode is read only while a
/// name watcher asks, whatever the realtime opcode answers: one refusal used
/// to switch every watcher - the taskbar slot included - to reading the
/// location-gated opcode each second.
/// </para>
/// </remarks>
public sealed partial class WifiMonitor : BackgroundService
{
    /// <summary>How often the Wi-Fi is read while watched.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan NameEvery = TimeSpan.FromSeconds(10);

    /// <summary>Windows 11 24H2: the build that brought the realtime opcode and the location rule.</summary>
    public const int LocationRuleBuild = 26100;

    /// <summary>How many refusals in a row before an older Windows is taken to lack the realtime opcode.</summary>
    private const int RefusalsBeforeFallback = 3;

    private readonly IWlanClient _client;
    private readonly ILogger<WifiMonitor> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _nudge = new(0, 1);
    private readonly Lock _gate = new();
    private readonly int _build;

    private int _watchers;
    private int _nameWatchers;
    private bool _realtimeMissing;
    private int _realtimeRefusals;
    private DateTimeOffset _namedAt = DateTimeOffset.MinValue;
    private WifiAssociation? _association;
    private NameAccess _name = NameAccess.NotAsked;
    private WlanStatus _lastFailure = WlanStatus.Ok;

    public WifiMonitor(IWlanClient client, ILogger<WifiMonitor> logger, TimeProvider time)
        : this(client, logger, time, Environment.OSVersion.Version.Build)
    {
    }

    /// <summary>For tests: a monitor that takes itself to be on a given Windows build.</summary>
    internal WifiMonitor(IWlanClient client, ILogger<WifiMonitor> logger, TimeProvider time, int windowsBuild)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _logger = logger;
        _time = time;
        _build = windowsBuild;
    }

    /// <summary>Whether this Windows may read link figures from the current-connection opcode: before 24H2 only.</summary>
    private bool MayFallBack => _build < LocationRuleBuild;

    /// <summary>Raised on the reading thread after each reading.</summary>
    public event EventHandler<WifiReading>? Sampled;

    /// <summary>The most recent reading.</summary>
    public WifiReading Latest { get; private set; } = WifiReading.Empty;

    /// <summary>
    /// The signal over the last half hour at one reading a second, the same
    /// span the throughput chart holds, for drawing one under the other.
    /// </summary>
    public SignalHistory History { get; } = new(1800);

    /// <summary>Whether anyone is watching.</summary>
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

    /// <summary>
    /// Asks for readings until the token is let go. With <paramref name="withName"/>,
    /// also asks Windows for the network's name - which may make Windows ask the
    /// person about location the first time - so pass it only from Wi-Fi details
    /// the person opened.
    /// </summary>
    public IDisposable Watch(bool withName = false)
    {
        bool first;

        lock (_gate)
        {
            first = _watchers++ == 0;

            if (withName && _nameWatchers++ == 0)
            {
                _namedAt = DateTimeOffset.MinValue;
            }
        }

        if (first || withName)
        {
            Nudge();
        }

        return new Watcher(this, withName);
    }

    private void Nudge()
    {
        _nudge.Signal();
    }

    private void Release(bool withName)
    {
        lock (_gate)
        {
            _watchers = Math.Max(0, _watchers - 1);

            if (withName)
            {
                _nameWatchers = Math.Max(0, _nameWatchers - 1);
            }

            if (_nameWatchers == 0)
            {
                _association = null;
                _name = NameAccess.NotAsked;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!IsWatched)
                {
                    History.Clear();
                    await _nudge.WaitAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                long started = _time.GetTimestamp();
                ReadOnce();

                TimeSpan rest = Every - _time.GetElapsedTime(started);

                if (rest > TimeSpan.Zero)
                {
                    await _nudge.WaitAsync(rest, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Takes one reading. Exposed so tests can drive the monitor a reading at a time.</summary>
    internal WifiReading ReadOnce()
    {
        WifiReading reading;

        try
        {
            reading = Read();
        }
#pragma warning disable CA1031 // A failed read is an unknown reading; the page shows a dash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReadFailed(ex);
            reading = WifiReading.Empty with { At = _time.GetLocalNow() };
        }

        Latest = reading;
        History.Add(new SignalPoint(reading.At, reading.Rssi, reading.ReceiveMbps, reading.TransmitMbps, reading.Band));
        Sampled?.Invoke(this, reading);
        return reading;
    }

    private WifiReading Read()
    {
        DateTimeOffset now = _time.GetLocalNow();
        (WlanStatus status, IReadOnlyList<WlanInterface> adapters) = _client.Interfaces();

        if (status != WlanStatus.Ok)
        {
            Note(status);
            return WifiReading.Empty with
            {
                At = now,
                State = status == WlanStatus.NoService ? WifiState.ServiceOff : WifiState.Unknown,
            };
        }

        WlanInterface? adapter = adapters.FirstOrDefault(each => each.IsConnected)
            ?? adapters.FirstOrDefault(each => each.IsConnecting)
            ?? (adapters.Count > 0 ? adapters[0] : null);

        if (adapter is null)
        {
            return WifiReading.Empty with { At = now, State = WifiState.NoAdapter };
        }

        if (!adapter.IsConnected)
        {
            return WifiReading.Empty with
            {
                At = now,
                State = adapter.IsConnecting ? WifiState.Connecting : WifiState.Disconnected,
                Adapter = adapter,
            };
        }

        WifiLinkQuality? quality = null;
        bool fallback = false;
        bool refused = false;
        WifiState state = WifiState.Connected;

        if (!_realtimeMissing)
        {
            (WlanStatus asked, byte[]? data) = _client.Query(adapter.Id, WlanClient.RealtimeQualityOpcode);

            switch (asked)
            {
                case WlanStatus.Ok:
                    quality = WifiDecoders.RealtimeQuality(data);
                    _realtimeRefusals = 0;
                    break;

                case WlanStatus.NotSupported:
                    refused = true;

                    if (MayFallBack && ++_realtimeRefusals >= RefusalsBeforeFallback)
                    {
                        _realtimeMissing = true;
                        LogNoRealtime();
                    }
                    else if (!MayFallBack)
                    {
                        Note(asked);
                    }

                    break;

                case WlanStatus.RadioOff:
                    state = WifiState.RadioOff;
                    break;

                case WlanStatus.NotConnected:
                    state = WifiState.Disconnected;
                    break;

                default:
                    Note(asked);
                    break;
            }
        }

        bool wantName;

        lock (_gate)
        {
            wantName = _nameWatchers > 0;
        }

        // The figures from the current connection only before 24H2, where it
        // needs no permission; from 24H2 on it is read for a name watcher alone.
        bool needFigures = MayFallBack && (_realtimeMissing || refused);
        bool needName = wantName && now - _namedAt >= NameEvery;

        if (state == WifiState.Connected && (needFigures || needName))
        {
            (WlanStatus asked, byte[]? data) = _client.Query(adapter.Id, WlanClient.CurrentConnectionOpcode);

            if (needName)
            {
                _namedAt = now;
            }

            WifiAssociation? association = asked == WlanStatus.Ok ? WifiDecoders.ConnectionAttributesOf(data) : null;
            NameAccess access = asked switch
            {
                WlanStatus.Ok when association is not null => NameAccess.Allowed,
                WlanStatus.Denied => NameAccess.Denied,
                _ => NameAccess.Unavailable,
            };

            fallback = needFigures && association is not null;

            lock (_gate)
            {
                // Asked again here: the last name watcher may have gone while
                // Windows was being asked, and its name must go with it. Before
                // 24H2 the figures need the association whatever; the name is
                // still only shown when asked.
                bool stillWanted = _nameWatchers > 0;
                _association = stillWanted || needFigures ? association : null;
                _name = stillWanted ? access : NameAccess.NotAsked;
            }
        }

        WifiAssociation? kept;
        NameAccess name;

        lock (_gate)
        {
            kept = _association;
            name = _name;
        }

        return new WifiReading(
            now,
            state,
            adapter,
            quality,
            state == WifiState.Connected ? kept : null,
            name,
            fallback || (_realtimeMissing && kept is not null));
    }

    private void Note(WlanStatus status)
    {
        if (status != _lastFailure)
        {
            _lastFailure = status;
            LogStatus(status);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _nudge.Dispose();
    }

    private sealed class Watcher(WifiMonitor monitor, bool withName) : IDisposable
    {
        private WifiMonitor? _monitor = monitor;

        public void Dispose() => Interlocked.Exchange(ref _monitor, null)?.Release(withName);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "This Windows has no realtime Wi-Fi quality; using the current connection instead")]
    private partial void LogNoRealtime();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Wi-Fi reading answered {Status}")]
    private partial void LogStatus(WlanStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the Wi-Fi failed")]
    private partial void LogReadFailed(Exception error);
}
