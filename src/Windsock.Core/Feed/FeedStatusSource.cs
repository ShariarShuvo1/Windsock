using Windsock.Core.Connectivity;
using Windsock.Core.Hardware;
using Windsock.Core.History;
using Windsock.Core.Home;
using Windsock.Core.Networking;

namespace Windsock.Core.Feed;

/// <summary>
/// The feed's figures, read from the monitors the app already runs: the
/// throughput monitor, the connection checks, history, the machine's readings
/// and the router's counters. The data plan comes in through a function, so
/// the plans feature can hand over whatever it has without this knowing how.
/// </summary>
public sealed class FeedStatusSource : IFeedSource
{
    private static readonly TimeSpan UsageFreshFor = TimeSpan.FromSeconds(30);

    private readonly ThroughputMonitor _throughput;
    private readonly ConnectionMonitor _connection;
    private readonly IUsageHistoryStore _history;
    private readonly SystemMonitor _system;
    private readonly HouseholdTraffic _household;
    private readonly Func<FeedPlan?> _plan;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private (FeedUsage Usage, long Stamp)? _usage;

    public FeedStatusSource(
        ThroughputMonitor throughput,
        ConnectionMonitor connection,
        IUsageHistoryStore history,
        SystemMonitor system,
        HouseholdTraffic household,
        Func<FeedPlan?> plan,
        TimeProvider time)
    {
        _throughput = throughput ?? throw new ArgumentNullException(nameof(throughput));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _household = household ?? throw new ArgumentNullException(nameof(household));
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public FeedSnapshot Read()
    {
        DateTimeOffset now = _time.GetLocalNow();

        return new FeedSnapshot(
            now,
            Rates(_throughput.Latest),
            Connection(_connection.State, _connection.Latest),
            Usage(now),
            Plan(),
            System(_system.Latest),
            Household());
    }

    /// <inheritdoc />
    public FeedUsageRange? ReadUsage(DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity)
    {
        try
        {
            IReadOnlyList<UsageBucket> buckets = _history.Read(from, until, granularity);
            List<FeedBucket> kept = [.. buckets
                .Where(each => each.Minutes > 0)
                .Select(each => new FeedBucket(each.Start, each.BytesDown, each.BytesUp, each.Minutes))];

            return new FeedUsageRange(
                from,
                until,
                granularity,
                new FeedTotals(kept.Sum(each => each.BytesDown), kept.Sum(each => each.BytesUp)),
                kept);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public IDisposable Watch() => new Watches([_system.Watch(), _connection.Watch(), _household.Watch()]);

    /// <summary>The rates in a sample, or nothing before the first.</summary>
    public static FeedRates Rates(ThroughputSample sample) =>
        sample.Timestamp == DateTimeOffset.MinValue
            ? new FeedRates(null, null, null)
            : new FeedRates(sample.Timestamp, sample.DownloadBytesPerSecond, sample.UploadBytesPerSecond);

    /// <summary>The connection as the feed tells it.</summary>
    public static FeedConnection Connection(ConnectionState state, ConnectionSample? latest)
    {
        string status = state.Status switch
        {
            ConnectionStatus.Online => "online",
            ConnectionStatus.Offline => "offline",
            _ => "checking",
        };

        string? cause = state.Cause switch
        {
            null => null,
            OutageCause.NoNetwork => "no-network",
            OutageCause.Router => "router",
            OutageCause.Provider => "provider",
            OutageCause.SignIn => "sign-in",
            _ => "unknown",
        };

        return new FeedConnection(
            status,
            state.Since,
            cause,
            latest?.Facts.Network?.Name,
            latest?.Router?.TotalMilliseconds,
            latest?.Internet?.TotalMilliseconds,
            latest?.Facts.VpnActive);
    }

    /// <summary>How busy the machine is, or nothing before the first reading.</summary>
    public static FeedSystem System(SystemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.HasValue)
        {
            return new FeedSystem(null, null, null, null, null, null, null);
        }

        return new FeedSystem(
            snapshot.Taken,
            snapshot.Processor.BusyPercent,
            snapshot.Memory.Total > 0 ? snapshot.Memory.UsedPercent : null,
            snapshot.Graphics.Count > 0 ? snapshot.Graphics.Max(each => each.BusyPercent) : null,
            snapshot.Drives.Count > 0 ? snapshot.Drives.Max(each => each.BusyPercent) : null,
            snapshot.Processor.Celsius,
            snapshot.Graphics.Select(each => each.Celsius).OfType<double>().DefaultIfEmpty(double.NaN).Max() is var hottest && double.IsFinite(hottest) ? hottest : null);
    }

    private FeedUsage? Usage(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_usage is { } cached && _time.GetElapsedTime(cached.Stamp) < UsageFreshFor)
            {
                return cached.Usage;
            }
        }

        try
        {
            DateOnly today = LocalCalendar.DateOf(now, TimeZoneInfo.Local);
            DateTimeOffset midnight = LocalCalendar.StartOf(today, TimeZoneInfo.Local);
            DateTimeOffset first = LocalCalendar.StartOf(LocalCalendar.MonthOf(today), TimeZoneInfo.Local);
            DateTimeOffset until = now.AddMinutes(1);

            UsageSummary day = _history.Summarise(midnight, until);
            UsageSummary month = _history.Summarise(first, until);

            FeedUsage usage = new(new FeedTotals(day.BytesDown, day.BytesUp), new FeedTotals(month.BytesDown, month.BytesUp), now);

            lock (_gate)
            {
                _usage = (usage, _time.GetTimestamp());
            }

            return usage;
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            return null;
        }
    }

    private FeedPlan? Plan()
    {
        try
        {
            return _plan();
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            return null;
        }
    }

    private FeedHousehold? Household()
    {
        if (_household.State == HouseholdState.Off)
        {
            return null;
        }

        HouseholdRate? rate = _household.Latest;
        return new FeedHousehold(rate?.At, rate?.DownBytesPerSecond, rate?.UpBytesPerSecond, _household.Router);
    }

    private sealed class Watches(IReadOnlyList<IDisposable> held) : IDisposable
    {
        private IReadOnlyList<IDisposable>? _held = held;

        public void Dispose()
        {
            foreach (IDisposable each in Interlocked.Exchange(ref _held, null) ?? [])
            {
                each.Dispose();
            }
        }
    }
}
