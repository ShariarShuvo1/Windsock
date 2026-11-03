using Microsoft.Extensions.Logging;
using Windsock.Core.Adapters;
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Notifications;
using Windsock.Core.Settings;

namespace Windsock.Core.Alerts;

/// <summary>
/// Passes on a network's print only once two readings in a row agree on it.
/// </summary>
/// <remarks>
/// In the middle of a move the network's name and its adapters do not yet
/// agree - the new Wi-Fi's name with the old router behind it - and half a
/// move read as a change would cry "rogue router". Two equal readings are a
/// network that has settled.
/// </remarks>
public sealed class PrintConfirmation
{
    private NetworkPrint? _last;

    /// <summary>Takes a reading; returns it when the one before said the same, else nothing.</summary>
    public NetworkPrint? Offer(NetworkPrint? print)
    {
        NetworkPrint? before = _last;
        _last = print;

        return print is not null && before is not null && Same(before, print) ? print : null;
    }

    /// <summary>Forgets the last reading, so the next must be confirmed afresh.</summary>
    public void Reset() => _last = null;

    /// <summary>Whether two readings describe the same network the same way.</summary>
    public static bool Same(NetworkPrint a, NetworkPrint b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        return string.Equals(a.Key, b.Key, StringComparison.Ordinal)
            && string.Equals(a.Address, b.Address, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.DnsText, b.DnsText, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Gateway, b.Gateway, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.RouterMac, b.RouterMac, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// This PC's address, the DNS servers or the router changing on a network it
/// has been on before: a sign of a VPN leak, a rogue hotspot or spoofing.
/// </summary>
/// <remarks>
/// <para>
/// Each network is remembered - its address, DNS servers and router, in the
/// record of alerts - so the comparison spans restarts: joining "Home" and
/// finding a different router behind it is noticed even if the last visit was
/// a week ago. A network not seen before is remembered, never alerted on;
/// moving between networks is the network alert's business.
/// </para>
/// <para>
/// The adapters come from the one reader the app has, <see cref="AdapterMonitor"/>,
/// watched only while one of these alerts is on: it reads when Windows says an
/// address changed and once a minute, which also catches a router or DNS change
/// that changes no address. A reading that differs from the one before asks for
/// another a few seconds later, and only two equal readings are compared
/// (<see cref="PrintConfirmation"/>). Nothing is compared in the minute after
/// starting or waking. The router's hardware address is read from the neighbour
/// table Windows already keeps. None of it sends a packet.
/// </para>
/// </remarks>
public sealed partial class NetworkChangeAlerts : IAlertSource
{
    /// <summary>The gap before a reading that differed is read again.</summary>
    private static readonly TimeSpan Confirm = TimeSpan.FromSeconds(3);

    /// <summary>The most extra readings asked for in a row while a network keeps changing.</summary>
    private const int MostConfirms = 5;

    private readonly WindsockSettings _settings;
    private readonly INoticeSink _notices;
    private readonly AlertSettling _settling;
    private readonly INetworkTagSource _network;
    private readonly AdapterMonitor _adapters;
    private readonly IRouterAddressSource _routers;
    private readonly IAlertLog _log;
    private readonly TimeProvider _clock;
    private readonly ILogger<NetworkChangeAlerts> _logger;
    private readonly Lock _gate = new();
    private readonly PrintConfirmation _confirmation = new();

    private IDisposable? _watch;
    private ITimer? _again;
    private int _confirms;
    private bool _failed;

    public NetworkChangeAlerts(
        WindsockSettings settings,
        INoticeSink notices,
        AlertSettling settling,
        INetworkTagSource network,
        AdapterMonitor adapters,
        IRouterAddressSource routers,
        IAlertLog log,
        TimeProvider clock,
        ILogger<NetworkChangeAlerts> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(settling);
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(routers);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _notices = notices;
        _settling = settling;
        _network = network;
        _adapters = adapters;
        _routers = routers;
        _log = log;
        _clock = clock;
        _logger = logger;
        _settling.Woke += OnWoke;
    }

    /// <inheritdoc />
    public void Apply()
    {
        lock (_gate)
        {
            bool wanted = _settings.Alerts.Changes.AnyNetwork;

            if (wanted && _watch is null)
            {
                _adapters.Changed += OnReading;
                // The first comparison waits out the minute after starting, then reads at once.
                TimeSpan first = _settling.Began + AlertSettling.Grace + Confirm - _clock.GetUtcNow();
                _again = _clock.CreateTimer(_ => _ = _adapters.ReadNowAsync(), null, first > Confirm ? first : Confirm, Timeout.InfiniteTimeSpan);
                _watch = _adapters.Watch();
            }
            else if (!wanted && _watch is not null)
            {
                Stop();
            }
        }
    }

    public void Dispose()
    {
        _settling.Woke -= OnWoke;

        lock (_gate)
        {
            Stop();
        }
    }

    /// <summary>Takes one reading of the adapters. Exposed so tests can drive it a reading at a time.</summary>
    internal void Take(AdapterSnapshot snapshot)
    {
        if (_settling.IsSettling || !_settings.Alerts.Changes.AnyNetwork)
        {
            lock (_gate)
            {
                _confirmation.Reset();
            }

            return;
        }

        NetworkPrint? print = Print(snapshot);
        NetworkPrint? settled;

        lock (_gate)
        {
            settled = _confirmation.Offer(print);

            if (settled is null && print is not null && _confirms < MostConfirms)
            {
                // Read again shortly rather than at the next minute's beat.
                _confirms++;
                _again?.Change(Confirm, Timeout.InfiniteTimeSpan);
            }
            else if (settled is not null)
            {
                _confirms = 0;
            }
        }

        if (settled is not null)
        {
            Compare(settled);
        }
    }

    private void Stop()
    {
        _adapters.Changed -= OnReading;
        _again?.Dispose();
        _again = null;
        _watch?.Dispose();
        _watch = null;
        _confirmation.Reset();
    }

    private void OnReading(object? sender, AdapterReading reading)
    {
        try
        {
            Take(reading.Snapshot);
        }
#pragma warning disable CA1031 // On the monitor's thread: a failed comparison is no alert, never a crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReadFailed(ex);
        }
    }

    private void Compare(NetworkPrint now)
    {
        try
        {
            NetworkPrint? known = _log.KnownPrint(now.Key);
            ChangeAlertSettings choices = _settings.Alerts.Changes;

            foreach (NetworkPrintDifference change in NetworkPrintJudge.Compare(known, now))
            {
                bool wanted = change.Change switch
                {
                    NetworkPrintChange.Address => choices.Address,
                    NetworkPrintChange.Dns => choices.Dns,
                    _ => choices.Router,
                };

                if (wanted)
                {
                    _notices.Send(AlertNotices.NetworkChange(change));
                }
            }

            _log.KeepPrint(NetworkPrintJudge.Remember(known, now));
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

    private NetworkPrint? Print(AdapterSnapshot snapshot) =>
        NetworkPrint.From(
            snapshot,
            _network.Current,
            (adapter, router) => _routers.HardwareAddressOf(router, adapter.Index),
            _clock.GetLocalNow());

    private void OnWoke(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _confirmation.Reset();
            _confirms = 0;

            // Look once the minute after waking is over: the router behind the
            // network rejoined is exactly what a rogue hotspot would change.
            _again?.Change(AlertSettling.Grace + Confirm, Timeout.InfiniteTimeSpan);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "A reading of the adapters could not be compared for the change alerts")]
    private partial void LogReadFailed(Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The networks remembered for the change alerts could not be read or written")]
    private partial void LogStoreFailed(Exception error);
}
