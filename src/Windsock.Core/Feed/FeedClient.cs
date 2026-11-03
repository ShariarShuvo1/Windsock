using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace Windsock.Core.Feed;

/// <summary>How a request to the local feed went.</summary>
public enum FeedReach
{
    /// <summary>It answered.</summary>
    Answered,

    /// <summary>The feed has never been switched on, so there is no address or token to use.</summary>
    NeverSwitchedOn,

    /// <summary>Nothing is listening: Windsock is not running, or its feed is off.</summary>
    NotRunning,

    /// <summary>The feed refused: the token is out of date, most likely.</summary>
    Refused,

    /// <summary>It answered with something else, or not in time.</summary>
    Failed,
}

/// <summary>What the feed said.</summary>
/// <param name="Reach">How the request went.</param>
/// <param name="Status">The HTTP status, or 0 when nothing answered.</param>
/// <param name="Body">The answer's text, when there was one.</param>
public sealed record FeedReply(FeedReach Reach, int Status, string? Body)
{
    /// <summary>What went wrong, in a sentence a person can act on; empty when it answered.</summary>
    public string Problem => Reach switch
    {
        FeedReach.NeverSwitchedOn => "Windsock's local feed has never been switched on. Turn it on in Windsock, under Settings, then try again.",
        FeedReach.NotRunning => "Windsock is not running, or its local feed is off. Start Windsock and turn the feed on in its settings.",
        FeedReach.Refused => "The local feed refused the request. Its token may have been reset; this reads the current one from feed.json.",
        FeedReach.Failed => Status == 0 ? "The local feed did not answer in time." : $"The local feed answered with {Status}.",
        _ => string.Empty,
    };
}

/// <summary>
/// Asks the local feed of the Windsock running on this PC, finding its port
/// and token in feed.json: what the command line and the AI tools use.
/// </summary>
/// <remarks>
/// The address and token are looked up for every request, not once: the AI
/// tools server runs for as long as the assistant does, through a token reset,
/// a new port, or the feed being switched on for the first time after it
/// started. Reading feed.json is a few hundred bytes.
/// </remarks>
public sealed class FeedClient : IDisposable
{
    private readonly Func<FeedEndpoint?> _find;
    private readonly HttpClient _http;

    /// <summary>A client for one endpoint, as given.</summary>
    public FeedClient(FeedEndpoint? endpoint)
        : this(() => endpoint)
    {
    }

    /// <summary>A client that asks for the endpoint afresh before every request.</summary>
    public FeedClient(Func<FeedEndpoint?> find)
    {
        ArgumentNullException.ThrowIfNull(find);

        _find = find;
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>A client for the feed named in the data folder's feed.json, read again for every request.</summary>
    public static FeedClient FromDataFolder() => new(() => FeedEndpoint.Read(FeedEndpoint.DefaultPath));

    /// <summary>Whether there is an address to ask at all, now.</summary>
    public bool IsKnown => _find() is not null;

    /// <summary>Asks for one address, such as "/v1/status", waiting at most five seconds.</summary>
    public async Task<FeedReply> GetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(pathAndQuery);

        if (_find() is not { } endpoint)
        {
            return new FeedReply(FeedReach.NeverSwitchedOn, 0, null);
        }

        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            using HttpRequestMessage request = Request(endpoint, pathAndQuery);
            using HttpResponseMessage response = await _http.SendAsync(request, wait.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(wait.Token).ConfigureAwait(false);

            return new FeedReply(Reach(response.StatusCode), (int)response.StatusCode, body);
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
        {
            return new FeedReply(FeedReach.NotRunning, 0, null);
        }
        catch (HttpRequestException)
        {
            return new FeedReply(FeedReach.NotRunning, 0, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new FeedReply(FeedReach.Failed, 0, null);
        }
    }

    /// <summary>
    /// Follows the live events, yielding each status as its line of JSON, until
    /// cancelled or the feed goes. A refusal or an absent feed yields nothing;
    /// <paramref name="ended"/> says why it stopped.
    /// </summary>
    public async IAsyncEnumerable<string> FollowAsync(StrongBox<FeedReply> ended, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ended);

        if (_find() is not { } endpoint)
        {
            ended.Value = new FeedReply(FeedReach.NeverSwitchedOn, 0, null);
            yield break;
        }

        HttpResponseMessage? response = null;
        ended.Value = new FeedReply(FeedReach.Answered, 200, null);

        try
        {
            using HttpRequestMessage request = Request(endpoint, "/v1/events");
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            ended.Value = new FeedReply(FeedReach.NotRunning, 0, null);
        }

        if (response is null)
        {
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                ended.Value = new FeedReply(Reach(response.StatusCode), (int)response.StatusCode, null);
                yield break;
            }

            Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using StreamReader reader = new(stream, Encoding.UTF8);
            StringBuilder data = new();

            while (true)
            {
                string? line;

                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    line = null;
                }

                if (line is null)
                {
                    ended.Value = new FeedReply(FeedReach.NotRunning, 0, null);
                    yield break;
                }

                if (line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        yield return data.ToString();
                        data.Clear();
                    }

                    continue;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(line.AsSpan(5).TrimStart(' '));
                }
            }
        }
    }

    private static HttpRequestMessage Request(FeedEndpoint endpoint, string pathAndQuery)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(endpoint.Address, pathAndQuery));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static FeedReach Reach(HttpStatusCode status) => status switch
    {
        HttpStatusCode.OK => FeedReach.Answered,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.MisdirectedRequest => FeedReach.Refused,
        _ => FeedReach.Failed,
    };

    public void Dispose() => _http.Dispose();
}
