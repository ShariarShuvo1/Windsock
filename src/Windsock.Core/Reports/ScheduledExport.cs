using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.History;
using Windsock.Core.Settings;

namespace Windsock.Core.Reports;

/// <summary>One stretch scheduled export writes a file for.</summary>
/// <param name="First">Its first local date: the day, or the month's first.</param>
/// <param name="From">When it starts.</param>
/// <param name="Until">When it ends. Exclusive.</param>
public readonly record struct ExportStretch(DateOnly First, DateTimeOffset From, DateTimeOffset Until);

/// <summary>Works out which finished days or months are due to be written.</summary>
public static class ExportSchedule
{
    /// <summary>
    /// How long after a stretch ends before it is written: Windows settles
    /// each app's figures for the last minutes some while after, and they are
    /// in a JSON file.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromHours(1);

    /// <summary>The most stretches written in one go, so a long catch-up goes in steps.</summary>
    public const int Most = 62;

    /// <summary>The first local date of the stretch holding a date.</summary>
    public static DateOnly StretchOf(ExportEvery every, DateOnly date) =>
        every == ExportEvery.Month ? new DateOnly(date.Year, date.Month, 1) : date;

    /// <summary>The first local date of the stretch after one.</summary>
    public static DateOnly After(ExportEvery every, DateOnly first) =>
        every == ExportEvery.Month ? first.AddMonths(1) : first.AddDays(1);

    /// <summary>The first local date of the stretch before one.</summary>
    public static DateOnly Before(ExportEvery every, DateOnly first) =>
        every == ExportEvery.Month ? first.AddMonths(-1) : first.AddDays(-1);

    /// <summary>
    /// The finished stretches from <paramref name="next"/> on, oldest first, that
    /// have been over for at least <see cref="Settle"/>.
    /// </summary>
    public static IReadOnlyList<ExportStretch> Due(ExportEvery every, DateOnly next, DateTimeOffset now, TimeZoneInfo zone, int most = Most)
    {
        ArgumentNullException.ThrowIfNull(zone);

        List<ExportStretch> due = [];

        for (DateOnly first = StretchOf(every, next); due.Count < most; first = After(every, first))
        {
            DateTimeOffset from = LocalCalendar.StartOf(first, zone);
            DateTimeOffset until = LocalCalendar.StartOf(After(every, first), zone);

            if (until + Settle > now)
            {
                break;
            }

            due.Add(new ExportStretch(first, from, until));
        }

        return due;
    }

    /// <summary>The file a stretch is written to: <c>Windsock-2026-10-04.csv</c>, <c>Windsock-2026-09.json</c>.</summary>
    public static string FileName(ExportEvery every, DateOnly first, ExportFormat format) => string.Concat(
        "Windsock-",
        first.ToString(every == ExportEvery.Month ? "yyyy-MM" : "yyyy-MM-dd", CultureInfo.InvariantCulture),
        format == ExportFormat.Json ? ".json" : ".csv");
}

/// <summary>What one run of scheduled export did.</summary>
/// <param name="At">When it ran.</param>
/// <param name="Written">The files written.</param>
/// <param name="LeftAlone">Files that were already there and were not overwritten.</param>
/// <param name="Empty">Stretches with nothing recorded, for which no file was written.</param>
/// <param name="Problem">Why it stopped, or null when it did not.</param>
public sealed record ExportRun(DateTimeOffset At, IReadOnlyList<string> Written, IReadOnlyList<string> LeftAlone, int Empty, string? Problem);

/// <summary>
/// Writes each finished day or month of history to a chosen folder by itself,
/// catching up on any it missed while the PC was off.
/// </summary>
/// <remarks>
/// <para>
/// How far it has got is a bookmark in the history file, so a stretch is written
/// once. When first switched on it starts with the last finished stretch, so the
/// first file appears at once rather than tomorrow; everything before is what
/// History's own export is for.
/// </para>
/// <para>
/// A file already there is never overwritten, whoever wrote it: it is left
/// alone and the run says so. Each file is written under a temporary name and
/// renamed, so a file that is there is a whole one. A folder that is not there
/// - a drive unplugged - stops the run without moving the bookmark, and it is
/// tried again within the hour.
/// </para>
/// <para>
/// A CSV holds network use a minute at a time with its network, which History
/// imports again; a JSON file holds everything History does, by the hour (see
/// <see cref="HistoryJson"/>). Neither ever holds Windows' figures for time
/// Windsock was closed in place of what Windsock recorded.
/// </para>
/// </remarks>
public sealed partial class ScheduledExport : BackgroundService
{
    private const string DayMark = "auto_export_day";
    private const string MonthMark = "auto_export_month";
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan LookEvery = TimeSpan.FromHours(1);

    private readonly WindsockSettings _settings;
    private readonly IHistoryFile _file;
    private readonly IUsageHistoryStore _history;
    private readonly IUsageAggregates _aggregates;
    private readonly HistoryJson _json;
    private readonly TimeProvider _clock;
    private readonly Func<TimeZoneInfo> _zone;
    private readonly ILogger<ScheduledExport> _logger;
    private readonly SemaphoreSlim _one = new(1, 1);

    public ScheduledExport(
        WindsockSettings settings,
        IHistoryFile file,
        IUsageHistoryStore history,
        IUsageAggregates aggregates,
        HistoryJson json,
        TimeProvider clock,
        ILogger<ScheduledExport> logger)
        : this(settings, file, history, aggregates, json, clock, static () => TimeZoneInfo.Local, logger)
    {
    }

    internal ScheduledExport(
        WindsockSettings settings,
        IHistoryFile file,
        IUsageHistoryStore history,
        IUsageAggregates aggregates,
        HistoryJson json,
        TimeProvider clock,
        Func<TimeZoneInfo> zone,
        ILogger<ScheduledExport> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(aggregates);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _file = file;
        _history = history;
        _aggregates = aggregates;
        _json = json;
        _clock = clock;
        _zone = zone;
        _logger = logger;
    }

    /// <summary>What the last run did, or null before the first.</summary>
    public ExportRun? LastRun { get; private set; }

    /// <summary>Raised after a run wrote something or stopped on a problem.</summary>
    public event EventHandler? Changed;

    /// <summary>Runs now rather than within the hour: after the folder or the schedule changed.</summary>
    public Task<ExportRun?> RunNowAsync() => Task.Run(() => RunDue(_clock.GetUtcNow()));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, _clock, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                RunDue(_clock.GetUtcNow());
                await Task.Delay(LookEvery, _clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Writes every stretch that is due. Internal so tests can drive it; null when switched off.</summary>
    internal ExportRun? RunDue(DateTimeOffset now)
    {
        AutoExportSettings choices = _settings.Usage.AutoExport;

        if (!choices.Enabled || _file.State != HistoryFileState.Ready)
        {
            return null;
        }

        _one.Wait();

        try
        {
            ExportRun run = Run(choices, now);

            if (run.Written.Count > 0 || run.LeftAlone.Count > 0 || run.Problem is not null || LastRun?.Problem is not null)
            {
                LastRun = run;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                LastRun ??= run;
            }

            return run;
        }
        finally
        {
            _one.Release();
        }
    }

    private ExportRun Run(AutoExportSettings choices, DateTimeOffset now)
    {
        List<string> written = [];
        List<string> leftAlone = [];
        int empty = 0;
        string folder = choices.Folder?.Trim() ?? string.Empty;

        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            return new ExportRun(now, written, leftAlone, 0, "Choose a folder to export to.");
        }

        if (!Directory.Exists(folder))
        {
            return new ExportRun(now, written, leftAlone, 0, string.Concat("The folder ", folder, " is not there."));
        }

        TimeZoneInfo zone = _zone();
        ExportEvery every = choices.Every;
        string mark = every == ExportEvery.Month ? MonthMark : DayMark;

        try
        {
            DateOnly next = _file.ReadMark(mark) is { } kept
                ? DateOnly.FromDayNumber((int)Math.Clamp(kept, DateOnly.MinValue.DayNumber, DateOnly.MaxValue.DayNumber))
                : ExportSchedule.Before(every, ExportSchedule.StretchOf(every, LocalCalendar.DateOf(now - ExportSchedule.Settle, zone)));

            foreach (ExportStretch stretch in ExportSchedule.Due(every, next, now, zone))
            {
                string name = ExportSchedule.FileName(every, stretch.First, choices.Format);
                string path = Path.Combine(folder, name);

                if (File.Exists(path))
                {
                    leftAlone.Add(path);
                }
                else if (_history.Summarise(stretch.From, stretch.Until).Minutes == 0)
                {
                    empty++;
                }
                else
                {
                    Write(path, stretch, choices.Format, now);

                    if (File.Exists(path))
                    {
                        written.Add(path);
                        LogWrote(name);
                    }
                }

                _file.WriteMark(mark, ExportSchedule.After(every, stretch.First).DayNumber);
            }

            return new ExportRun(now, written, leftAlone, empty, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || HistoryFailure.IsStorage(ex))
        {
            LogFailed(ex);
            return new ExportRun(now, written, leftAlone, empty, ex.Message);
        }
    }

    /// <summary>
    /// Writes one stretch under a temporary name, then renames it, never over a
    /// file of the same name that appeared meanwhile.
    /// </summary>
    private void Write(string path, ExportStretch stretch, ExportFormat format, DateTimeOffset now)
    {
        string temporary = path + ".partial";

        try
        {
            if (format == ExportFormat.Json)
            {
                using FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None);
                _json.Write(stream, stretch.From, stretch.Until, UsageGranularity.Hour, now);
            }
            else
            {
                IReadOnlyList<UsageMinute> minutes = _aggregates.ReadMinuteRows(EpochMinute.From(stretch.From), EpochMinute.From(stretch.Until));
                using StreamWriter writer = new(temporary, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                UsageCsv.WriteMinutes(writer, minutes);
            }

            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _one.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled export wrote {Name}")]
    private partial void LogWrote(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled export stopped")]
    private partial void LogFailed(Exception exception);
}
