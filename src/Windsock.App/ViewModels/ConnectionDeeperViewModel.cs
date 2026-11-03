using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windsock.App.Services;
using Windsock.App.Services.Network;
using Windsock.Core.Adapters;
using Windsock.Core.Asn;
using Windsock.Core.Connectivity;
using Windsock.Core.History;
using Windsock.Core.Lookups;
using Windsock.Core.Networking;
using Windsock.Core.Settings;
using Windsock.Core.SpeedTests;

namespace Windsock.App.ViewModels;

/// <summary>One choice of public address service.</summary>
public sealed record PublicServiceOption(PublicAddressService Service, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One choice of STUN server.</summary>
public sealed record StunServerOption(StunServerChoice Server, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One choice of report period.</summary>
public sealed record ReportRangeOption(int Days, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One row of the hop-by-hop table.</summary>
public sealed record HopRowView(
    string Number,
    string Address,
    string Network,
    string Loss,
    string Sent,
    string Last,
    string Average,
    string Best,
    string Worst,
    bool LossCarriesOn,
    bool IsSilent);

/// <summary>
/// Backs what Connection › Status gained in step 4: a verdict for calls and
/// games with the figure that cost it, jitter and loss, IPv6, carrier-grade
/// NAT, DNS, the public address on request, the hop-by-hop table (full
/// edition) and the report for the provider.
/// </summary>
/// <remarks>
/// The verdict, jitter and loss come from the checks the Status page already
/// makes; nothing extra is sent for them. Everything that would reach beyond
/// this PC - the public address, the STUN test, timing the DNS servers, the
/// hop table - has its switch here, the same setting as on the privacy page,
/// and runs only when its button is pressed.
/// </remarks>
public sealed partial class ConnectionDeeperViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private static readonly TimeSpan RecentTest = TimeSpan.FromDays(7);

    private readonly ConnectionMonitor _monitor;
    private readonly AdapterMonitor _adapters;
    private readonly PublicAddressLookup _publicAddress;
    private readonly ConnectionProbes _probes;
    private readonly HopWatch _hops;
    private readonly OutsideContacts _contacts;
    private readonly NetworkRouteCache _routes;
    private readonly AsnDirectory _networks;
    private readonly ISpeedTestStore _tests;
    private readonly IOutageStore _outages;
    private readonly IQualityStore _quality;
    private readonly IMachineHistoryStore _machine;
    private readonly IUsageHistoryStore _usage;
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<ConnectionDeeperViewModel> _logger;

    private IDisposable? _monitorWatch;
    private IDisposable? _adapterWatch;
    private double? _loadedIncrease;
    private string? _lastReport;
    private bool _shown;

    // Cancelled when the page goes, so a table whose name is still being looked up never starts.
    private CancellationTokenSource? _hopStart;
    private bool _reading;
    private bool _disposed;
    private int _queued;

    public ConnectionDeeperViewModel(
        ConnectionMonitor monitor,
        AdapterMonitor adapters,
        PublicAddressLookup publicAddress,
        ConnectionProbes probes,
        HopWatch hops,
        OutsideContacts contacts,
        NetworkRouteCache routes,
        AsnDirectory networks,
        ISpeedTestStore tests,
        IOutageStore outages,
        IQualityStore quality,
        IMachineHistoryStore machine,
        IUsageHistoryStore usage,
        WindsockSettings settings,
        ISettingsStore settingsStore,
        Dispatcher dispatcher,
        ILogger<ConnectionDeeperViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _monitor = monitor;
        _adapters = adapters;
        _publicAddress = publicAddress;
        _probes = probes;
        _hops = hops;
        _contacts = contacts;
        _routes = routes;
        _networks = networks;
        _tests = tests;
        _outages = outages;
        _quality = quality;
        _machine = machine;
        _usage = usage;
        _settings = settings;
        _settingsStore = settingsStore;
        _dispatcher = dispatcher;
        _logger = logger;

        PublicServices =
        [
            new(PublicAddressService.Ipify, "ipify (api.ipify.org)"),
            new(PublicAddressService.Icanhazip, "icanhazip.com"),
            new(PublicAddressService.Custom, "An address I type"),
        ];

        StunServers =
        [
            new(StunServerChoice.Nextcloud, "Nextcloud (stun.nextcloud.com)"),
            new(StunServerChoice.Cloudflare, "Cloudflare (stun.cloudflare.com)"),
            new(StunServerChoice.Google, "Google (stun.l.google.com)"),
            new(StunServerChoice.Custom, "A server I type"),
        ];

        ReportRanges =
        [
            new(7, "Last 7 days"),
            new(30, "Last 30 days"),
            new(90, "Last 90 days"),
        ];

        SelectedReportRange = 30;
        ReadChoices();
    }

    /// <summary>Whether this edition has the hop-by-hop table.</summary>
    public static bool HopOffered => OutsideContacts.AreOffered;

    public IReadOnlyList<PublicServiceOption> PublicServices { get; }

    public IReadOnlyList<StunServerOption> StunServers { get; }

    public IReadOnlyList<ReportRangeOption> ReportRanges { get; }

    // ------------------------------------------------------------- Verdicts

    [ObservableProperty]
    public partial string CallsVerdict { get; private set; } = "—";

    [ObservableProperty]
    public partial string CallsDetail { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial Verdict CallsMood { get; private set; }

    [ObservableProperty]
    public partial string GamesVerdict { get; private set; } = "—";

    [ObservableProperty]
    public partial string GamesDetail { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial Verdict GamesMood { get; private set; }

    [ObservableProperty]
    public partial string GradeNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string JitterValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string JitterNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LossValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string LossNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string MedianValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string MedianNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string UnderLoadValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string UnderLoadNote { get; private set; } = string.Empty;

    // ------------------------------------------------------- The connection

    [ObservableProperty]
    public partial string Ipv6Value { get; private set; } = "—";

    [ObservableProperty]
    public partial string Ipv6Note { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string NatValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string NatNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DnsValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string DnsNote { get; private set; } = string.Empty;

    // -------------------------------------------------------- Public address

    [ObservableProperty]
    public partial bool AllowPublicAddress { get; set; }

    [ObservableProperty]
    public partial PublicAddressService PublicService { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomPublicService))]
    public partial string PublicCustomUrl { get; set; } = string.Empty;

    public bool IsCustomPublicService => PublicService == PublicAddressService.Custom;

    [ObservableProperty]
    public partial string PublicDestination { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookUpCommand))]
    public partial bool IsLookingUp { get; private set; }

    [ObservableProperty]
    public partial string PublicResult { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PublicNetwork { get; private set; } = string.Empty;

    // -------------------------------------------------------------- NAT test

    [ObservableProperty]
    public partial bool AllowStun { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomStun))]
    public partial StunServerChoice StunServer { get; set; }

    [ObservableProperty]
    public partial string StunCustom { get; set; } = string.Empty;

    public bool IsCustomStun => StunServer == StunServerChoice.Custom;

    [ObservableProperty]
    public partial string NatDestination { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestNatCommand))]
    public partial bool IsTestingNat { get; private set; }

    [ObservableProperty]
    public partial string NatResult { get; private set; } = string.Empty;

    // ------------------------------------------------------------ DNS timing

    [ObservableProperty]
    public partial bool AllowDnsTiming { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TimeDnsCommand))]
    public partial bool IsTimingDns { get; private set; }

    public ObservableCollection<string> DnsTimings { get; } = [];

    // -------------------------------------------------------------- Hop table

    [ObservableProperty]
    public partial bool TraceRoutes { get; set; }

    [ObservableProperty]
    public partial string HopTarget { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartHopsCommand))]
    public partial bool CanStartHops { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartHopsCommand), nameof(StopHopsCommand))]
    public partial bool IsHopRunning { get; private set; }

    [ObservableProperty]
    public partial string HopHeadline { get; private set; } = string.Empty;

    public ObservableCollection<HopRowView> Hops { get; } = [];

    [ObservableProperty]
    public partial bool HasHops { get; private set; }

    // ----------------------------------------------------------------- Report

    [ObservableProperty]
    public partial int SelectedReportRange { get; set; }

    [ObservableProperty]
    public partial bool IncludeNames { get; set; }

    [ObservableProperty]
    public partial string ReportNotice { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasReport { get; private set; }

    /// <summary>Follows the checks and the adapters while Status is on screen.</summary>
    public void Show()
    {
        if (_disposed || _shown)
        {
            return;
        }

        _shown = true;
        _monitor.Sampled += OnSampled;
        _adapters.Changed += OnAdaptersChanged;
        _tests.SpeedTestsChanged += OnTestsChanged;
        _hops.Updated += OnHopsUpdated;
        _hops.Ended += OnHopsEnded;
        _contacts.Changed += OnContactsChanged;
        _monitorWatch = _monitor.Watch();
        _adapterWatch = _adapters.Watch();

        ReadChoices();
        Refresh();
        ApplyAdapters(_adapters.Latest);
        _ = ReadLatestTestAsync();

        if (_hops.Snapshot is { } snapshot)
        {
            ApplyHops(snapshot);
        }
    }

    /// <summary>Stops following, and stops the hop table: it sends only while it can be seen.</summary>
    public void Hide()
    {
        if (!_shown)
        {
            return;
        }

        _shown = false;
        _monitor.Sampled -= OnSampled;
        _adapters.Changed -= OnAdaptersChanged;
        _tests.SpeedTestsChanged -= OnTestsChanged;
        _hops.Updated -= OnHopsUpdated;
        _hops.Ended -= OnHopsEnded;
        _contacts.Changed -= OnContactsChanged;
        _monitorWatch?.Dispose();
        _monitorWatch = null;
        _adapterWatch?.Dispose();
        _adapterWatch = null;
        _hopStart?.Cancel();
        _hopStart?.Dispose();
        _hopStart = null;
        _hops.Stop();
        IsHopRunning = false;
    }

    private void ReadChoices()
    {
        _reading = true;

        try
        {
            ConnectionToolsSettings tools = _settings.ConnectionTools;
            AllowPublicAddress = tools.PublicAddressLookups;
            PublicService = tools.PublicAddressService;
            PublicCustomUrl = tools.PublicAddressCustomUrl;
            AllowStun = tools.StunTest;
            StunServer = tools.StunServer;
            StunCustom = tools.StunCustomServer;
            AllowDnsTiming = tools.DnsTiming;
            TraceRoutes = _contacts.TraceRoutes;
        }
        finally
        {
            _reading = false;
        }

        Reflect();
    }

    partial void OnAllowPublicAddressChanged(bool value) => Save(tools => tools.PublicAddressLookups = value);

    partial void OnPublicServiceChanged(PublicAddressService value)
    {
        OnPropertyChanged(nameof(IsCustomPublicService));
        Save(tools => tools.PublicAddressService = value);
    }

    partial void OnAllowStunChanged(bool value) => Save(tools => tools.StunTest = value);

    partial void OnStunServerChanged(StunServerChoice value) => Save(tools => tools.StunServer = value);

    partial void OnAllowDnsTimingChanged(bool value) => Save(tools => tools.DnsTiming = value);

    partial void OnTraceRoutesChanged(bool value)
    {
        if (_reading)
        {
            return;
        }

        _contacts.TraceRoutes = value;

        if (!value)
        {
            _hops.Stop();
            IsHopRunning = false;
        }

        Reflect();
    }

    [RelayCommand]
    private void ApplyPublicCustom() => Save(tools => tools.PublicAddressCustomUrl = PublicCustomUrl.Trim());

    [RelayCommand]
    private void ApplyStunCustom() => Save(tools => tools.StunCustomServer = StunCustom.Trim());

    private void Save(Action<ConnectionToolsSettings> change)
    {
        if (_reading)
        {
            return;
        }

        change(_settings.ConnectionTools);
        _settingsStore.Save(_settings);
        Reflect();
    }

    private void OnContactsChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(ReadChoices);

    /// <summary>The words that follow the switches: who would be contacted, and what can run.</summary>
    private void Reflect()
    {
        ConnectionToolsSettings tools = _settings.ConnectionTools;
        PublicDestination = string.Concat("Asks ", PublicAddressLookup.Describe(tools), ", which learns your public address.");
        NatDestination = string.Concat("Asks your router for its outside address (stays on your network)", tools.StunTest ? string.Concat(", then ", _probes.StunServer, ", which learns your public address and port.") : ".");

        string? target = _settings.Connection.InternetTarget;
        HopTarget = target is null
            ? "Choose an internet address to ping under Checks, below; the table traces the way to it."
            : string.Concat("Towards ", target, ", one probe a second to every router on the way, while the table is open.");
        CanStartHops = HopOffered && _contacts.TraceRoutes && target is not null && !IsHopRunning;
    }

    private void OnSampled(object? sender, ConnectionSample sample)
    {
        if (Interlocked.Exchange(ref _queued, 1) == 0)
        {
            _dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _queued, 0);
                Refresh();
            });
        }
    }

    private void Refresh()
    {
        LatencyPoint[] points = _monitor.History.Since(DateTimeOffset.Now - Window);
        bool internet = _settings.Connection.InternetTarget is not null && points.Any(each => each.InternetTried);
        Steadiness steadiness = SteadinessMath.Of(points, internet ? CheckTarget.Internet : CheckTarget.Router);

        QualityGrade calls = QualityGrader.Grade(QualityUse.Calls, steadiness, _loadedIncrease);
        QualityGrade games = QualityGrader.Grade(QualityUse.Games, steadiness, _loadedIncrease);

        (CallsVerdict, CallsDetail, CallsMood) = Words(calls);
        (GamesVerdict, GamesDetail, GamesMood) = Words(games);

        GradeNote = steadiness.Sent == 0
            ? "The verdict appears after a few minutes of checks."
            : internet
                ? "From the last hour of checks to your chosen internet address, and the latest speed test's latency under load."
                : "Only your router is checked, so this speaks for your home network alone - Wi-Fi and the router - not your provider. Choose an internet address under Checks to judge the whole way.";

        JitterValue = steadiness.JitterMs is { } jitter ? string.Create(CultureInfo.CurrentCulture, $"{jitter:0.#} ms") : "—";
        JitterNote = steadiness.JitterMs is null
            ? "Needs ten answered checks"
            : "How much the response time wanders between checks";

        LossValue = steadiness.Loss is { } loss ? loss.ToString("0.#%", CultureInfo.CurrentCulture) : "—";
        LossNote = steadiness.Loss is null
            ? "Needs twenty checks"
            : string.Create(CultureInfo.CurrentCulture, $"{steadiness.Sent - steadiness.Answered} of {steadiness.Sent} unanswered");

        MedianValue = steadiness.MedianMs is { } median ? string.Create(CultureInfo.CurrentCulture, $"{median:0} ms") : "—";
        MedianNote = internet ? "Median, to your internet address" : "Median, to your router";

        UnderLoadValue = _loadedIncrease is { } more ? string.Create(CultureInfo.CurrentCulture, $"+{more:0} ms") : "—";
        UnderLoadNote = _loadedIncrease is null ? "Run a speed test to measure" : "From the latest speed test";
    }

    private static (string Verdict, string Detail, Verdict Mood) Words(QualityGrade grade)
    {
        if (grade.Verdict == Verdict.Unknown)
        {
            return ("—", "Not enough checks yet", Verdict.Unknown);
        }

        string detail = grade.Cost is { } cost && grade.Verdict != Verdict.Good
            ? string.Concat("Held back by ", cost.Describe())
            : "Every figure is in the good range";

        return (grade.Verdict.ToString(), detail, grade.Verdict);
    }

    private void OnAdaptersChanged(object? sender, AdapterReading reading) => _dispatcher.BeginInvoke(() => ApplyAdapters(_adapters.Latest));

    private void ApplyAdapters(AdapterReading reading)
    {
        if (reading.Snapshot.At == DateTimeOffset.MinValue)
        {
            return;
        }

        Ipv6Value = reading.Ipv6 switch
        {
            Ipv6State.Working => "Working",
            Ipv6State.ConfiguredNotConfirmed => "Set up",
            Ipv6State.NotAvailable => "Not available",
            _ => "—",
        };
        Ipv6Note = reading.Ipv6 switch
        {
            Ipv6State.Working => "Windows reaches the internet over it",
            Ipv6State.ConfiguredNotConfirmed => "Not yet confirmed by Windows",
            Ipv6State.NotAvailable => "This connection is IPv4 only",
            _ => Ipv6Judge.Describe(reading.Ipv6),
        };

        CarrierNatVerdict local = CarrierNatJudge.Judge(reading.Snapshot, null, _publicAddress.Latest?.Four, Route());
        (NatValue, NatNote) = Nat(local);

        AdapterDetails? carrier = reading.Snapshot.Carrier;
        DnsServer[] servers = carrier is null ? [] : [.. carrier.DnsServers];
        DnsValue = servers.Length == 0 ? "—" : string.Join(", ", servers.Take(2).Select(each => each.Address.ToString()));
        DnsNote = servers.Length == 0
            ? "No DNS server"
            : servers.Any(each => each.Encryption == DnsEncryption.Configured)
                ? "Encryption is set in Windows for at least one"
                : servers.Any(each => each.Encryption == DnsEncryption.Available)
                    ? "Windows could encrypt to these, but is not set to"
                    : "Plain lookups: Windows cannot encrypt to these";
    }

    private IReadOnlyList<PathHop>? Route() =>
        _settings.Connection.InternetTarget is { } target && _routes.Route(target) is { State: RouteState.Found } route ? route.Hops : null;

    private static (string Value, string Note) Nat(CarrierNatVerdict verdict) => verdict.State switch
    {
        CarrierNatState.Yes => ("Shared address", verdict.Reason),
        CarrierNatState.DoubleNat => ("Double NAT", verdict.Reason),
        CarrierNatState.Upstream => ("Another NAT upstream", verdict.Reason),
        CarrierNatState.Likely => ("Probably shared", verdict.Reason),
        CarrierNatState.No => ("Your own address", verdict.Reason),
        _ => ("Not known", verdict.Reason),
    };

    private void OnTestsChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() => _ = ReadLatestTestAsync());

    private async Task ReadLatestTestAsync()
    {
        DateTimeOffset now = DateTimeOffset.Now;

        double? increase = await Task.Run(() =>
        {
            try
            {
                return _tests.ReadSpeedTests(now - RecentTest, now.AddMinutes(1)).FirstOrDefault(each => each.LoadedIncreaseMs is not null)?.LoadedIncreaseMs;
            }
            catch (Exception ex) when (HistoryFailure.IsStorage(ex) || ex is InvalidOperationException)
            {
                return null;
            }
        }).ConfigureAwait(true);

        _loadedIncrease = increase;
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanLookUp))]
    private async Task LookUpAsync()
    {
        IsLookingUp = true;
        PublicResult = "Asking…";
        PublicNetwork = string.Empty;

        try
        {
            PublicAddressResult result = await _publicAddress.LookUpAsync(again: true).ConfigureAwait(true);

            switch (result.Outcome)
            {
                case LookupOutcome.Answered:
                    PublicResult = string.Join("  ·  ", new[] { result.Four?.ToString(), result.Six?.ToString() }.Where(each => each is not null));
                    PublicNetwork = result.Network is { } network
                        ? string.Concat("Network: ", NetworkWords.Network(network), ". With a VPN on, this is the VPN's exit.")
                        : "The network behind it is not in Windsock's table.";
                    ApplyAdapters(_adapters.Latest);
                    break;

                case LookupOutcome.SwitchedOff:
                    PublicResult = "Turn on the switch first.";
                    break;

                case LookupOutcome.NotSet:
                    PublicResult = "Type an HTTPS address that answers with your address as plain text.";
                    break;

                default:
                    PublicResult = result.Problem ?? "The service did not answer.";
                    break;
            }
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            // The lookup answers its own failures; this is the backstop for one it did not foresee.
            PublicResult = "This PC could not reach the service.";
        }
        finally
        {
            IsLookingUp = false;
        }
    }

    /// <summary>A failure of the network under a probe, which a page reports rather than dies of.</summary>
    private static bool IsNetworkFailure(Exception ex) =>
        ex is System.Net.Sockets.SocketException or System.Net.Http.HttpRequestException or IOException or TimeoutException;

    private bool CanLookUp() => !IsLookingUp;

    [RelayCommand(CanExecute = nameof(CanTestNat))]
    private async Task TestNatAsync()
    {
        IsTestingNat = true;
        NatResult = "Testing…";

        try
        {
            NatCheck check = await _probes.CheckNatAsync(Route()).ConfigureAwait(true);
            List<string> lines = [];

            lines.Add(check.Router.Answered
                ? string.Concat("Your router says its outside address is ", check.Router.Address!.ToString(), ".")
                : "Your router did not answer a NAT-PMP request; many do not.");

            if (check.Stun is { } stun)
            {
                if (stun.Problem is { } problem)
                {
                    lines.Add(string.Concat(stun.Server, ": ", problem));
                }
                else
                {
                    lines.Add(string.Concat(
                        stun.Server,
                        " saw you as ",
                        stun.Mapped?.ToString() ?? "—",
                        stun.Summary is { } summary ? string.Concat(". NAT type: ", summary, " (mapping ", Words(stun.Mapping), ", filtering ", Words(stun.Filtering), ").") : ". It cannot tell the NAT type: this server has no second address."));
                }
            }

            (NatValue, NatNote) = Nat(check.CarrierNat);
            lines.Add(check.CarrierNat.Reason);
            NatResult = string.Join(" ", lines);
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            NatResult = StunNatTest.Unreachable;
        }
        finally
        {
            IsTestingNat = false;
        }
    }

    private bool CanTestNat() => !IsTestingNat;

    private static string Words(NatMapping mapping) => mapping switch
    {
        NatMapping.None => "none",
        NatMapping.EndpointIndependent => "the same for everyone",
        NatMapping.AddressDependent => "per address",
        NatMapping.AddressAndPortDependent => "per address and port",
        _ => "not known",
    };

    private static string Words(NatFiltering filtering) => filtering switch
    {
        NatFiltering.EndpointIndependent => "open",
        NatFiltering.AddressDependent => "per address",
        NatFiltering.AddressAndPortDependent => "per address and port",
        _ => "not known",
    };

    [RelayCommand(CanExecute = nameof(CanTimeDns))]
    private async Task TimeDnsAsync()
    {
        IsTimingDns = true;
        DnsTimings.Clear();
        DnsTimings.Add("Timing…");

        try
        {
            IReadOnlyList<DnsTiming> timings = await _probes.TimeDnsAsync().ConfigureAwait(true);
            DnsTimings.Clear();

            if (timings.Count == 0)
            {
                DnsTimings.Add(AllowDnsTiming ? "No DNS server to time." : "Turn on the switch first.");
                return;
            }

            foreach (DnsTiming timing in timings)
            {
                DnsTimings.Add(timing.MedianMs is { } median
                    ? string.Create(CultureInfo.CurrentCulture, $"{timing.Server}: {median:0} ms (first {timing.FirstMs:0} ms, best {timing.BestMs:0} ms, {timing.Answered} of {timing.Sent} answered)")
                    : string.Concat(timing.Server.ToString(), ": ", timing.Problem ?? "no answer"));
            }
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            DnsTimings.Clear();
            DnsTimings.Add("This PC could not reach its DNS servers.");
        }
        finally
        {
            IsTimingDns = false;
        }
    }

    private bool CanTimeDns() => !IsTimingDns;

    [RelayCommand(CanExecute = nameof(CanStartHops))]
    private async Task StartHopsAsync()
    {
        if (_settings.Connection.InternetTarget is not { } target)
        {
            return;
        }

        Hops.Clear();
        HasHops = false;
        HopHeadline = "Finding the way…";

        _hopStart?.Cancel();
        _hopStart?.Dispose();
        CancellationTokenSource start = new();
        _hopStart = start;

        bool started = await _hops.StartAsync(target, start.Token).ConfigureAwait(true);

        if (start.IsCancellationRequested)
        {
            // Stopped, left, or replaced while the name was looked up. A table
            // that started anyway is stopped, unless a newer start now owns it.
            if (started && (_hopStart is null || ReferenceEquals(_hopStart, start)))
            {
                _hops.Stop();
            }

            return;
        }

        IsHopRunning = started;

        if (!IsHopRunning)
        {
            HopHeadline = "The table could not start: the address could not be found, or route tracing is off.";
        }

        Reflect();
    }

    private void OnHopsEnded(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        IsHopRunning = false;
        HopHeadline = _contacts.TraceRoutes
            ? "The table stopped after an error; start it again to carry on."
            : "The table stopped: route tracing was switched off.";
        Reflect();
    });

    [RelayCommand(CanExecute = nameof(IsHopRunning))]
    private void StopHops()
    {
        _hopStart?.Cancel();
        _hops.Stop();
        IsHopRunning = false;
        Reflect();
    }

    private void OnHopsUpdated(object? sender, HopTableSnapshot snapshot) => _dispatcher.BeginInvoke(() => ApplyHops(snapshot));

    private void ApplyHops(HopTableSnapshot snapshot)
    {
        Hops.Clear();

        foreach (HopRow row in snapshot.Rows)
        {
            string address = row.Address ?? "No answer";

            if (row.OtherAddresses.Count > 0)
            {
                address = string.Create(CultureInfo.CurrentCulture, $"{address} (+{row.OtherAddresses.Count})");
            }

            Hops.Add(new HopRowView(
                row.Distance.ToString(CultureInfo.CurrentCulture),
                address,
                row.Address is { } known ? _networks.Find(known)?.Organisation ?? string.Empty : string.Empty,
                row.Loss is { } loss ? loss.ToString("0%", CultureInfo.CurrentCulture) : "—",
                row.Sent.ToString(CultureInfo.CurrentCulture),
                Ms(row.LastMs),
                Ms(row.AverageMs),
                Ms(row.BestMs),
                Ms(row.WorstMs),
                row.LossCarriesOn,
                row.Address is null));
        }

        HasHops = Hops.Count > 0;

        HopHeadline = snapshot.TroubleStartsAt is { } trouble
            ? string.Create(CultureInfo.CurrentCulture, $"Loss starts at hop {trouble.Distance} ({trouble.Address ?? "a silent router"}) and carries on to the far end: that is where packets are going missing. {snapshot.Rounds} rounds so far.")
            : snapshot.Rows.Any(each => each.Loss > 0)
                ? string.Create(CultureInfo.CurrentCulture, $"No loss reaches the far end. A router that drops some probes but passes traffic on is only slow to answer probes about itself. {snapshot.Rounds} rounds so far.")
                : string.Create(CultureInfo.CurrentCulture, $"No loss anywhere on the way. {snapshot.Rounds} rounds so far.");
    }

    private static string Ms(double? ms) => ms is { } value ? value.ToString("0", CultureInfo.CurrentCulture) : "—";

    [RelayCommand]
    private async Task SaveReportAsync()
    {
        DateTimeOffset until = DateTimeOffset.Now;
        DateTimeOffset from = until.AddDays(-Math.Clamp(SelectedReportRange, 1, 365));

        SaveFileDialog dialog = new()
        {
            Title = "Save the connection report",
            Filter = "Web page (*.html)|*.html",
            DefaultExt = ".html",
            AddExtension = true,
            FileName = string.Concat("Windsock connection report ", until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ".html"),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string path = dialog.FileName;
        string? network = IncludeNames ? _monitor.Latest?.Facts.Network?.Describe() : null;
        string? provider = IncludeNames && _publicAddress.Latest?.Network is { } system ? NetworkWords.Network(system) : null;

        ReportNotice = "Writing the report…";

        try
        {
            await Task.Run(() =>
            {
                IspReportData data = IspReport.Gather(from, until, DateTimeOffset.Now, _outages, _quality, _tests, _machine, _usage, _settings, network, provider);
                File.WriteAllText(path, IspReport.Render(data), System.Text.Encoding.UTF8);
            }).ConfigureAwait(true);

            _lastReport = path;
            HasReport = true;
            ReportNotice = string.Concat("Saved to ", path, ". It is a single web page with nothing in it but this PC's records.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogReportFailed(ex);
            HasReport = false;
            ReportNotice = string.Concat("The report could not be written there: ", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenReport()
    {
        if (_lastReport is { } path && File.Exists(path))
        {
            SettingsLauncher.OpenFile(path);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();
        _disposed = true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The connection report could not be written")]
    private partial void LogReportFailed(Exception error);
}
