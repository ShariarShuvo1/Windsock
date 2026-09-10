using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windsock.Core.Startup;
using AppModel = Windows.ApplicationModel;

namespace Windsock.App.Services;

/// <summary>
/// Run at login for the packaged edition, through the StartupTask declared in
/// AppxManifest.xml.
/// </summary>
/// <remarks>
/// <para>
/// The unpackaged edition writes an HKCU Run value and that is the end of it.
/// Inside the MSIX container the same write is redirected into a private copy
/// of the registry, so it succeeds, reads back as written, and Windows starts
/// nothing. A packaged app declares a StartupTask and asks Windows to switch
/// it, which is also what lists it under Settings and Task Manager where a
/// person expects to find it.
/// </para>
/// <para>
/// <see cref="TaskId"/> must match the TaskId in AppxManifest.xml exactly.
/// Windows matches on that string and answers with a plain failure if it does
/// not, so a typo here shows up as the setting never working rather than as an
/// error anyone would notice.
/// </para>
/// <para>
/// The user may turn this off in Task Manager, and after that Windsock is not
/// allowed to turn it back on. That is <see cref="StartupState.BlockedByUser"/>
/// and it is reported rather than retried, because retrying would be the app
/// arguing with a decision the person already made.
/// </para>
/// </remarks>
internal sealed partial class PackagedStartup : IStartupSwitch
{
    /// <summary>The TaskId in AppxManifest.xml. Change both or neither.</summary>
    public const string TaskId = "WindsockStartup";

    private readonly ILogger<PackagedStartup> _logger;

    public PackagedStartup(ILogger<PackagedStartup> logger)
    {
        _logger = logger;
        State = StartupState.Off;
        Detail = string.Empty;
    }

    /// <inheritdoc />
    public StartupState State { get; private set; }

    /// <inheritdoc />
    public string Detail { get; private set; }

    /// <inheritdoc />
    public async Task<StartupState> RefreshAsync()
    {
        try
        {
            AppModel.StartupTask task = await AppModel.StartupTask.GetAsync(TaskId);
            Settle(task.State);
        }
        catch (Exception error) when (IsExpected(error))
        {
            LogUnavailable(error);
            Fail("Windows did not answer about starting Windsock at sign-in.");
        }

        return State;
    }

    /// <inheritdoc />
    public async Task<StartupState> SetAsync(bool wanted)
    {
        try
        {
            AppModel.StartupTask task = await AppModel.StartupTask.GetAsync(TaskId);

            if (wanted)
            {
                Settle(await task.RequestEnableAsync());
            }
            else
            {
                // Disable returns nothing, so the state is read back rather
                // than assumed. It is also the one direction that always works:
                // an app may always stop itself starting.
                task.Disable();
                Settle(task.State);
            }
        }
        catch (Exception error) when (IsExpected(error))
        {
            LogSetFailed(error);
            Fail("Windows would not change whether Windsock starts at sign-in.");
        }

        return State;
    }

    /// <summary>
    /// Anything the platform can plausibly throw here. Kept to a list rather
    /// than catching everything, because the house rule is that an unguarded
    /// failure should still take the process down rather than leave a reading
    /// nobody can trust.
    /// </summary>
    private static bool IsExpected(Exception error) =>
        error is COMException or InvalidOperationException or UnauthorizedAccessException
            or NotSupportedException or PlatformNotSupportedException or ArgumentException;

    private void Settle(AppModel.StartupTaskState state)
    {
        Detail = string.Empty;

        State = state switch
        {
            AppModel.StartupTaskState.Enabled or AppModel.StartupTaskState.EnabledByPolicy => StartupState.On,
            AppModel.StartupTaskState.DisabledByUser => StartupState.BlockedByUser,
            AppModel.StartupTaskState.DisabledByPolicy => StartupState.Unavailable,
            _ => StartupState.Off,
        };

        Detail = State switch
        {
            StartupState.BlockedByUser =>
                "Turned off in Task Manager. Switch Windsock back on under Startup apps there.",
            StartupState.Unavailable =>
                "Turned off by a policy on this machine.",
            _ => string.Empty,
        };

        LogState(state);
    }

    private void Fail(string detail)
    {
        State = StartupState.Unavailable;
        Detail = detail;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Startup task reports {State}")]
    private partial void LogState(AppModel.StartupTaskState state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not read the startup task")]
    private partial void LogUnavailable(Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not change the startup task")]
    private partial void LogSetFailed(Exception error);
}
