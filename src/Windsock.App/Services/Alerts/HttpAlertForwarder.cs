using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Windsock.Core.Alerts;
using Windsock.Core.History;
using Windsock.Core.Settings;

namespace Windsock.App.Services.Alerts;

/// <summary>
/// Sends alerts on to the webhook or ntfy topic the person set up, one at a
/// time in the background, and notes in the record whether each one went.
/// </summary>
/// <remarks>
/// <para>
/// Off until set up, and it sends only the alert itself (see
/// <see cref="ForwardPayload"/>). Each alert is tried at most three times - at
/// once, after half a minute, after two minutes - and only for failures that
/// might pass: no answer, a server error, "too many requests". After that, or
/// at once for an answer that will not change, it is recorded as failed and
/// left; nothing is retried for ever, and nothing queues without end.
/// </para>
/// <para>
/// A webhook's address can hold its secret, and an ntfy topic is a password,
/// so neither is ever written to the log or the record: only the host.
/// </para>
/// </remarks>
public sealed partial class HttpAlertForwarder : IAlertForwarder, IDisposable
{
    /// <summary>The most alerts waiting to be forwarded; more are recorded as failed.</summary>
    public const int MostWaiting = 50;

    private static readonly TimeSpan[] Waits = [TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const string Interrupted = "Windsock closed before it was sent.";

    private readonly WindsockSettings _settings;
    private readonly IAlertLog _log;
    private readonly ILogger<HttpAlertForwarder> _logger;
    private readonly HttpClient _http;
    private readonly Channel<AlertRecord> _waiting = Channel.CreateBounded<AlertRecord>(
        new BoundedChannelOptions(MostWaiting) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private Task? _worker;
    private bool _disposed;

    public HttpAlertForwarder(WindsockSettings settings, IAlertLog log, ILogger<HttpAlertForwarder> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _log = log;
        _logger = logger;
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = false,
            ConnectCallback = ConnectAsync,
        })
        {
            Timeout = Timeout,
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Windsock", typeof(HttpAlertForwarder).Assembly.GetName().Version?.ToString(3) ?? "0"));
    }

    /// <inheritdoc />
    public bool IsReady => _settings.Alerts.Forward.Enabled && ForwardTarget.Problem(_settings.Alerts.Forward) is null;

    /// <inheritdoc />
    public void Forward(AlertRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!_waiting.Writer.TryWrite(record))
        {
            Note(record.Id, ForwardOutcome.Failed, "Not forwarded: too many alerts were already waiting.");
            return;
        }

        lock (_gate)
        {
            if (!_disposed)
            {
                _worker ??= Task.Run(WorkAsync);
            }
        }
    }

    /// <summary>
    /// Sends a test alert, and says what went wrong, or nothing when it arrived.
    /// For the Alerts page's test button, which can try a destination before it
    /// is saved.
    /// </summary>
    /// <param name="candidate">The destination to try, or nothing for the one in use.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    public async Task<string?> TestAsync(ForwardSettings? candidate = null, CancellationToken cancellationToken = default)
    {
        ForwardSettings forward = candidate ?? _settings.Alerts.Forward;

        if (ForwardTarget.Problem(forward) is { } problem)
        {
            return problem;
        }

        AlertRecord test = new(0, DateTimeOffset.Now, "test", "Forwarding works", "This is how Windsock's alerts will arrive here.");
        (bool sent, _, string detail) = await SendOnceAsync(test, forward, cancellationToken).ConfigureAwait(false);

        return sent ? null : detail;
    }

    public void Dispose()
    {
        // Registered under two names, so the host hands it back twice.
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _waiting.Writer.TryComplete();
        _stopping.Cancel();

        // What never left the queue is said so too; the next start sweeps up the rest.
        while (_waiting.Reader.TryRead(out AlertRecord? left))
        {
            Note(left.Id, ForwardOutcome.Failed, Interrupted);
        }

        _http.Dispose();
        _stopping.Dispose();
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (AlertRecord record in _waiting.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                try
                {
                    await DeliverAsync(record, _stopping.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    // Closing while it was on its way: said so, rather than "being forwarded" for ever.
                    Note(record.Id, ForwardOutcome.Failed, Interrupted);
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Opens the connection for a request, refusing plain http to anything not
    /// on this PC or the local network - checked against the addresses the name
    /// resolves to now, at the moment of sending, so a name a DNS suffix sends
    /// out to the internet is caught, and the connection goes to the very
    /// address that was checked.
    /// </summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        bool plain = string.Equals(context.InitialRequestMessage.RequestUri?.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        string host = context.DnsEndPoint.Host;

        IPAddress[] addresses = IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        if (plain)
        {
            addresses = [.. addresses.Where(ForwardTarget.IsPrivate)];

            if (addresses.Length == 0)
            {
                throw new PlainHttpRefusedException();
            }
        }

        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Plain http was asked of an address beyond this PC and the local network.</summary>
    private sealed class PlainHttpRefusedException : IOException
    {
        public PlainHttpRefusedException()
            : base("Plain http only goes to this PC or the local network.")
        {
        }
    }

    private async Task DeliverAsync(AlertRecord record, CancellationToken cancellationToken)
    {
        string detail = string.Empty;

        foreach (TimeSpan wait in Waits)
        {
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            ForwardSettings forward = _settings.Alerts.Forward;

            if (!forward.Enabled || ForwardTarget.Problem(forward) is not null)
            {
                Note(record.Id, ForwardOutcome.Failed, "Not forwarded: forwarding was turned off or changed before it went.");
                return;
            }

            (bool sent, bool worthRetrying, string said) = await SendOnceAsync(record, forward, cancellationToken).ConfigureAwait(false);

            if (sent)
            {
                Note(record.Id, ForwardOutcome.Sent, string.Empty);
                return;
            }

            detail = said;

            if (!worthRetrying)
            {
                break;
            }
        }

        LogFailed(ForwardTarget.Describe(_settings.Alerts.Forward), detail);
        Note(record.Id, ForwardOutcome.Failed, detail);
    }

    /// <summary>One attempt: whether it went, whether another might, and what happened in words.</summary>
    private async Task<(bool Sent, bool WorthRetrying, string Detail)> SendOnceAsync(
        AlertRecord record,
        ForwardSettings forward,
        CancellationToken cancellationToken)
    {
        if (ForwardTarget.Address(forward) is not { } address)
        {
            return (false, false, "The destination is not set up.");
        }

        string host = ForwardTarget.Describe(forward);
        string body = forward.To == ForwardKind.Webhook
            ? ForwardPayload.Webhook(record, forward.SenderName)
            : ForwardPayload.Ntfy(record, forward.NtfyTopic, forward.SenderName);

        using HttpRequestMessage request = new(HttpMethod.Post, address)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (forward.To == ForwardKind.Ntfy && !string.IsNullOrWhiteSpace(forward.NtfyToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", forward.NtfyToken.Trim());
        }

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return (true, false, string.Empty);
            }

            int code = (int)response.StatusCode;
            bool passing = code >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.RequestTimeout;

            return (false, passing, string.Concat(host, " answered ", code.ToString(System.Globalization.CultureInfo.InvariantCulture), " ", response.ReasonPhrase ?? string.Empty).TrimEnd() + ".");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, true, string.Concat(host, " did not answer within ", Timeout.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture), " seconds."));
        }
        catch (HttpRequestException ex) when (ex.InnerException is PlainHttpRefusedException)
        {
            return (false, false, string.Concat(host, " is not on this PC or your own network, so nothing was sent over plain http. Use https."));
        }
        catch (HttpRequestException)
        {
            // The exception's own text can quote the address; it is not passed on.
            return (false, true, string.Concat("Could not reach ", host, "."));
        }
    }

    private void Note(long id, ForwardOutcome outcome, string detail)
    {
        if (id <= 0)
        {
            return;
        }

        try
        {
            _log.SetForward(id, outcome, detail);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            // The record is a convenience; the alert was still sent, or not.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An alert could not be forwarded to {Host}: {Detail}")]
    private partial void LogFailed(string host, string detail);
}
