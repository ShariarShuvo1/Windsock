using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windsock.Core.Alerts;

namespace Windsock.App.Services.Alerts;

/// <summary>
/// Starts every alert that watches something, as the settings say, and stops
/// them with the app.
/// </summary>
/// <remarks>
/// Each source watches only while its alerts are on. The Alerts page calls
/// <see cref="Apply"/> after changing a switch, which starts or stops just the
/// sources whose switches changed.
/// </remarks>
public sealed partial class AlertService : IHostedService
{
    private readonly IReadOnlyList<IAlertSource> _sources;
    private readonly ProxyWatch _proxy;
    private readonly ILogger<AlertService> _logger;

    public AlertService(
        IEnumerable<IAlertSource> sources,
        ISessionEvents session,
        ProxyWatch proxy,
        ILogger<AlertService> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(logger);

        // The session events are taken here, unused, so the wake that starts
        // every watcher's grace again is heard from the start.
        _sources = [.. sources];
        _proxy = proxy;
        _logger = logger;
        _proxy.ByWindowsSettings = WindowsSettingsInFront;
    }

    /// <summary>Starts or stops each source to match the settings.</summary>
    public void Apply()
    {
        foreach (IAlertSource source in _sources)
        {
            try
            {
                source.Apply();
            }
#pragma warning disable CA1031 // One alert failing to start must not take the others, or the app, with it.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogApplyFailed(ex, source.GetType().Name);
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
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Whether the window in front belongs to Windows' Settings or Internet
    /// Options - the way a person changes the proxy - rather than to whatever
    /// program might have changed it.
    /// </summary>
    private static unsafe bool WindowsSettingsInFront()
    {
        HWND front = PInvoke.GetForegroundWindow();

        if (front == HWND.Null)
        {
            return false;
        }

        uint process;
        _ = PInvoke.GetWindowThreadProcessId(front, &process);

        try
        {
            using Process owner = Process.GetProcessById((int)process);
            return ProxyJudge.IsSettingsApp(owner.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Source} alerts could not be started or stopped")]
    private partial void LogApplyFailed(Exception error, string source);
}
