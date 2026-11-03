using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windsock.Core.Adapters;
using Windsock.Core.Asn;
using Windsock.Core.Settings;

namespace Windsock.Core.Lookups;

/// <summary>How a public address lookup went.</summary>
public enum LookupOutcome
{
    /// <summary>It answered.</summary>
    Answered,

    /// <summary>The switch for it is off, so nothing was sent.</summary>
    SwitchedOff,

    /// <summary>No service address is set (a custom one left empty).</summary>
    NotSet,

    /// <summary>The service did not answer, or answered with something that is not an address.</summary>
    Failed,
}

/// <summary>The address the internet sees, and whose network it belongs to.</summary>
/// <param name="Outcome">How the lookup went.</param>
/// <param name="Service">The host that was asked, as the person was told.</param>
/// <param name="At">When it answered.</param>
/// <param name="Four">The public IPv4 address, when there is one.</param>
/// <param name="Six">The public IPv6 address, when there is one.</param>
/// <param name="FourNetwork">The network the IPv4 address belongs to, from the offline table.</param>
/// <param name="SixNetwork">The network the IPv6 address belongs to.</param>
/// <param name="Problem">What went wrong, in a sentence, when something did.</param>
public sealed record PublicAddressResult(
    LookupOutcome Outcome,
    string Service,
    DateTimeOffset At,
    IPAddress? Four,
    IPAddress? Six,
    AutonomousSystem? FourNetwork,
    AutonomousSystem? SixNetwork,
    string? Problem)
{
    /// <summary>The provider's organisation, as the person's ISP or VPN's exit: from IPv4, else IPv6.</summary>
    public AutonomousSystem? Network => FourNetwork ?? SixNetwork;
}

/// <summary>
/// Asks a service the person chose for this PC's public address, only when
/// asked and only while its switch is on, and names the provider from the
/// offline AS table.
/// </summary>
/// <remarks>
/// <para>
/// One request a family per lookup, HTTPS only, no redirects followed, a five
/// second limit, and the answer parsed strictly: anything but a bare address
/// is a failure, not something to show. An answer is kept for five minutes and
/// forgotten when the network changes, so pressing the button twice does not
/// ask twice.
/// </para>
/// <para>
/// ipify answers IPv4 at api.ipify.org and IPv6 at api6.ipify.org. icanhazip.com
/// answers whichever family the connection used. A custom address must be HTTPS
/// and answer with the address as plain text.
/// </para>
/// </remarks>
public sealed partial class PublicAddressLookup : IDisposable
{
    /// <summary>How long an answer is believed.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly WindsockSettings _settings;
    private readonly AsnDirectory? _networks;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private Task<PublicAddressResult>? _running;

    public PublicAddressLookup(
        WindsockSettings settings,
        AsnDirectory? networks,
        TimeProvider time,
        ILogger<PublicAddressLookup>? logger = null,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _networks = networks;
        _time = time;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _http = WindsockAgent.Client(Patience, handler);
    }

    /// <summary>Raised after each lookup, on the thread that finished it.</summary>
    public event EventHandler<PublicAddressResult>? Answered;

    /// <summary>The last answer, while it is fresh.</summary>
    public PublicAddressResult? Latest { get; private set; }

    /// <summary>The hosts a lookup would contact with the current settings, for saying so before it runs.</summary>
    public static IReadOnlyList<Uri> Destinations(ConnectionToolsSettings tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        return tools.PublicAddressService switch
        {
            PublicAddressService.Ipify => [new Uri("https://api.ipify.org/"), new Uri("https://api6.ipify.org/")],
            PublicAddressService.Icanhazip => [new Uri("https://icanhazip.com/")],
            _ => Uri.TryCreate(tools.PublicAddressCustomUrl.Trim(), UriKind.Absolute, out Uri? custom) && custom.Scheme == Uri.UriSchemeHttps
                ? [custom]
                : [],
        };
    }

    /// <summary>The service's host names, for a sentence: "api.ipify.org and api6.ipify.org".</summary>
    public static string Describe(ConnectionToolsSettings tools)
    {
        IReadOnlyList<Uri> hosts = Destinations(tools);
        return hosts.Count == 0 ? "no service yet" : string.Join(" and ", hosts.Select(each => each.Host));
    }

    /// <summary>Forgets the last answer: the network changed, so the address may have too.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            Latest = null;
        }
    }

    /// <summary>
    /// The public address: the fresh answer when there is one, otherwise one
    /// lookup - shared with any already running - when the switch is on.
    /// </summary>
    /// <param name="again">Ask again even when the last answer is fresh.</param>
    /// <param name="cancellationToken">Stops waiting; a lookup already sent finishes for whoever else waits on it.</param>
    public Task<PublicAddressResult> LookUpAsync(bool again = false, CancellationToken cancellationToken = default)
    {
        ConnectionToolsSettings tools = _settings.ConnectionTools;
        DateTimeOffset now = _time.GetLocalNow();

        if (!tools.PublicAddressLookups)
        {
            return Task.FromResult(new PublicAddressResult(LookupOutcome.SwitchedOff, Describe(tools), now, null, null, null, null, null));
        }

        lock (_gate)
        {
            if (!again && Latest is { Outcome: LookupOutcome.Answered } fresh && now - fresh.At < Freshness)
            {
                return Task.FromResult(fresh);
            }

            // One lookup at a time; a finished one is never handed out again,
            // since it may have finished before the answer above was forgotten.
            if (_running is not { IsCompleted: false })
            {
                _running = RunAsync(tools);
            }

            return _running.WaitAsync(cancellationToken);
        }
    }

    private async Task<PublicAddressResult> RunAsync(ConnectionToolsSettings tools)
    {
        PublicAddressResult result = await AskAsync(tools).ConfigureAwait(false);

        lock (_gate)
        {
            Latest = result;
        }

        Answered?.Invoke(this, result);
        return result;
    }

    private async Task<PublicAddressResult> AskAsync(ConnectionToolsSettings tools)
    {
        IReadOnlyList<Uri> hosts = Destinations(tools);
        string service = Describe(tools);

        if (hosts.Count == 0)
        {
            return new PublicAddressResult(LookupOutcome.NotSet, service, _time.GetLocalNow(), null, null, null, null, "No HTTPS address is set for the lookup.");
        }

        IPAddress?[] answers = await Task.WhenAll(hosts.Select(GetAsync)).ConfigureAwait(false);

        IPAddress? four = answers.FirstOrDefault(each => each?.AddressFamily == AddressFamily.InterNetwork);
        IPAddress? six = answers.FirstOrDefault(each => each?.AddressFamily == AddressFamily.InterNetworkV6);
        DateTimeOffset at = _time.GetLocalNow();

        if (four is null && six is null)
        {
            return new PublicAddressResult(LookupOutcome.Failed, service, at, null, null, null, null, "The service did not answer with an address.");
        }

        return new PublicAddressResult(LookupOutcome.Answered, service, at, four, six, _networks?.Find(four), _networks?.Find(six), null);
    }

    private async Task<IPAddress?> GetAsync(Uri host)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(host, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogRefused(host.Host, (int)response.StatusCode);
                return null;
            }

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            LogFailed(host.Host, ex);
            return null;
        }
    }

    /// <summary>
    /// Reads a service's answer: a bare public address, perhaps with a trailing
    /// newline. Anything else - a page, JSON, a private address - is not an answer.
    /// </summary>
    public static IPAddress? Parse(string? body)
    {
        if (body is null)
        {
            return null;
        }

        string text = body.Trim();

        if (text.Length is 0 or > 45 || !IPAddress.TryParse(text, out IPAddress? address))
        {
            return null;
        }

        return AddressScopes.Of(address) == AddressScope.Public ? address : null;
    }

    public void Dispose() => _http.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "The public address service {Host} answered {Status}")]
    private partial void LogRefused(string host, int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "The public address service {Host} did not answer")]
    private partial void LogFailed(string host, Exception error);
}
