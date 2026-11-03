using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.History;
using Windsock.Core.Settings;

namespace Windsock.Core.Home;

/// <summary>
/// Runs the router check - maker, model, and the ports the router has opened
/// to the internet through UPnP - when asked, and once a week by itself.
/// </summary>
/// <remarks>
/// Off until <see cref="HomeSettings.CheckRouter"/> is on, and only on a private
/// network. The report is something to review, not an alarm: a port mapping is
/// how game consoles, video calls and file sharing reach the home, and most are
/// expected. Each network's last report is kept in the home store, so the page
/// can say when it was checked.
/// </remarks>
public sealed partial class RouterWatch : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromDays(7);
    private static readonly TimeSpan FirstWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LookAgain = TimeSpan.FromHours(6);

    private readonly WindsockSettings _settings;
    private readonly HomeLinkReader _links;
    private readonly IRouterClient _router;
    private readonly IHomeStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<RouterWatch> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);

    public RouterWatch(
        WindsockSettings settings,
        HomeLinkReader links,
        IRouterClient router,
        IHomeStore store,
        TimeProvider time,
        ILogger<RouterWatch> logger)
    {
        _settings = settings;
        _links = links;
        _router = router;
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised, on a background thread, after a check finishes.</summary>
    public event EventHandler<RouterReport>? Checked;

    /// <summary>Whether the setting is on.</summary>
    public bool IsEnabled => _settings.Home.CheckRouter;

    /// <summary>Whether a check is running.</summary>
    public bool IsChecking => _running.CurrentCount == 0;

    /// <summary>The last report kept for the home network this PC is on, or nothing.</summary>
    public RouterReport? Latest()
    {
        if (_links.Read().Primary is not { } link)
        {
            return null;
        }

        try
        {
            return _store.ReadRouterReport(link.NetworkKey) is { } kept ? RouterReport.FromJson(kept.Report) : null;
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogStoreFailed(ex);
            return null;
        }
    }

    /// <summary>
    /// Checks the router now. Nothing when switched off; a report saying so when
    /// not on a home network. One check at a time: a second request waits for
    /// the first and runs after it.
    /// </summary>
    public async Task<RouterReport?> CheckAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return null;
        }

        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            HomeLink? link = _links.Read().Primary;
            RouterReport report = await RouterCheck.RunAsync(_router, link, _time.GetLocalNow(), cancellationToken).ConfigureAwait(false);

            if (link is not null)
            {
                try
                {
                    _store.WriteRouterReport(link.NetworkKey, report.CheckedAt, report.ToJson());
                }
                catch (Exception ex) when (HistoryFailure.IsStorage(ex))
                {
                    LogStoreFailed(ex);
                }
            }

            LogChecked(report.Answer, report.PortMappings.Count);
            Checked?.Invoke(this, report);
            return report;
        }
        finally
        {
            _running.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstWait, _time, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (IsEnabled && Due())
                {
                    _ = await CheckAsync(stoppingToken).ConfigureAwait(false);
                }

                await Task.Delay(LookAgain, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private bool Due()
    {
        if (_links.Read().Primary is not { } link)
        {
            return false;
        }

        try
        {
            return _store.ReadRouterReport(link.NetworkKey) is not { } kept || _time.GetLocalNow() - kept.At >= Every;
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogStoreFailed(ex);
            return false;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _running.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The router report could not be read or written")]
    private partial void LogStoreFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Router check: {Answer}, {Mappings} port mappings")]
    private partial void LogChecked(RouterAnswer answer, int mappings);
}
