using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.Processes;

namespace Windsock.Core.Timeline;

/// <summary>
/// Listens to the DNS client's own events, so each app's name lookups can be
/// shown. Full edition, as administrator, in memory only.
/// </summary>
/// <remarks>
/// <para>
/// Provider <c>Microsoft-Windows-DNS-Client</c>
/// (<c>{1C95126E-7EEA-49A9-A3FE-A378B03DDB4D}</c>). Its registered manifest on
/// build 26200 gives event 3006 (query started: QueryName, QueryType) and 3008
/// (query finished: QueryStatus, QueryResults) <b>no</b> process field, so the
/// process is the event's own sender; 3009 to 3020 from version 1 carry
/// <c>ClientPID</c>, the process the DNS service answered for, which is taken
/// as given. An event sent by the DNS service itself without a ClientPID names
/// the service, not an app, and is left out.
/// </para>
/// <para>
/// A second real-time session beside the kernel trace, started only while that
/// runs - Windows opens either only for an administrator. Which events the
/// querying process sends and which the DNS service sends has not been seen
/// live: the development machine's shell is not elevated. The Operational log
/// is not used: it is off by default and turning it on is a lasting change to
/// the system.
/// </para>
/// </remarks>
public sealed partial class EtwDnsLookupSource : BackgroundService
{
    private const string SessionName = "Windsock-dns";

    /// <summary>Microsoft-Windows-DNS-Client.</summary>
    internal static readonly Guid Provider = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");

    internal const int QueryStarted = 3006;
    internal const int QueryFinished = 3008;
    internal const int CacheAnswered = 3020;

    private static readonly TimeSpan Look = TimeSpan.FromSeconds(15);

    private readonly IProcessTrafficSource _trace;
    private readonly DnsLookupLog _log;
    private readonly Func<int, bool> _isResolver;
    private readonly ILogger<EtwDnsLookupSource> _logger;
    private readonly Lock _gate = new();
    private TraceEventSession? _session;
    private Thread? _worker;
    private volatile bool _stopping;

    /// <param name="trace">The kernel trace; this session runs only while it does.</param>
    /// <param name="log">Where lookups are noted.</param>
    /// <param name="isResolver">Whether a process is the DNS service's own host, whose events without a ClientPID name no app.</param>
    /// <param name="logger">The log.</param>
    public EtwDnsLookupSource(IProcessTrafficSource trace, DnsLookupLog log, Func<int, bool> isResolver, ILogger<EtwDnsLookupSource> logger)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(isResolver);
        ArgumentNullException.ThrowIfNull(logger);

        _trace = trace;
        _log = log;
        _isResolver = isResolver;
        _logger = logger;
    }

    /// <summary>Whether lookups are being heard now.</summary>
    public bool IsListening
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                bool wanted = _trace.State == TrafficSourceState.Running;

                if (wanted && !IsListening)
                {
                    Start();
                }
                else if (!wanted && IsListening)
                {
                    Stop();
                }

                await Task.Delay(Look, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Stop();
        }
    }

    private void Start()
    {
        try
        {
            try
            {
                TraceEventSession.GetActiveSession(SessionName)?.Stop(noThrow: true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }

            TraceEventSession session = new(SessionName) { StopOnDispose = true };
            session.EnableProvider(Provider, TraceEventLevel.Verbose, ulong.MaxValue);

            RegisteredTraceEventParser parser = new(session.Source);
            parser.All += Hear;

            _stopping = false;
            Thread worker = new(() => Pump(session))
            {
                IsBackground = true,
                Name = "Windsock-dns-trace",
            };

            lock (_gate)
            {
                _session = session;
                _worker = worker;
            }

            worker.Start();
            LogStarted();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            LogUnavailable(ex);
            Stop();
        }
    }

    private void Pump(TraceEventSession session)
    {
        try
        {
            session.Source.Process();
        }
        catch (Exception ex) when (!_stopping && ex is not OutOfMemoryException)
        {
            LogUnavailable(ex);
        }
    }

    private void Stop()
    {
        TraceEventSession? session;
        Thread? worker;

        lock (_gate)
        {
            session = _session;
            worker = _worker;
            _session = null;
            _worker = null;
        }

        if (session is null)
        {
            return;
        }

        _stopping = true;
        session.Dispose();
        worker?.Join(TimeSpan.FromSeconds(2));
    }

    private void Hear(TraceEvent data)
    {
        int id = (int)data.ID;

        if (id is not (QueryStarted or QueryFinished or CacheAnswered))
        {
            return;
        }

        string? name = data.PayloadByName("QueryName") as string;

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        int processId = Whose(data.ProcessID, Number(data.PayloadByName("ClientPID")));

        if (processId <= 0)
        {
            return;
        }

        int type = Number(data.PayloadByName("QueryType")) ?? 0;

        if (id == QueryStarted)
        {
            _log.Asked(processId, name, type);
            return;
        }

        int status = Number(data.PayloadByName(id == QueryFinished ? "QueryStatus" : "Status")) ?? 0;
        _log.Answered(processId, name, type, status, DnsAnswer.Parse(data.PayloadByName("QueryResults") as string));
    }

    /// <summary>
    /// The process a DNS event is about: the payload's ClientPID when it has
    /// one, otherwise the sender, unless the sender is the DNS service itself.
    /// </summary>
    internal int Whose(int sender, int? clientProcessId) =>
        clientProcessId is > 0 ? clientProcessId.Value
        : sender > 4 && !_isResolver(sender) ? sender
        : 0;

    private static int? Number(object? value)
    {
        try
        {
            return value is null ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public override void Dispose()
    {
        Stop();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening to the DNS client for each app's lookups")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "The DNS client's events could not be heard")]
    private partial void LogUnavailable(Exception exception);
}
