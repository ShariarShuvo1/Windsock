using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.Apps;
using Windsock.Core.History;
using Windsock.Core.Processes;
using Windsock.Core.Security;
using Windsock.Core.Settings;

namespace Windsock.Core.Insight;

/// <summary>A program that used the network for the first time Windsock knows of.</summary>
/// <param name="AttributionId">The app, named as Windows' data usage names it.</param>
/// <param name="Name">What to call it: the program's own description, else its file name.</param>
/// <param name="FilePath">The program's file, when Windows would say.</param>
/// <param name="ProcessId">The process seen using the network.</param>
/// <param name="RemoteAddress">An address it was talking to, when a connection showed it; null when only the trace saw bytes.</param>
/// <param name="Signature">Who signed the program; null when its file could not be found.</param>
/// <param name="At">When it was seen.</param>
public sealed record NewAppSighting(
    string AttributionId,
    string Name,
    string? FilePath,
    int ProcessId,
    string? RemoteAddress,
    SignatureInfo? Signature,
    DateTimeOffset At)
{
    /// <summary>The signer's name, or a few words saying there is none.</summary>
    public string PublisherText => Signature switch
    {
        { State: SignatureState.Trusted, Publisher: { } publisher } => publisher,
        { State: SignatureState.NoSignature } => "Not signed",
        { State: SignatureState.NotTrusted } => "Signature does not hold",
        { State: SignatureState.Unconfirmed, Publisher: { } publisher } => publisher + " (not confirmed offline)",
        _ => "Publisher not known",
    };
}

/// <summary>
/// Notices a program using the network for the first time, and says who
/// signed it.
/// </summary>
/// <remarks>
/// <para>
/// Every 15 seconds the connection tables are read - they name each
/// connection's process without administrator rights - and, while the kernel
/// trace runs, every process it counted bytes for. Each process is put to the
/// app Windows counts it as; an app neither in <see cref="SeenApps"/> nor in
/// history's last week is new. A process whose file Windows will not name to a
/// standard user (most service hosts) cannot be put to an app, and is skipped
/// rather than guessed at.
/// </para>
/// <para>
/// The very first run, and the first after all history is erased, learns
/// quietly: everything already in use is noted and nothing is called new, or
/// the first minute would bring a notice for every app on the PC. Windows'
/// figures in history count as seen, so an app that ran while Windsock was
/// closed is not new either.
/// </para>
/// <para>
/// Only raises <see cref="Sighted"/>; the notice itself is the app's to send.
/// It looks only while somebody wants to hear (the New app online alert is
/// on): reading the tables and checking signatures every 15 seconds for
/// nobody is a cost with no use. The first look after a stretch of not
/// looking learns quietly, so switching the alert on brings no burst of apps
/// that were merely used meanwhile.
/// </para>
/// </remarks>
public sealed partial class NewAppWatcher : BackgroundService
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HistoryReach = TimeSpan.FromDays(8);
    private const int Remembered = 20;

    private readonly IProcessConnectionDetailSource _connections;
    private readonly IProcessTrafficSource _traffic;
    private readonly IProcessIdentityResolver _identities;
    private readonly Func<DateTimeOffset, DateTimeOffset, IEnumerable<string>> _countedBetween;
    private readonly IUsageHistoryStore? _history;
    private readonly SignatureBook _signatures;
    private readonly SeenApps _seen;
    private readonly Func<int, string?> _countedAs;
    private readonly TimeProvider _clock;
    private readonly ILogger<NewAppWatcher> _logger;
    private readonly Dictionary<int, string?> _apps = [];
    private readonly Dictionary<int, ProcessTraffic> _bytes = [];
    private readonly List<NewAppSighting> _recent = [];
    private readonly Lock _gate = new();
    private readonly Func<bool> _wanted;
    private volatile bool _learning;
    private bool _historyLearned;
    private bool _idle;

    public NewAppWatcher(
        IProcessConnectionDetailSource connections,
        IProcessTrafficSource traffic,
        IProcessIdentityResolver identities,
        IAppUsageStore appUsage,
        IUsageHistoryStore history,
        SignatureBook signatures,
        SeenApps seen,
        WindsockSettings settings,
        ILogger<NewAppWatcher> logger)
        : this(
            connections,
            traffic,
            identities,
            (from, until) => appUsage.ReadAppTotals(from, until).Select(static each => each.AttributionId),
            history,
            signatures,
            seen,
            CountedAs,
            TimeProvider.System,
            logger,
            () => settings.Insight.NewAppNotices)
    {
        ArgumentNullException.ThrowIfNull(appUsage);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(settings);
    }

    internal NewAppWatcher(
        IProcessConnectionDetailSource connections,
        IProcessTrafficSource traffic,
        IProcessIdentityResolver identities,
        Func<DateTimeOffset, DateTimeOffset, IEnumerable<string>> countedBetween,
        IUsageHistoryStore? history,
        SignatureBook signatures,
        SeenApps seen,
        Func<int, string?> countedAs,
        TimeProvider clock,
        ILogger<NewAppWatcher> logger,
        Func<bool>? wanted = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(traffic);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(countedBetween);
        ArgumentNullException.ThrowIfNull(signatures);
        ArgumentNullException.ThrowIfNull(seen);
        ArgumentNullException.ThrowIfNull(countedAs);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _traffic = traffic;
        _identities = identities;
        _countedBetween = countedBetween;
        _history = history;
        _signatures = signatures;
        _seen = seen;
        _countedAs = countedAs;
        _clock = clock;
        _logger = logger;
        _wanted = wanted ?? (static () => true);
        _learning = !seen.Existed;
    }

    /// <summary>Raised on the watcher's thread for each app seen for the first time.</summary>
    public event EventHandler<NewAppSighting>? Sighted;

    /// <summary>The last few new apps, newest first, for a page that opens later.</summary>
    public IReadOnlyList<NewAppSighting> Recent
    {
        get
        {
            lock (_gate)
            {
                return [.. _recent];
            }
        }
    }

    /// <summary>Whether this pass is learning quietly rather than calling apps new.</summary>
    public bool IsLearning => _learning;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_history is not null)
        {
            _history.Erased += OnErased;
        }

        try
        {
            await Task.Delay(Settle, _clock, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                Look();
                await Task.Delay(Step, _clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_history is not null)
            {
                _history.Erased -= OnErased;
            }
        }
    }

    /// <summary>
    /// One look, if anybody wants to hear; otherwise nothing at all, and the
    /// next look after the gap learns quietly. Internal so tests can drive it.
    /// </summary>
    internal void Look()
    {
        if (!_wanted())
        {
            _idle = true;
            return;
        }

        if (_idle)
        {
            _idle = false;
            _learning = true;
        }

        Pass();
    }

    /// <summary>One look at what is using the network. Internal so tests can drive it.</summary>
    internal void Pass()
    {
        DateTimeOffset now = _clock.GetUtcNow();

        if (!_historyLearned)
        {
            LearnHistory(now);
            _historyLearned = true;
        }

        Dictionary<int, string?> users = Users();
        HashSet<string>? counted = null;
        bool learning = _learning;
        bool changed = false;

        foreach ((int processId, string? remote) in users)
        {
            if (!_apps.TryGetValue(processId, out string? app))
            {
                app = _countedAs(processId);
                _apps[processId] = app;
            }

            if (string.IsNullOrEmpty(app) || AppAttribution.Classify(app).Kind is AppKind.System or AppKind.Unattributed)
            {
                continue;
            }

            if (_seen.Contains(app))
            {
                continue;
            }

            if (learning)
            {
                changed |= _seen.Add(app);
                continue;
            }

            // Windows may have counted it while Windsock was closed, and the
            // collector may not have copied that in when the list was read.
            counted ??= CountedLately(now);

            if (counted.Contains(app))
            {
                changed |= _seen.Add(app);
                continue;
            }

            changed |= _seen.Add(app);
            Announce(app, processId, remote, now);
        }

        // Ids of processes that have ended may come back as other programs.
        foreach (int gone in _apps.Keys.Where(id => !users.ContainsKey(id)).ToList())
        {
            _apps.Remove(gone);
        }

        if (learning)
        {
            _learning = false;
            changed = true;
            LogLearned(_seen.Count);
        }

        if (changed && !_seen.Save())
        {
            LogSaveFailed();
        }
    }

    /// <summary>Every process using the network now, with an address it is talking to where one shows.</summary>
    private Dictionary<int, string?> Users()
    {
        Dictionary<int, string?> users = [];

        foreach (ProcessConnection connection in _connections.ReadAll())
        {
            if (connection.ProcessId <= 4
                || connection.Protocol != TransportProtocol.Tcp
                || connection.State is TcpConnectionState.Listening or TcpConnectionState.TimeWait or TcpConnectionState.Closed
                || !IPAddress.TryParse(connection.RemoteAddress, out IPAddress? remote)
                || LoopbackEnds.IsLoopback(remote)
                || remote.Equals(IPAddress.Any)
                || remote.Equals(IPAddress.IPv6Any))
            {
                continue;
            }

            users.TryAdd(connection.ProcessId, connection.RemoteAddress);
        }

        // The trace leaves loopback out of a process's totals, so any bytes at
        // all here went over a network.
        _traffic.CopyTo(_bytes);

        foreach ((int processId, ProcessTraffic traffic) in _bytes)
        {
            if (processId > 4 && traffic.BytesReceived + traffic.BytesSent > 0)
            {
                users.TryAdd(processId, null);
            }
        }

        return users;
    }

    private void Announce(string app, int processId, string? remote, DateTimeOffset now)
    {
        ProcessIdentity identity = _identities.Resolve(processId);
        SignatureInfo? signature = identity.FilePath is { } path ? _signatures.Check(path) : null;
        string name = string.IsNullOrWhiteSpace(identity.Description) ? identity.Name : identity.Description;

        NewAppSighting sighting = new(app, name, identity.FilePath, processId, remote, signature, now);

        lock (_gate)
        {
            _recent.Insert(0, sighting);

            if (_recent.Count > Remembered)
            {
                _recent.RemoveAt(_recent.Count - 1);
            }
        }

        LogSighted(name, sighting.PublisherText);
        Sighted?.Invoke(this, sighting);
    }

    /// <summary>Notes every app history holds, which has used the network by definition.</summary>
    private void LearnHistory(DateTimeOffset now)
    {
        try
        {
            int added = _seen.AddRange(_countedBetween(DateTimeOffset.UnixEpoch, now.AddMinutes(1)));

            if (added > 0 && _seen.Existed)
            {
                _ = _seen.Save();
            }
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogHistoryFailed(ex);
        }
    }

    private HashSet<string> CountedLately(DateTimeOffset now)
    {
        try
        {
            return new HashSet<string>(_countedBetween(now - HistoryReach, now.AddMinutes(1)), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogHistoryFailed(ex);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// All history erased: forget the seen list with it, and learn again
    /// quietly from what is in use.
    /// </summary>
    private void OnErased(object? sender, EventArgs e)
    {
        try
        {
            if (_history is null || _history.Extent().HasData)
            {
                return;
            }
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            return;
        }

        ForgetAll();
    }

    /// <summary>Forgets every app seen, and learns again quietly from the next pass.</summary>
    internal void ForgetAll()
    {
        _seen.Forget();
        _learning = true;

        lock (_gate)
        {
            _recent.Clear();
        }
    }

    private static string? CountedAs(int processId)
    {
        (string? native, string? family) = ProcessIdentityResolver.ReadCountedAs(processId);

        return native is null && family is null
            ? null
            : AppMinuteTally.CountedAs(processId, family, native, image: null);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Learned {Count} apps already using the network; later ones are announced")]
    private partial void LogLearned(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "A program used the network for the first time: {Name} ({Publisher})")]
    private partial void LogSighted(string name, string publisher);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the list of apps seen online")]
    private partial void LogSaveFailed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read history's apps for the new-app watcher")]
    private partial void LogHistoryFailed(Exception exception);
}
