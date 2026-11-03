using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Settings;

namespace Windsock.Core.Plans;

/// <summary>
/// Keeps every data plan's status up to date, says which plan the network in
/// use falls under, and raises a crossing once per level per cycle.
/// </summary>
/// <remarks>
/// <para>
/// Worked out again after each recorded minute, when the network changes, when
/// history is erased or imported, when asked, and once a minute regardless so
/// the time left keeps moving while recording is off. Each is a millisecond or
/// two a plan; with no plans there is nothing to do.
/// </para>
/// <para>
/// A crossing is noted in the history file (<see cref="IPlanLevelStore"/>), so it
/// is raised once however often Windsock starts in that cycle. Should the file
/// refuse the note, it is still raised, once a run.
/// </para>
/// <para>
/// Events are raised on the monitor's own thread. Anything touching the screen marshals.
/// </para>
/// </remarks>
public sealed partial class PlanMonitor : BackgroundService
{
    private static readonly TimeSpan LookEvery = TimeSpan.FromMinutes(1);

    private readonly WindsockSettings _settings;
    private readonly PlanCalculator _calculator;
    private readonly IPlanLevelStore _levels;
    private readonly CurrentNetwork? _network;
    private readonly UsageRecorder? _recorder;
    private readonly IUsageHistoryStore? _history;
    private readonly TimeProvider _clock;
    private readonly ILogger<PlanMonitor> _logger;
    private readonly SemaphoreSlim _nudge = new(0, 1);
    private readonly Lock _gate = new();
    private readonly HashSet<(string Plan, long Cycle)> _looked = [];
    private readonly HashSet<(string Plan, long Cycle, int Level)> _told = [];
    private readonly HashSet<string> _unworkable = new(StringComparer.Ordinal);
    private IReadOnlyList<PlanStatus> _statuses = [];
    private PlanStatus? _active;
    private bool _failed;

    public PlanMonitor(
        WindsockSettings settings,
        PlanCalculator calculator,
        IPlanLevelStore levels,
        CurrentNetwork network,
        UsageRecorder recorder,
        IUsageHistoryStore history,
        TimeProvider clock,
        ILogger<PlanMonitor> logger)
        : this(settings, calculator, levels, clock, logger, network, recorder, history)
    {
    }

    internal PlanMonitor(
        WindsockSettings settings,
        PlanCalculator calculator,
        IPlanLevelStore levels,
        TimeProvider clock,
        ILogger<PlanMonitor> logger,
        CurrentNetwork? network = null,
        UsageRecorder? recorder = null,
        IUsageHistoryStore? history = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(calculator);
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _calculator = calculator;
        _levels = levels;
        _clock = clock;
        _logger = logger;
        _network = network;
        _recorder = recorder;
        _history = history;
    }

    /// <summary>Raised after the statuses were worked out again and something in them changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when a plan's cycle passes one of its levels, once per level per cycle.</summary>
    public event EventHandler<PlanLevelCrossing>? LevelCrossed;

    /// <summary>Every plan's status, in the order the plans are listed.</summary>
    public IReadOnlyList<PlanStatus> Statuses
    {
        get
        {
            lock (_gate)
            {
                return _statuses;
            }
        }
    }

    /// <summary>
    /// The status of the plan the network in use falls under - the first plan
    /// naming it, else the first on every network - or null when none does.
    /// This is what the taskbar's "data left" shows.
    /// </summary>
    public PlanStatus? Active
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    /// <summary>
    /// The active plan in a few words, for a taskbar slot or a tooltip - "41 GB
    /// left · 17 days", "Over by 2.1 GB · 3 days" - or null when no plan covers
    /// the network in use or its cycle is not under way, which a meter shows as
    /// a dash. Safe from any thread; read it again on <see cref="Changed"/>.
    /// </summary>
    public string? ActiveText => Active is { } status ? PlanWords.Left(status) : null;

    /// <summary>
    /// How much of the active plan's allowance is used, 0 to 1 and past 1 when
    /// over, or null with no active plan or no allowance: for a bar or a colour.
    /// </summary>
    public double? ActiveFraction => Active?.Fraction;

    /// <summary>The status of one plan, by its id, or null.</summary>
    public PlanStatus? StatusOf(string planId)
    {
        foreach (PlanStatus status in Statuses)
        {
            if (string.Equals(status.Plan.Id, planId, StringComparison.Ordinal))
            {
                return status;
            }
        }

        return null;
    }

    /// <summary>Works everything out again soon: after a plan was added, changed or removed.</summary>
    public void Refresh()
    {
        _nudge.Signal();
    }

    private void OnSomethingChanged(object? sender, EventArgs e) => Refresh();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_network is not null)
        {
            _network.Changed += OnSomethingChanged;
        }

        if (_recorder is not null)
        {
            _recorder.Recorded += OnSomethingChanged;
        }

        if (_history is not null)
        {
            _history.Erased += OnSomethingChanged;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Evaluate();
                await _nudge.WaitAsync(LookEvery, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_network is not null)
            {
                _network.Changed -= OnSomethingChanged;
            }

            if (_recorder is not null)
            {
                _recorder.Recorded -= OnSomethingChanged;
            }

            if (_history is not null)
            {
                _history.Erased -= OnSomethingChanged;
            }
        }
    }

    /// <summary>Works out every plan once and raises what that finds. Internal so tests can drive it.</summary>
    internal void Evaluate()
    {
        DateTimeOffset now = _clock.GetLocalNow();
        List<DataPlan> plans = Plans();
        List<PlanStatus> statuses = new(plans.Count);
        List<PlanLevelCrossing> crossings = [];

        foreach (DataPlan plan in plans)
        {
            PlanStatus? status;

            try
            {
                status = _calculator.Status(plan, now);
                _failed = false;
            }
            catch (Exception ex) when (HistoryFailure.IsStorage(ex))
            {
                if (!_failed)
                {
                    _failed = true;
                    LogReadFailed(ex);
                }

                continue;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ArithmeticException)
            {
                if (_unworkable.Add(plan.Id ?? string.Empty))
                {
                    LogPlanUnworkable(ex, plan.Name);
                }

                continue;
            }

            if (status is null)
            {
                continue;
            }

            // One plan the store or the calculator cannot take - an id a
            // hand-edited file left blank, a figure out of reach - is logged
            // once and passed over; it must not stop every other plan, nor
            // the host with it.
            try
            {
                crossings.AddRange(Crossings(status));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ArithmeticException)
            {
                if (_unworkable.Add(plan.Id ?? string.Empty))
                {
                    LogPlanUnworkable(ex, plan.Name);
                }
            }

            statuses.Add(status);
        }

        PlanStatus? active = null;

        // A plan whose cycle is running wins over one that has ended or not
        // begun, wherever it is in the list; the rest only when none is running.
        string? network = _network?.Current?.Key;
        HashSet<DataPlan> running = new(statuses.Where(status => status.State == PlanState.Running).Select(status => status.Plan), ReferenceEqualityComparer.Instance);

        if ((DataPlan.For(plans.Where(running.Contains), network) ?? DataPlan.For(plans, network)) is { } chosen)
        {
            active = statuses.Find(status => ReferenceEquals(status.Plan, chosen));
        }

        bool changed;

        lock (_gate)
        {
            changed = !SameFigures(_statuses, statuses) || !ReferenceEquals(_active?.Plan, active?.Plan);
            _statuses = statuses;
            _active = active;
        }

        foreach (PlanLevelCrossing crossing in crossings)
        {
            LogCrossed(crossing.Level, crossing.FirstLook);
            LevelCrossed?.Invoke(this, crossing);
        }

        if (changed || crossings.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private List<DataPlan> Plans()
    {
        // Pages replace the list rather than change it; a page that changes it
        // in place anyway costs one turn, not a crash.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return [.. _settings.Usage.Plans];
            }
            catch (InvalidOperationException)
            {
            }
        }

        return [];
    }

    private List<PlanLevelCrossing> Crossings(PlanStatus status)
    {
        List<PlanLevelCrossing> crossings = [];

        if (status.State != PlanState.Running || !status.HasLimit)
        {
            return crossings;
        }

        (string, long) cycle = (status.Plan.Id, status.Period.StartMinute);
        bool firstLook = _looked.Add(cycle);

        foreach (int level in status.Plan.Levels)
        {
            if (!status.Reached(level) || _told.Contains((status.Plan.Id, status.Period.StartMinute, level)))
            {
                continue;
            }

            bool fresh;

            try
            {
                fresh = _levels.MarkCrossed(status.Plan.Id, status.Period.StartMinute, level, EpochMinute.From(status.At));
            }
            catch (Exception ex) when (HistoryFailure.IsStorage(ex))
            {
                // Better told twice in two runs than not at all.
                fresh = true;
            }

            _told.Add((status.Plan.Id, status.Period.StartMinute, level));

            if (fresh)
            {
                crossings.Add(new PlanLevelCrossing(status.Plan, level, status, firstLook));
            }
        }

        return crossings;
    }

    /// <summary>Whether two lists of statuses show the same figures, so a quiet minute raises nothing.</summary>
    private static bool SameFigures(IReadOnlyList<PlanStatus> before, List<PlanStatus> after)
    {
        if (before.Count != after.Count)
        {
            return false;
        }

        for (int each = 0; each < before.Count; each++)
        {
            PlanStatus left = before[each];
            PlanStatus right = after[each];

            if (!ReferenceEquals(left.Plan, right.Plan)
                || left.Period != right.Period
                || left.Used != right.Used
                || left.OffPeakUsed != right.OffPeakUsed
                || left.Outlook.DaysLeft != right.Outlook.DaysLeft
                || left.State != right.State)
            {
                return false;
            }
        }

        return true;
    }

    public override void Dispose()
    {
        base.Dispose();
        _nudge.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Passed over the data plan {Name}, which could not be worked out")]
    private partial void LogPlanUnworkable(Exception exception, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read history to work out the data plans")]
    private partial void LogReadFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "A data plan passed {Level}% of its allowance (first look: {FirstLook})")]
    private partial void LogCrossed(int level, bool firstLook);
}
