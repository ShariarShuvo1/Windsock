using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windsock.Core.Connectivity;
using Windsock.Core.Formatting;
using Windsock.Core.History;
using Windsock.Core.Lookups;
using Windsock.Core.Plans;
using Windsock.Core.Settings;
using Windsock.Core.Wifi;

namespace Windsock.App.Services;

/// <summary>
/// Gathers what the meter shows besides rates and the machine - response
/// times, today's and this month's totals, the plan, Wi-Fi, addresses, JSON
/// values - and keeps each source running only while a place shows it.
/// </summary>
/// <remarks>
/// <para>
/// Collection is asked for, not assumed: nothing here starts until the meter
/// says a place needs it, and each source stops the moment none does.
/// Engines another part of the app registers (the plan monitor, the Wi-Fi
/// monitor, the public address lookup, history's quarter hours) are taken
/// from the container if they are there and left out if not, so a slot whose
/// engine is missing shows a dash rather than failing.
/// </para>
/// <para>
/// The public address is asked for only while its own switch, on the
/// Adapters page and the privacy page, is on - and then at most every five
/// minutes, and again after the network changes. With the switch off a public
/// address place shows a dash and nothing is sent.
/// </para>
/// </remarks>
internal sealed partial class MeterSources : IDisposable
{
    private static readonly TimeSpan TotalsEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan AddressEvery = TimeSpan.FromMinutes(5);

    private readonly ConnectionMonitor _connection;
    private readonly WindsockSettings _settings;
    private readonly IServiceProvider _services;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _clock;
    private readonly MeterFeeds _feeds;
    private IDisposable? _connectionWatch;
    private IDisposable? _wifiWatch;
    private WifiMonitor? _wifi;
    private PlanMonitor? _plans;
    private PublicAddressLookup? _publicAddress;
    private HashSet<MeterField> _wanted = [];
    private MeterExtras _extras = MeterExtras.None;
    private double? _router;
    private double? _internet;
    private bool _internetChecked;
    private long? _today;
    private long? _month;
    private string? _local;
    private string? _public;
    private string? _country;
    private string? _plan;
    private double? _planUsed;
    private WifiFigures? _wifiFigures;
    private DateTimeOffset _totalsAt = DateTimeOffset.MinValue;
    private DateTimeOffset _addressAskedAt = DateTimeOffset.MinValue;
    private bool _readingTotals;
    private bool _queued;
    private bool _listeningForAddresses;
    private bool _disposed;

    public MeterSources(
        ConnectionMonitor connection,
        WindsockSettings settings,
        IServiceProvider services,
        Dispatcher dispatcher,
        ILogger logger)
    {
        _connection = connection;
        _settings = settings;
        _services = services;
        _dispatcher = dispatcher;
        _logger = logger;
        _feeds = new MeterFeeds(dispatcher, logger);
        _feeds.Changed += (_, _) => Queue();

        _clock = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(15) };
        _clock.Tick += (_, _) => Tick();
    }

    /// <summary>Raised on the UI thread when anything the meter shows from here changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Everything known now.</summary>
    public MeterExtras Extras => _extras;

    /// <summary>
    /// Starts what these readings need and stops what they no longer do.
    /// </summary>
    public void Want(IEnumerable<MeterField> fields, IReadOnlyList<MeterFeed> feeds)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(feeds);

        if (_disposed)
        {
            return;
        }

        HashSet<MeterField> wanted = [.. fields];
        _wanted = wanted;

        FollowPings(wanted.Contains(MeterField.RouterPing) || wanted.Contains(MeterField.InternetPing));
        FollowWifi(wanted.Contains(MeterField.WifiSignal));
        FollowPlan(wanted.Contains(MeterField.Plan));
        FollowPublicAddress(wanted.Contains(MeterField.PublicAddress));
        FollowLocalAddress(wanted.Contains(MeterField.LocalAddress));
        _feeds.Follow(wanted.Contains(MeterField.Feed) ? feeds : [], _settings.Taskbar.Feeds);

        if (wanted.Contains(MeterField.TodayTotal) || wanted.Contains(MeterField.MonthTotal))
        {
            _totalsAt = DateTimeOffset.MinValue;
        }
        else
        {
            _today = null;
            _month = null;
        }

        bool timed = wanted.Contains(MeterField.TodayTotal) || wanted.Contains(MeterField.MonthTotal)
            || wanted.Contains(MeterField.PublicAddress) || wanted.Contains(MeterField.LocalAddress);

        if (timed)
        {
            _clock.Start();
            Tick();
        }
        else
        {
            _clock.Stop();
        }

        Queue();
    }

    /// <summary>Stops everything.</summary>
    public void Stop() => Want([], []);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        _feeds.Dispose();
    }

    private void Tick()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if ((_wanted.Contains(MeterField.TodayTotal) || _wanted.Contains(MeterField.MonthTotal))
            && now - _totalsAt >= TotalsEvery && !_readingTotals)
        {
            _totalsAt = now;
            _ = ReadTotalsAsync();
        }

        if (_wanted.Contains(MeterField.PublicAddress))
        {
            AskPublicAddress(now);
        }

        if (_wanted.Contains(MeterField.LocalAddress))
        {
            ReadLocalAddress();
        }
    }

    // ------------------------------------------------------------ response

    private void FollowPings(bool wanted)
    {
        if (wanted == (_connectionWatch is not null))
        {
            return;
        }

        if (wanted)
        {
            _connection.Sampled += OnConnectionSample;
            _connectionWatch = _connection.Watch();

            if (_connection.Latest is { } latest)
            {
                Take(latest);
            }

            return;
        }

        _connection.Sampled -= OnConnectionSample;
        _connectionWatch?.Dispose();
        _connectionWatch = null;
        _router = null;
        _internet = null;
        _internetChecked = false;
    }

    private void OnConnectionSample(object? sender, ConnectionSample sample) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_connectionWatch is not null)
            {
                Take(sample);
                Queue();
            }
        }));

    private void Take(ConnectionSample sample)
    {
        _router = sample.Router?.TotalMilliseconds;
        _internet = sample.Internet?.TotalMilliseconds;
        _internetChecked = sample.InternetChecked;
    }

    // ---------------------------------------------------------------- Wi-Fi

    private void FollowWifi(bool wanted)
    {
        if (wanted == (_wifiWatch is not null))
        {
            return;
        }

        if (wanted)
        {
            _wifi = _services.GetService<WifiMonitor>();

            if (_wifi is null)
            {
                return;
            }

            _wifi.Sampled += OnWifiSample;
            _wifiWatch = _wifi.Watch();
            Take(_wifi.Latest);
            return;
        }

        if (_wifi is not null)
        {
            _wifi.Sampled -= OnWifiSample;
        }

        _wifiWatch?.Dispose();
        _wifiWatch = null;
        _wifiFigures = null;
    }

    private void OnWifiSample(object? sender, WifiReading reading) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_wifiWatch is not null)
            {
                Take(reading);
                Queue();
            }
        }));

    private void Take(WifiReading reading)
    {
        _wifiFigures = reading.IsConnected
            ? new WifiFigures(reading.SignalPercent, reading.Bars, Band(reading.Band))
            : null;
    }

    private static string Band(WifiBand band) => band switch
    {
        WifiBand.TwoPointFour => "2.4 GHz",
        WifiBand.Five => "5 GHz",
        WifiBand.Six => "6 GHz",
        _ => string.Empty,
    };

    // ----------------------------------------------------------------- plan

    private void FollowPlan(bool wanted)
    {
        if (wanted == (_plans is not null))
        {
            return;
        }

        if (wanted)
        {
            _plans = _services.GetService<PlanMonitor>();

            if (_plans is not null)
            {
                _plans.Changed += OnPlansChanged;
                ReadPlan();
            }

            return;
        }

        _plans!.Changed -= OnPlansChanged;
        _plans = null;
        _plan = null;
        _planUsed = null;
    }

    private void OnPlansChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            ReadPlan();
            Queue();
        }));

    private void ReadPlan()
    {
        _plan = _plans?.ActiveText;
        _planUsed = _plans?.ActiveFraction is { } used ? used * 100 : null;
    }

    // --------------------------------------------------------------- totals

    private async Task ReadTotalsAsync()
    {
        if (_services.GetService<IUsageAggregates>() is not { } aggregates)
        {
            return;
        }

        _readingTotals = true;

        try
        {
            DateTimeOffset now = DateTimeOffset.Now;
            DateTimeOffset today = new(now.Date, now.Offset);
            DateTimeOffset month = new(new DateTime(now.Year, now.Month, 1), now.Offset);
            long until = EpochMinute.From(now) + 1;

            (long Today, long Month) totals = await Task.Run(() =>
            {
                long fromMonth = EpochMinute.From(month);
                long fromToday = EpochMinute.From(today);
                long day = 0;
                long all = 0;

                foreach (UsageQuarter quarter in aggregates.ReadQuarters(fromMonth, until))
                {
                    all += quarter.BytesTotal;

                    if (quarter.StartMinute >= fromToday)
                    {
                        day += quarter.BytesTotal;
                    }
                }

                return (day, all);
            }).ConfigureAwait(true);

            _today = totals.Today;
            _month = totals.Month;
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            _today = null;
            _month = null;
            LogTotalsFailed(ex);
        }
        finally
        {
            _readingTotals = false;
        }

        Queue();
    }

    // ------------------------------------------------------------ addresses

    private void FollowPublicAddress(bool wanted)
    {
        if (wanted == (_publicAddress is not null))
        {
            return;
        }

        if (wanted)
        {
            _publicAddress = _services.GetService<PublicAddressLookup>();

            if (_publicAddress is not null)
            {
                _publicAddress.Answered += OnPublicAddress;
                _addressAskedAt = DateTimeOffset.MinValue;
            }

            return;
        }

        _publicAddress!.Answered -= OnPublicAddress;
        _publicAddress = null;
        _public = null;
        _country = null;
    }

    /// <summary>
    /// The public address: what Connection's Look up last found, and nothing
    /// asked of the meter's own unless the person chose to keep it current -
    /// then one lookup every five minutes at most. Neither without the
    /// service's own switch.
    /// </summary>
    private void AskPublicAddress(DateTimeOffset now)
    {
        if (_publicAddress is not { } lookup)
        {
            return;
        }

        if (!_settings.ConnectionTools.PublicAddressLookups)
        {
            _public = null;
            _country = null;
            return;
        }

        PublicAddressResult? latest = lookup.Latest;
        bool current = _settings.Taskbar.RefreshPublicAddress;

        if (latest is { Outcome: LookupOutcome.Answered } && (!current || now - latest.At < AddressEvery))
        {
            Take(latest);
            return;
        }

        if (!current)
        {
            _public = null;
            _country = null;
            return;
        }

        if (now - _addressAskedAt < AddressEvery)
        {
            return;
        }

        _addressAskedAt = now;
        _ = lookup.LookUpAsync(again: true);
    }

    private void OnPublicAddress(object? sender, PublicAddressResult result) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_publicAddress is not null)
            {
                Take(result);
                Queue();
            }
        }));

    private void Take(PublicAddressResult result)
    {
        if (result.Outcome != LookupOutcome.Answered)
        {
            _public = null;
            _country = null;
            return;
        }

        _public = (result.Four ?? result.Six)?.ToString();
        _country = result.Network?.Country is { Length: 2 } country ? country.ToUpperInvariant() : null;
    }

    private void FollowLocalAddress(bool wanted)
    {
        if (wanted == _listeningForAddresses)
        {
            return;
        }

        _listeningForAddresses = wanted;

        if (wanted)
        {
            NetworkChange.NetworkAddressChanged += OnAddressChanged;
            ReadLocalAddress();
            return;
        }

        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _local = null;
    }

    private void OnAddressChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_listeningForAddresses)
            {
                ReadLocalAddress();
                Queue();
            }
        }));

    /// <summary>
    /// The address of the adapter that carries the default route: up, not a
    /// loopback or a tunnel, with a gateway. IPv4 first, then a global IPv6.
    /// </summary>
    private void ReadLocalAddress()
    {
        try
        {
            IPAddress? best = null;

            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceProperties properties = adapter.GetIPProperties();

                if (properties.GatewayAddresses.Count == 0)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                {
                    IPAddress address = unicast.Address;

                    if (address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        _local = address.ToString();
                        return;
                    }

                    if (best is null && address.AddressFamily == AddressFamily.InterNetworkV6
                        && !address.IsIPv6LinkLocal && !address.IsIPv6UniqueLocal)
                    {
                        best = address;
                    }
                }
            }

            _local = best?.ToString();
        }
        catch (NetworkInformationException)
        {
            _local = null;
        }
    }

    // --------------------------------------------------------------- telling

    private void Queue()
    {
        if (_queued || _disposed)
        {
            return;
        }

        _queued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _queued = false;
            _extras = new MeterExtras
            {
                RouterMilliseconds = _router,
                InternetMilliseconds = _internet,
                InternetChecked = _internetChecked,
                TodayBytes = _today,
                MonthBytes = _month,
                LocalAddress = _local,
                PublicAddress = _public,
                PublicCountry = _country,
                PlanLeft = _plan,
                PlanUsedPercent = _planUsed,
                Wifi = _wifiFigures,
                Feeds = _feeds.Readings,
                Temperature = _settings.Temperature,
            };

            Changed?.Invoke(this, EventArgs.Empty);
        }));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The meter could not read today's totals from history")]
    private partial void LogTotalsFailed(Exception ex);
}
