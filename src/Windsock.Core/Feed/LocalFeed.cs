using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.Settings;

namespace Windsock.Core.Feed;

/// <summary>Whether the local feed is running.</summary>
public enum FeedState
{
    /// <summary>Switched off.</summary>
    Off,

    /// <summary>Listening.</summary>
    Running,

    /// <summary>Switched on, but it could not listen: see <see cref="FeedStatus.Problem"/>.</summary>
    Failed,
}

/// <summary>Where the local feed stands.</summary>
/// <param name="State">Off, running or failed.</param>
/// <param name="Port">The port it listens on, or would.</param>
/// <param name="Problem">Why it failed, in a sentence, when it did.</param>
/// <param name="LiveClients">How many clients are following the live events.</param>
public sealed record FeedStatus(FeedState State, int Port, string? Problem, int LiveClients);

/// <summary>
/// The local, read-only status feed: JSON and Server-Sent Events on
/// 127.0.0.1 and ::1, for Rainmeter, Stream Deck, Home Assistant, an OBS
/// overlay, the command line and the AI tools.
/// </summary>
/// <remarks>
/// <para>
/// Windows' own HTTP server (HTTP.sys, through HttpListener) rather than
/// Kestrel, which would add 26 MiB to every download for a feed that only ever
/// answers GET: measured, HttpListener needs no administrator for a loopback
/// address, answers in under a millisecond once warm, and streams events with
/// chunked encoding. Only the two address literals are registered, never
/// "localhost": measured on the development machine, a <c>localhost</c> prefix
/// makes HTTP.sys listen on every address (<c>[::]:port</c>) and a connection
/// from the LAN is accepted, while <c>127.0.0.1</c> and <c>[::1]</c> listen on
/// loopback alone and the LAN is refused. Requests addressed to "localhost"
/// still arrive, since Windows routes an address-literal registration by the
/// address the connection reached.
/// </para>
/// <para>
/// Off by default. Every request passes <see cref="FeedGuard"/>: from this PC,
/// GET or HEAD, the Host header naming the feed (DNS rebinding), no other
/// site's page, and the token. Nothing it answers changes anything. While it
/// has clients it holds the monitors behind its figures awake, and lets them
/// go thirty seconds after the last request.
/// </para>
/// </remarks>
public sealed partial class LocalFeed : IHostedService, IDisposable
{
    /// <summary>The most clients following the live events at once.</summary>
    public const int MostLiveClients = 8;

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan KeepAwake = TimeSpan.FromSeconds(30);
    private static readonly byte[] Retry = Encoding.UTF8.GetBytes("retry: 2000\n\n");

    private readonly WindsockSettings _settings;
    private readonly IFeedSource _source;
    private readonly TimeProvider _time;
    private readonly ILogger<LocalFeed> _logger;
    private readonly string _endpointPath;
    private readonly string _version;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<Guid, Channel<byte[]>> _live = new();

    private FeedStatus _state;
    private HttpListener? _listener;
    private CancellationTokenSource? _stop;
    private FeedEndpoint? _endpoint;
    private IDisposable? _watch;
    private long _lastRequest;
    private long _eventId;
    private bool _disposed;

    public LocalFeed(WindsockSettings settings, IFeedSource source, TimeProvider time, ILogger<LocalFeed> logger)
        : this(settings, source, time, logger, FeedEndpoint.DefaultPath)
    {
    }

    internal LocalFeed(WindsockSettings settings, IFeedSource source, TimeProvider time, ILogger<LocalFeed> logger, string endpointPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(source);

        _settings = settings;
        _source = source;
        _time = time;
        _logger = logger;
        _endpointPath = endpointPath;
        _version = VersionOf(typeof(LocalFeed).Assembly);
        _state = new FeedStatus(FeedState.Off, settings.Feed.PortInUse, null, 0);
    }

    /// <summary>Raised, on any thread, when <see cref="Status"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Where the feed stands. Put together under the lock from the state Apply
    /// last set and the live clients now, so a stream ending as the feed stops
    /// cannot write an old "Running" back over "Off".
    /// </summary>
    public FeedStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _state with { LiveClients = _live.Count };
            }
        }
    }

    /// <summary>
    /// The feed's address and token, for the settings page to show and copy;
    /// nothing until the feed has been switched on once.
    /// </summary>
    public FeedEndpoint? Endpoint
    {
        get
        {
            lock (_gate)
            {
                return _endpoint ?? FeedEndpoint.Read(_endpointPath);
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Apply();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            StopLocked(writeEndpoint: true);
            _state = new FeedStatus(FeedState.Off, _settings.Feed.PortInUse, null, 0);
        }

        Publish();
        return Task.CompletedTask;
    }

    /// <summary>Starts, stops or moves the feed to match the settings.</summary>
    public void Apply()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            FeedSettings wanted = _settings.Feed;
            int port = wanted.PortInUse;

            if (!wanted.Enabled)
            {
                StopLocked(writeEndpoint: _listener is not null);
                _state = new FeedStatus(FeedState.Off, port, null, 0);
            }
            else if (_listener is not null && _endpoint?.Port == port)
            {
                return;
            }
            else
            {
                StopLocked(writeEndpoint: false);
                _state = StartLocked(port);
            }
        }

        Publish();
    }

    /// <summary>
    /// Makes a new token and restarts the feed with it, so every client that
    /// had the old one has to be given the new one.
    /// </summary>
    public void ResetToken()
    {
        lock (_gate)
        {
            FeedEndpoint current = _endpoint ?? FeedEndpoint.Prepare(_endpointPath, _settings.Feed.PortInUse);
            _endpoint = current.WithNewToken();

            try
            {
                _endpoint.Write(_endpointPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogEndpointNotWritten(ex);
            }

            foreach (Channel<byte[]> live in _live.Values)
            {
                live.Writer.TryComplete();
            }
        }
    }

    private FeedStatus StartLocked(int port)
    {
        FeedEndpoint endpoint = (_endpoint is { } known ? known with { Port = port } : FeedEndpoint.Prepare(_endpointPath, port))
            with { Running = true, ProcessId = Environment.ProcessId };

        HttpListener listener = new() { IgnoreWriteExceptions = true };

        foreach (string path in FeedRoutes.Prefixes)
        {
            listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}{path}"));
            listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://[::1]:{port}{path}"));
        }

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            listener.Close();
            LogStartFailed(ex, port);
            _endpoint = endpoint with { Running = false, ProcessId = null };
            WriteEndpoint();

            return new FeedStatus(FeedState.Failed, port, Problem(ex, port), 0);
        }

        _listener = listener;
        _endpoint = endpoint;
        _stop = new CancellationTokenSource();
        WriteEndpoint();

        CancellationToken stop = _stop.Token;
        _ = Task.Run(() => AcceptAsync(listener, stop), CancellationToken.None);
        _ = Task.Run(() => PumpAsync(stop), CancellationToken.None);

        LogStarted(port);
        return new FeedStatus(FeedState.Running, port, null, 0);
    }

    private void StopLocked(bool writeEndpoint)
    {
        if (_listener is null)
        {
            return;
        }

        _stop?.Cancel();

        foreach (Channel<byte[]> live in _live.Values)
        {
            live.Writer.TryComplete();
        }

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        _listener = null;
        _stop?.Dispose();
        _stop = null;
        _watch?.Dispose();
        _watch = null;

        if (_endpoint is not null)
        {
            _endpoint = _endpoint with { Running = false, ProcessId = null };

            if (writeEndpoint)
            {
                WriteEndpoint();
            }
        }

        LogStopped();
    }

    private void WriteEndpoint()
    {
        try
        {
            _endpoint?.Write(_endpointPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogEndpointNotWritten(ex);
        }
    }

    private static string Problem(HttpListenerException ex, int port) => ex.ErrorCode switch
    {
        5 => "Windows refused the feed its address.",
        32 or 183 => string.Create(CultureInfo.InvariantCulture, $"Another program already answers on port {port}, or on part of the feed's address there. Choose another port."),
        _ => string.Create(CultureInfo.InvariantCulture, $"Windows could not start the feed on port {port} ({ex.ErrorCode})."),
    };

    private async Task AcceptAsync(HttpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => AnswerAsync(context, stop), CancellationToken.None);
        }
    }

    private async Task AnswerAsync(HttpListenerContext context, CancellationToken stop)
    {
        try
        {
            await RouteAsync(context, stop).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            Abort(context);
        }
#pragma warning disable CA1031 // The last resort: no request is ever left open, and nothing escapes as an unobserved task.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogAnswerFailed(ex);
            await FailAsync(context).ConfigureAwait(false);
        }
    }

    /// <summary>Answers 500 to a request whose answer went wrong, or drops it when even that cannot be sent.</summary>
    private static async Task FailAsync(HttpListenerContext context)
    {
        try
        {
            await ReplyAsync(context, 500, Json, FeedJson.WriteError("Something went wrong answering that."), head: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            Abort(context);
        }
    }

    private async Task RouteAsync(HttpListenerContext context, CancellationToken stop)
    {
        HttpListenerRequest request = context.Request;
        FeedEndpoint? endpoint;

        lock (_gate)
        {
            endpoint = _endpoint;
        }

        if (endpoint is null)
        {
            Abort(context);
            return;
        }

        FeedVerdict verdict = FeedGuard.Check(
            request.RemoteEndPoint?.Address,
            request.HttpMethod,
            request.Headers["Host"],
            request.Headers["Origin"],
            request.Headers["Sec-Fetch-Site"],
            request.Headers["Sec-Fetch-Mode"],
            request.Headers["Authorization"],
            request.QueryString[FeedGuard.TokenParameter],
            endpoint.Port,
            endpoint.Token,
            _settings.Feed.TokenInAddress);

        bool head = request.HttpMethod == "HEAD";

        if (verdict != FeedVerdict.Allowed)
        {
            if (verdict == FeedVerdict.NoToken)
            {
                context.Response.AddHeader("WWW-Authenticate", "Bearer realm=\"Windsock\"");
            }

            await ReplyAsync(context, FeedGuard.StatusOf(verdict), Json, FeedJson.WriteError(Refusal(verdict)), head).ConfigureAwait(false);
            return;
        }

        Interlocked.Exchange(ref _lastRequest, _time.GetTimestamp());
        FeedRoute route = FeedRoutes.Find(request.Url?.AbsolutePath);

        switch (route)
        {
            case FeedRoute.Index:
                await ReplyAsync(context, 200, Json, FeedJson.WriteIndex(_time.GetLocalNow(), _version), head).ConfigureAwait(false);
                return;

            case FeedRoute.Events when !head:
                await StreamAsync(context, stop).ConfigureAwait(false);
                return;

            case FeedRoute.Overlay:
                context.Response.AddHeader("Content-Security-Policy", FeedOverlay.Policy);
                await ReplyAsync(context, 200, "text/html; charset=utf-8", FeedOverlay.Page, head).ConfigureAwait(false);
                return;

            case FeedRoute.Usage when HasPeriod(request):
                await UsageAsync(context, head).ConfigureAwait(false);
                return;
        }

        if (FeedRoutes.PartOf(route) is { } part)
        {
            await ReplyAsync(context, 200, Json, FeedJson.Write(_source.Read(), part, _version), head).ConfigureAwait(false);
            return;
        }

        await ReplyAsync(context, route == FeedRoute.Events ? 405 : 404, Json, FeedJson.WriteError("Nothing here. Ask / for what the feed offers."), head).ConfigureAwait(false);
    }

    private static bool HasPeriod(HttpListenerRequest request) =>
        request.QueryString["period"] is not null
        || request.QueryString["month"] is not null
        || request.QueryString["day"] is not null
        || request.QueryString["from"] is not null
        || request.QueryString["until"] is not null
        || request.QueryString["by"] is not null;

    private async Task UsageAsync(HttpListenerContext context, bool head)
    {
        var query = context.Request.QueryString;
        UsagePeriod? period = UsagePeriod.Read(
            query["period"],
            query["month"],
            query["day"],
            query["from"],
            query["until"],
            query["by"],
            _time.GetLocalNow(),
            TimeZoneInfo.Local,
            out string? problem);

        if (period is null)
        {
            await ReplyAsync(context, 400, Json, FeedJson.WriteError(problem ?? "That period cannot be read."), head).ConfigureAwait(false);
            return;
        }

        FeedUsageRange? range = _source.ReadUsage(period.From, period.Until, period.Granularity);

        if (range is null)
        {
            await ReplyAsync(context, 503, Json, FeedJson.WriteError("History cannot be read just now."), head).ConfigureAwait(false);
            return;
        }

        await ReplyAsync(context, 200, Json, FeedJson.WriteUsage(range, _time.GetLocalNow(), _version), head).ConfigureAwait(false);
    }

    /// <summary>Follows the status as Server-Sent Events: one <c>status</c> event a second until the client goes.</summary>
    private async Task StreamAsync(HttpListenerContext context, CancellationToken stop)
    {
        Guid id = Guid.NewGuid();
        Channel<byte[]> events = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        // The count and the add under one lock, so eight clients arriving at
        // once cannot all pass the check.
        bool admitted;

        lock (_gate)
        {
            admitted = _live.Count < MostLiveClients;

            if (admitted)
            {
                _live[id] = events;
            }
        }

        if (!admitted)
        {
            await ReplyAsync(context, 503, Json, FeedJson.WriteError("Too many clients are following the feed already."), head: false).ConfigureAwait(false);
            return;
        }

        Publish();

        HttpListenerResponse response = context.Response;

        try
        {
            response.StatusCode = 200;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.SendChunked = true;
            AddCommonHeaders(response);

            Stream output = response.OutputStream;
            await output.WriteAsync(Retry, stop).ConfigureAwait(false);
            await output.WriteAsync(Event(_source.Read()), stop).ConfigureAwait(false);
            await output.FlushAsync(stop).ConfigureAwait(false);

            await foreach (byte[] each in events.Reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
                await output.WriteAsync(each, stop).ConfigureAwait(false);
                await output.FlushAsync(stop).ConfigureAwait(false);
            }

            response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            Abort(context);
        }
        finally
        {
            lock (_gate)
            {
                _live.TryRemove(id, out _);
            }

            Publish();
        }
    }

    /// <summary>
    /// Once a second while the feed runs: keeps the monitors awake while anyone
    /// is using the feed, and sends the live clients the status.
    /// </summary>
    private async Task PumpAsync(CancellationToken stop)
    {
        using PeriodicTimer timer = new(Tick, _time);

        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                long last = Interlocked.Read(ref _lastRequest);
                bool wanted = !_live.IsEmpty || (last != 0 && _time.GetElapsedTime(last) < KeepAwake);

                lock (_gate)
                {
                    if (stop.IsCancellationRequested)
                    {
                        return;
                    }

                    if (wanted && _watch is null)
                    {
                        _watch = _source.Watch();
                    }
                    else if (!wanted && _watch is not null)
                    {
                        _watch.Dispose();
                        _watch = null;
                    }
                }

                if (_live.IsEmpty)
                {
                    continue;
                }

                byte[] message = Event(_source.Read());

                foreach (Channel<byte[]> live in _live.Values)
                {
                    live.Writer.TryWrite(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One status event: an id, the event name, and the snapshot as one line of JSON.</summary>
    private byte[] Event(FeedSnapshot snapshot)
    {
        long id = Interlocked.Increment(ref _eventId);
        byte[] json = FeedJson.Write(snapshot, FeedPart.All, _version);
        byte[] head = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"id: {id}\nevent: status\ndata: "));
        byte[] message = new byte[head.Length + json.Length + 2];

        head.CopyTo(message, 0);
        json.CopyTo(message, head.Length);
        message[^2] = (byte)'\n';
        message[^1] = (byte)'\n';
        return message;
    }

    private const string Json = "application/json; charset=utf-8";

    private static async Task ReplyAsync(HttpListenerContext context, int status, string contentType, byte[] body, bool head)
    {
        HttpListenerResponse response = context.Response;
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = body.Length;
        AddCommonHeaders(response);

        if (!head)
        {
            await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        }

        response.Close();
    }

    /// <summary>
    /// What every answer carries: never cached, never sniffed, and no referrer
    /// sent onwards, since an address may hold the token. No CORS header, ever:
    /// without one, a browser withholds the answer from any other site's page.
    /// </summary>
    private static void AddCommonHeaders(HttpListenerResponse response)
    {
        response.AddHeader("Cache-Control", "no-store");
        response.AddHeader("X-Content-Type-Options", "nosniff");
        response.AddHeader("Referrer-Policy", "no-referrer");
    }

    private static string Refusal(FeedVerdict verdict) => verdict switch
    {
        FeedVerdict.NotLocal => "The feed answers this PC only.",
        FeedVerdict.WrongMethod => "The feed only reads: GET or HEAD.",
        FeedVerdict.WrongHost => "Ask the feed by its own address: 127.0.0.1, localhost or [::1].",
        FeedVerdict.ForeignPage => "The feed does not answer other sites' pages.",
        _ => "This needs the feed's token, from Windsock's settings.",
    };

    private static void Abort(HttpListenerContext context)
    {
        try
        {
            context.Response.Abort();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    /// <summary>Says the status changed; readers take <see cref="Status"/>, which is put together under the lock.</summary>
    private void Publish() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>The version a build carries, without the source revision after a plus.</summary>
    public static string VersionOf(Assembly assembly)
    {
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string version = informational ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? version[..plus] : version;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopLocked(writeEndpoint: true);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Local feed listening on loopback port {Port}")]
    private partial void LogStarted(int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Local feed stopped")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Local feed could not listen on port {Port}")]
    private partial void LogStartFailed(Exception exception, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The feed's address file could not be written")]
    private partial void LogEndpointNotWritten(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A feed request failed and was answered 500")]
    private partial void LogAnswerFailed(Exception exception);
}
