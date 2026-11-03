using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.Formatting;
using Windsock.Core.History;
using Windsock.Core.Notifications;
using Windsock.Core.Settings;

namespace Windsock.Core.Reports;

/// <summary>
/// What a weekly digest says: what the week used, against the week before,
/// its busiest app and day, and its outages.
/// </summary>
public static class WeeklyDigest
{
    /// <summary>The notice topic, so the digest can be switched off on its own.</summary>
    public const string Topic = "weekly-digest";

    /// <summary>The section a click on the digest opens.</summary>
    public const string Opens = "reports";

    /// <summary>The hour of the first day of a week from which last week's digest may be sent.</summary>
    public const int SendFromHour = 9;

    /// <summary>
    /// The notice for a finished week, or null when nothing at all is known of it.
    /// </summary>
    /// <param name="week">The week's report, with its change from the week before.</param>
    /// <param name="nameOf">The name to show for an app, by Windows' id for it.</param>
    public static Notice? Compose(PeriodReport week, Func<string, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(week);
        ArgumentNullException.ThrowIfNull(nameOf);

        if (!week.Totals.HasData || week.Totals.All <= 0)
        {
            return null;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        string title = "Last week: " + SizeFormatter.Format(week.Totals.All);
        StringBuilder message = new();

        if (week.Change is { Percent: { } percent } && Math.Abs(percent) >= 0.01)
        {
            message.Append(culture, $"{(percent > 0 ? "Up" : "Down")} {Math.Abs(percent):P0} on the week before. ");
        }

        if (week.TopApps.Count > 0 && week.TopApps[0].Total > 0)
        {
            message.Append(culture, $"Busiest app: {nameOf(week.TopApps[0].AttributionId)}, {SizeFormatter.Format(week.TopApps[0].Total)}. ");
        }

        if (week.BusiestDay is { } day)
        {
            message.Append(culture, $"Busiest day: {day.Start.ToString("dddd", culture)}. ");
        }

        message.Append(week.Outages.Count switch
        {
            0 => "No outages.",
            1 => string.Create(culture, $"1 outage, {DurationLabel.Format(week.Outages.Offline.TotalSeconds)} offline."),
            _ => string.Create(culture, $"{week.Outages.Count} outages, {DurationLabel.Format(week.Outages.Offline.TotalSeconds)} offline."),
        });

        return new Notice(Topic, title, message.ToString().Trim())
        {
            Tag = Topic,
            Opens = Opens,
            Quiet = true,
        };
    }

    /// <summary>
    /// The finished week a digest is due for, or null: the week before the one
    /// holding the present, once its first day has reached
    /// <see cref="SendFromHour"/>, and only if it was not sent already. A week
    /// whose turn came while the PC was off is sent at the next start that
    /// same week, and skipped after that - a digest a week late is noise.
    /// </summary>
    public static ReportPeriod? Due(DateOnly? lastSent, DateTimeOffset now, TimeZoneInfo zone, DayOfWeek firstDay)
    {
        ArgumentNullException.ThrowIfNull(zone);

        ReportPeriod thisWeek = ReportPeriod.Week(LocalCalendar.DateOf(now, zone), firstDay, zone);
        ReportPeriod lastWeek = thisWeek.Previous(zone);

        if (lastSent is { } sent && sent >= lastWeek.First)
        {
            return null;
        }

        return LocalCalendar.Wall(now, zone) >= thisWeek.First.ToDateTime(new TimeOnly(SendFromHour, 0)) ? lastWeek : null;
    }
}

/// <summary>Sends the weekly digest when one is due, if the person asked for it.</summary>
public sealed partial class DigestService : BackgroundService
{
    private const string SentMark = "digest_week";
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan LookEvery = TimeSpan.FromMinutes(30);

    private readonly WindsockSettings _settings;
    private readonly UsageReports _reports;
    private readonly IHistoryFile _file;
    private readonly INoticeSink _notices;
    private readonly Func<string, string> _nameOf;
    private readonly TimeProvider _clock;
    private readonly ILogger<DigestService> _logger;

    public DigestService(
        WindsockSettings settings,
        UsageReports reports,
        IHistoryFile file,
        INoticeSink notices,
        Apps.AppNames names,
        TimeProvider clock,
        ILogger<DigestService> logger)
        : this(settings, reports, file, notices, NameFrom(names), clock, logger)
    {
    }

    internal DigestService(
        WindsockSettings settings,
        UsageReports reports,
        IHistoryFile file,
        INoticeSink notices,
        Func<string, string> nameOf,
        TimeProvider clock,
        ILogger<DigestService> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(nameOf);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _reports = reports;
        _file = file;
        _notices = notices;
        _nameOf = nameOf;
        _clock = clock;
        _logger = logger;
    }

    private static Func<string, string> NameFrom(Apps.AppNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return id => names.Describe(id).Name;
    }

    /// <summary>The day weeks start on: the person's choice, else the region's.</summary>
    public DayOfWeek FirstDay =>
        _settings.Usage.Digest.WeekStarts ?? CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    /// <summary>Last week's digest as it would be sent now, or null when there is nothing to say: for a page to show it.</summary>
    public Notice? Preview()
    {
        TimeZoneInfo zone = _reports.Zone;
        ReportPeriod lastWeek = ReportPeriod.Week(LocalCalendar.DateOf(_clock.GetUtcNow(), zone), FirstDay, zone).Previous(zone);
        return WeeklyDigest.Compose(_reports.Report(lastWeek, topApps: 1), _nameOf);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, _clock, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                SendIfDue(_clock.GetUtcNow());
                await Task.Delay(LookEvery, _clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Sends the digest if one is due. Internal so tests can drive it; true when one went.</summary>
    internal bool SendIfDue(DateTimeOffset now)
    {
        if (!_settings.Usage.Digest.Enabled || _file.State != HistoryFileState.Ready)
        {
            return false;
        }

        try
        {
            TimeZoneInfo zone = _reports.Zone;
            DateOnly? lastSent = _file.ReadMark(SentMark) is { } day ? DateOnly.FromDayNumber((int)day) : null;

            if (WeeklyDigest.Due(lastSent, now, zone, FirstDay) is not { } week)
            {
                return false;
            }

            // Marked first: a notice that fails is not tried again every half hour.
            _file.WriteMark(SentMark, week.First.DayNumber);

            if (WeeklyDigest.Compose(_reports.Report(week, topApps: 1), _nameOf) is not { } notice)
            {
                return false;
            }

            bool sent = _notices.Send(notice);
            LogSent(sent);
            return sent;
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogFailed(ex);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Weekly digest composed (sent: {Sent})")]
    private partial void LogSent(bool sent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read history for the weekly digest")]
    private partial void LogFailed(Exception exception);
}
