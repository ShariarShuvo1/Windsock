using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Windsock.App.Services;

/// <summary>Where the search for a newer version has got to.</summary>
public enum UpdateStage
{
    Idle,

    Unsupported,

    Checking,

    Current,

    Available,

    Downloading,

    Ready,

    Failed,
}

/// <summary>
/// Looks for a newer Windsock, fetches it, and puts it in place on restart.
/// </summary>
public sealed partial class UpdateService
{
    private const string Releases = "https://github.com/ShariarShuvo1/Windsock";
    private readonly ILogger<UpdateService> _logger;
    private readonly UpdateManager? _manager;
    private UpdateInfo? _found;

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;

        try
        {
            _manager = new UpdateManager(new GithubSource(Releases, null, prerelease: false));
        }
#pragma warning disable CA1031 // Any failure to construct it means the same thing: this copy cannot update itself.
        catch (Exception error)
#pragma warning restore CA1031
        {
            LogNoUpdater(error);
            _manager = null;
        }

        Stage = CanUpdate ? UpdateStage.Idle : UpdateStage.Unsupported;
    }

    /// <summary>Raised on the UI thread when the stage or the detail changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Where the search has got to.</summary>
    public UpdateStage Stage { get; private set; }

    /// <summary>What to say about it, in one line.</summary>
    public string Detail { get; private set; } = string.Empty;

    /// <summary>The version found, when one was.</summary>
    public string? NewVersion { get; private set; }

    /// <summary>
    /// Whether this copy is one the updater can replace.
    /// </summary>
    public bool CanUpdate => _manager is { IsInstalled: true };

    /// <summary>The version running now.</summary>
    public string CurrentVersion =>
        _manager?.CurrentVersion?.ToString()
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    /// <summary>Looks for a newer version.</summary>
    public async Task CheckAsync(bool quietly)
    {
        if (_manager is null || !CanUpdate || Stage is UpdateStage.Checking or UpdateStage.Downloading)
        {
            return;
        }

        Move(UpdateStage.Checking, "Looking for a newer version…");

        try
        {
            UpdateInfo? found = await _manager.CheckForUpdatesAsync().ConfigureAwait(true);

            if (found is null)
            {
                _found = null;
                NewVersion = null;
                Move(UpdateStage.Current, quietly ? string.Empty : "Windsock is up to date.");
                return;
            }

            _found = found;
            NewVersion = found.TargetFullRelease.Version.ToString();
            Move(UpdateStage.Available, $"Version {NewVersion} is available.");
            LogFound(NewVersion);
        }
#pragma warning disable CA1031 // Offline, rate limited, or a release that cannot be read: all of them mean no update today.
        catch (Exception error)
#pragma warning restore CA1031
        {
            LogCheckFailed(error);
            Move(UpdateStage.Failed, quietly ? string.Empty : "Could not reach the releases page.");
        }
    }

    /// <summary>Fetches the version already found.</summary>
    public async Task DownloadAsync()
    {
        if (_manager is null || _found is null || Stage != UpdateStage.Available)
        {
            return;
        }

        Move(UpdateStage.Downloading, "Downloading…");

        try
        {
            await _manager.DownloadUpdatesAsync(_found).ConfigureAwait(true);
            Move(UpdateStage.Ready, $"Version {NewVersion} is ready. It will be applied when Windsock restarts.");
        }
#pragma warning disable CA1031 // A download that failed leaves the running copy untouched, which is the important part.
        catch (Exception error)
#pragma warning restore CA1031
        {
            LogDownloadFailed(error);
            Move(UpdateStage.Failed, "The download did not finish.");
        }
    }

    /// <summary>
    /// Puts the fetched version in place, restarting Windsock to do it.
    /// </summary>
    public void ApplyAndRestart()
    {
        if (_manager is null || _found is null || Stage != UpdateStage.Ready)
        {
            return;
        }

        LogApplying(NewVersion ?? "unknown");
        _manager.ApplyUpdatesAndRestart(_found);
    }

    private void Move(UpdateStage stage, string detail)
    {
        Stage = stage;
        Detail = detail;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "This copy cannot update itself")]
    private partial void LogNoUpdater(Exception error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update {Version} is available")]
    private partial void LogFound(string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not check for updates")]
    private partial void LogCheckFailed(Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not download the update")]
    private partial void LogDownloadFailed(Exception error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying update {Version} and restarting")]
    private partial void LogApplying(string version);
}
