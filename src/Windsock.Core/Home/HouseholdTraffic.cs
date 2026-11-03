using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.Settings;

namespace Windsock.Core.Home;

/// <summary>
/// The whole household's traffic, read from the router's own WAN counters over
/// UPnP every five seconds while anyone watches.
/// </summary>
/// <remarks>
/// <para>
/// Off until <see cref="HomeSettings.ReadRouterTraffic"/> is on, and then the
/// router is only asked while something watches - a page showing it, or the
/// local feed while one of its clients is connected - and only on a private
/// network. Every request goes to the router's own address and nowhere else.
/// </para>
/// <para>
/// Five seconds keeps a 32-bit counter to at most one wrap between readings up
/// to about 6.8 Gb/s; see <see cref="CounterDelta"/> for how a wrap is told
/// from a reset. A router that does not report its counters, or does not answer
/// UPnP, is <see cref="HouseholdState.NotOffered"/> and shows a dash, never a
/// zero. The counters are the router's internet side, so traffic that stays
/// inside the home is not in them.
/// </para>
/// </remarks>
public sealed partial class HouseholdTraffic : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AskAgain = TimeSpan.FromMinutes(5);

    private readonly WindsockSettings _settings;
    private readonly HomeLinkReader _links;
    private readonly IRouterClient _router;
    private readonly TimeProvider _time;
    private readonly ILogger<HouseholdTraffic> _logger;
    private readonly SemaphoreSlim _nudge = new(0, 1);
    private readonly Lock _gate = new();

    private int _watchers;

    public HouseholdTraffic(
        WindsockSettings settings,
        HomeLinkReader links,
        IRouterClient router,
        TimeProvider time,
        ILogger<HouseholdTraffic> logger)
    {
        _settings = settings;
        _links = links;
        _router = router;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised, on a background thread, after each reading and each change of state.</summary>
    public event EventHandler? Changed;

    /// <summary>Where reading stands.</summary>
    public HouseholdState State { get; private set; } = HouseholdState.Off;

    /// <summary>The latest rates, or nothing before the second reading.</summary>
    public HouseholdRate? Latest { get; private set; }

    /// <summary>The router's maker and model, once it has answered.</summary>
    public string? Router { get; private set; }

    /// <summary>Asks for readings until the token is let go.</summary>
    public IDisposable Watch()
    {
        lock (_gate)
        {
            _watchers++;
        }

        Nudge();
        return new Watcher(this);
    }

    /// <summary>Looks at the setting again.</summary>
    public void Refresh() => Nudge();

    private bool IsWatched
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
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_settings.Home.ReadRouterTraffic)
            {
                Settle(HouseholdState.Off);
            }
            else if (!IsWatched)
            {
                Settle(HouseholdState.Idle);
            }
            else
            {
                try
                {
                    await ReadAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }

            try
            {
                await _nudge.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Reads for as long as the setting is on and somebody watches.</summary>
    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        HomeLink? link = _links.Read().Primary;

        if (link?.Gateway is null)
        {
            Settle(HouseholdState.NotHome);
            await PauseAsync(AskAgain, cancellationToken).ConfigureAwait(false);
            return;
        }

        Settle(HouseholdState.Starting);
        GatewayDescription? description = await _router.DescribeAsync(link, cancellationToken).ConfigureAwait(false);
        GatewayService? counters = description?.Find("WANCommonInterfaceConfig");

        if (counters is null)
        {
            Router = null;
            Settle(HouseholdState.NotOffered);
            await PauseAsync(AskAgain, cancellationToken).ConfigureAwait(false);
            return;
        }

        Router = RouterReport.Describe(description!.Manufacturer, description.ModelName, description.FriendlyName);

        (ulong Down, ulong Up, long Stamp)? previous = null;
        int failures = 0;

        while (_settings.Home.ReadRouterTraffic && IsWatched && !cancellationToken.IsCancellationRequested)
        {
            // Still on the same home network, with the same router? Moving to
            // another network - or a VPN taking over - stops the asking at once,
            // rather than sending SOAP to an old gateway address every five
            // seconds, which on the new network may be somebody else's machine.
            if (!SameLink(link, _links.Read().Primary))
            {
                Latest = null;
                Settle(HouseholdState.Starting);
                return;
            }

            long stamp = _time.GetTimestamp();
            SoapAnswer received = await _router.CallAsync(counters, "GetTotalBytesReceived", null, cancellationToken).ConfigureAwait(false);
            SoapAnswer sent = await _router.CallAsync(counters, "GetTotalBytesSent", null, cancellationToken).ConfigureAwait(false);
            ulong? down = CounterDelta.Parse(received["NewTotalBytesReceived"]);
            ulong? up = CounterDelta.Parse(sent["NewTotalBytesSent"]);

            if (down is null || up is null)
            {
                if (++failures >= 3)
                {
                    LogCountersRefused(link.Gateway);
                    Latest = null;
                    Settle(HouseholdState.NotOffered);
                    await PauseAsync(AskAgain, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                failures = 0;

                if (previous is { } before)
                {
                    TimeSpan elapsed = _time.GetElapsedTime(before.Stamp, stamp);
                    ulong? downBytes = CounterDelta.Between(before.Down, down.Value, elapsed);
                    ulong? upBytes = CounterDelta.Between(before.Up, up.Value, elapsed);

                    Latest = new HouseholdRate(
                        _time.GetLocalNow(),
                        downBytes is { } d && elapsed > TimeSpan.Zero ? d / elapsed.TotalSeconds : null,
                        upBytes is { } u && elapsed > TimeSpan.Zero ? u / elapsed.TotalSeconds : null);
                }

                previous = (down.Value, up.Value, stamp);
                State = HouseholdState.Reading;
                Changed?.Invoke(this, EventArgs.Empty);
            }

            await PauseAsync(Interval, cancellationToken).ConfigureAwait(false);
        }

        Latest = null;
    }

    /// <summary>Whether a link read now is the one the counters were found on: the same network, and the same router.</summary>
    internal static bool SameLink(HomeLink first, HomeLink? now) =>
        now is not null
        && string.Equals(first.NetworkKey, now.NetworkKey, StringComparison.Ordinal)
        && Equals(first.Gateway, now.Gateway)
        && Equals(first.Address, now.Address);

    private async Task PauseAsync(TimeSpan wait, CancellationToken cancellationToken) =>
        _ = await _nudge.WaitAsync(wait, cancellationToken).ConfigureAwait(false);

    private void Settle(HouseholdState state)
    {
        if (State == state && (state == HouseholdState.Reading || Latest is null))
        {
            return;
        }

        State = state;

        if (state != HouseholdState.Reading)
        {
            Latest = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Nudge()
    {
        _nudge.Signal();
    }

    public override void Dispose()
    {
        base.Dispose();
        _nudge.Dispose();
    }

    private sealed class Watcher(HouseholdTraffic owner) : IDisposable
    {
        private HouseholdTraffic? _owner = owner;

        public void Dispose()
        {
            HouseholdTraffic? owner = Interlocked.Exchange(ref _owner, null);

            if (owner is null)
            {
                return;
            }

            lock (owner._gate)
            {
                owner._watchers--;
            }

            owner.Nudge();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The router at {Gateway} stopped answering for its traffic counters")]
    private partial void LogCountersRefused(System.Net.IPAddress gateway);
}
