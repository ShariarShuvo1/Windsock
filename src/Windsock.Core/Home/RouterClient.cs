using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Windsock.Core.Home;

/// <summary>Talks UPnP to the router, and only to the router.</summary>
public interface IRouterClient
{
    /// <summary>
    /// Asks the router at a link's gateway address whether it is an Internet
    /// Gateway Device, and reads its description. Nothing when it does not
    /// answer, or answers with anything but itself. Throws only when cancelled.
    /// </summary>
    Task<GatewayDescription?> DescribeAsync(HomeLink link, CancellationToken cancellationToken);

    /// <summary>
    /// Posts one read-only action to a service on the router. A garbled answer
    /// when it could not be reached. Throws only when cancelled.
    /// </summary>
    Task<SoapAnswer> CallAsync(GatewayService service, string action, IReadOnlyList<(string Name, string Value)>? arguments, CancellationToken cancellationToken);
}

/// <summary>The router client over a plain HTTP client, held to the router's own address.</summary>
/// <remarks>
/// No redirects, no proxy, a three-second connection limit, five seconds in
/// all, and a megabyte at most read back. The search goes to the router's own
/// address on UDP 1900 rather than to the multicast group, so the rest of the
/// network never hears it; a router that answers only multicast searches is
/// reported as not answering.
/// </remarks>
public sealed class RouterClient : IRouterClient, IDisposable
{
    private static readonly TimeSpan SearchWait = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;

    public RouterClient()
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxResponseHeadersLength = 16,
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = 1024 * 1024,
        };
    }

    /// <inheritdoc />
    public async Task<GatewayDescription?> DescribeAsync(HomeLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (link.Gateway is not { } gateway || link.Address is null)
        {
            return null;
        }

        Uri? location = await SearchAsync(link.Address, gateway, cancellationToken).ConfigureAwait(false);

        if (location is null)
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await _http.GetAsync(location, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return UpnpWire.Describe(xml, location, gateway);
        }
        catch (Exception ex) when (Unreachable(ex, cancellationToken))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<SoapAnswer> CallAsync(
        GatewayService service,
        string action,
        IReadOnlyList<(string Name, string Value)>? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        if (!action.StartsWith("Get", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only read-only actions are ever sent to the router.", nameof(action));
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, service.ControlUrl)
            {
                Content = new StringContent(UpnpWire.Envelope(service.ServiceType, action, arguments), Encoding.UTF8, "text/xml"),
            };

            request.Headers.TryAddWithoutValidation("SOAPAction", UpnpWire.ActionHeader(service.ServiceType, action));

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return UpnpWire.Answer(xml, action);
        }
        catch (Exception ex) when (Unreachable(ex, cancellationToken))
        {
            return SoapAnswer.Garbled;
        }
    }

    private static async Task<Uri?> SearchAsync(IPAddress from, IPAddress gateway, CancellationToken cancellationToken)
    {
        try
        {
            using UdpClient udp = new(new IPEndPoint(from, 0));
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(SearchWait);

            await udp.SendAsync(UpnpWire.Search(), new IPEndPoint(gateway, UpnpWire.SsdpPort), wait.Token).ConfigureAwait(false);

            while (true)
            {
                UdpReceiveResult answer = await udp.ReceiveAsync(wait.Token).ConfigureAwait(false);

                if (answer.RemoteEndPoint.Address.Equals(gateway)
                    && UpnpWire.Location(Encoding.ASCII.GetString(answer.Buffer), gateway) is { } location)
                {
                    return location;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static bool Unreachable(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or IOException or InvalidOperationException
        || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    public void Dispose() => _http.Dispose();
}
