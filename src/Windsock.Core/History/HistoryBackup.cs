using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.Settings;

namespace Windsock.Core.History;

/// <summary>One copy of the history file.</summary>
/// <param name="Path">Where it is.</param>
/// <param name="Date">The local date it was made, from its name.</param>
/// <param name="Bytes">How big it is.</param>
public sealed record BackupInfo(string Path, DateOnly Date, long Bytes);

/// <summary>Names backup files and works out which to keep.</summary>
public static class BackupRotation
{
    private const string Prefix = "Windsock-";
    private const string Extension = ".db";
    private const string DatePattern = "yyyy-MM-dd";

    /// <summary>How old the newest copy may get before another is made.</summary>
    public const int EveryDays = 7;

    /// <summary>The name of a copy made on a date: <c>Windsock-2026-10-05.db</c>.</summary>
    public static string NameFor(DateOnly date) =>
        string.Concat(Prefix, date.ToString(DatePattern, CultureInfo.InvariantCulture), Extension);

    /// <summary>The date in a copy's name, or null for a file that is not one of Windsock's copies.</summary>
    public static DateOnly? DateOf(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        string name = Path.GetFileName(fileName);

        if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            || name.Length != Prefix.Length + DatePattern.Length + Extension.Length)
        {
            return null;
        }

        return DateOnly.TryParseExact(
            name.AsSpan(Prefix.Length, DatePattern.Length),
            DatePattern,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateOnly date)
            ? date
            : null;
    }

    /// <summary>Whether a new copy is due: none yet, or the newest a week old.</summary>
    public static bool IsDue(DateOnly? newest, DateOnly today) =>
        newest is not { } last || today.DayNumber - last.DayNumber >= EveryDays;

    /// <summary>
    /// The copies to delete so that <paramref name="keep"/> remain: the oldest
    /// first. Files that are not Windsock's copies are never named.
    /// </summary>
    public static IReadOnlyList<string> ToRemove(IEnumerable<string> files, int keep)
    {
        ArgumentNullException.ThrowIfNull(files);

        List<(string Path, DateOnly Date)> copies = [];

        foreach (string file in files)
        {
            if (DateOf(file) is { } date)
            {
                copies.Add((file, date));
            }
        }

        copies.Sort(static (left, right) => right.Date.CompareTo(left.Date));
        return [.. copies.Skip(Math.Max(0, keep)).Select(copy => copy.Path)];
    }
}

/// <summary>
/// Keeps a copy of the history file beside it, made once a week, the oldest
/// let go once there are more than the settings keep.
/// </summary>
/// <remarks>
/// <para>
/// The copy is made with SQLite's online backup while the store holds its own
/// lock (<see cref="IHistoryFile.BackupTo"/>), so no write, erase or VACUUM is
/// part way through, and it is checked before it is kept. It is written under a
/// temporary name and renamed, so a copy that exists is a whole one.
/// </para>
/// <para>
/// A copy holds history, so erasing history erases the copies too: any erase
/// deletes them all, and a fresh one is made from what is left soon after.
/// Otherwise "erase" would leave the very thing erased sitting in the folder
/// beside it.
/// </para>
/// </remarks>
public sealed partial class HistoryBackup : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LookEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan AfterErase = TimeSpan.FromMinutes(1);

    private readonly IHistoryFile _file;
    private readonly IUsageHistoryStore _history;
    private readonly WindsockSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<HistoryBackup> _logger;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly SemaphoreSlim _nudge = new(0, 1);
    private bool _erased;

    /// <summary>Copies of erased history that could not all be deleted yet; tried again at every look.</summary>
    private bool _forgetPending;

    public HistoryBackup(IHistoryFile file, IUsageHistoryStore history, WindsockSettings settings, TimeProvider clock, ILogger<HistoryBackup> logger)
        : this(file, history, settings, Path.Combine(Path.GetDirectoryName(file?.FilePath) ?? ".", "Backups"), clock, logger)
    {
    }

    internal HistoryBackup(IHistoryFile file, IUsageHistoryStore history, WindsockSettings settings, string folder, TimeProvider clock, ILogger<HistoryBackup> logger)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _file = file;
        _history = history;
        _settings = settings;
        Folder = folder;
        _clock = clock;
        _logger = logger;
        _history.Erased += OnErased;

        // An older version's erase, finished as the file opened.
        if (file.SettledOlderErase)
        {
            _erased = true;
            _forgetPending = true;
        }
    }

    /// <summary>The folder the copies are kept in, beside the history file.</summary>
    public string Folder { get; }

    /// <summary>Why the last copy could not be made, or null when it was.</summary>
    public string? Problem { get; private set; }

    /// <summary>Raised when a copy is made or let go, or one could not be made.</summary>
    public event EventHandler? Changed;

    /// <summary>The copies there are, newest first.</summary>
    public IReadOnlyList<BackupInfo> Backups()
    {
        List<BackupInfo> copies = [];

        try
        {
            if (!Directory.Exists(Folder))
            {
                return copies;
            }

            foreach (string path in Directory.EnumerateFiles(Folder, "Windsock-*.db"))
            {
                if (BackupRotation.DateOf(path) is { } date)
                {
                    copies.Add(new BackupInfo(path, date, new FileInfo(path).Length));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        copies.Sort(static (left, right) => right.Date.CompareTo(left.Date));
        return copies;
    }

    /// <summary>Makes a copy now, whatever the schedule says, and lets the oldest go.</summary>
    public Task<BackupInfo?> BackUpNowAsync() => Task.Run(() => BackUp(_clock.GetLocalNow()));

    private void OnErased(object? sender, EventArgs e)
    {
        _erased = true;
        _forgetPending = true;
        Forget();

        _nudge.Signal();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, _clock, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                RunDue(_clock.GetLocalNow());

                if (await _nudge.WaitAsync(LookEvery, stoppingToken).ConfigureAwait(false))
                {
                    // After an erase: give the person a moment to erase more.
                    await Task.Delay(AfterErase, _clock, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Makes a copy if one is due. Internal so tests can drive it.</summary>
    internal BackupInfo? RunDue(DateTimeOffset now)
    {
        // Copies of erased history go whether or not copies are still wanted.
        if (_forgetPending)
        {
            Forget();
        }

        BackupSettings choices = _settings.Usage.Backup;

        if (!choices.Enabled || _file.State == HistoryFileState.Unavailable)
        {
            return null;
        }

        DateOnly today = DateOnly.FromDateTime(now.DateTime);
        IReadOnlyList<BackupInfo> copies = Backups();
        DateOnly? newest = copies.Count > 0 ? copies[0].Date : null;

        if (!_erased && !BackupRotation.IsDue(newest, today))
        {
            return null;
        }

        try
        {
            if (!_history.Extent().HasData)
            {
                return null;
            }
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            return null;
        }

        return BackUp(now);
    }

    /// <summary>Makes a copy and lets the oldest go.</summary>
    internal BackupInfo? BackUp(DateTimeOffset now)
    {
        _one.Wait();

        try
        {
            DateOnly today = DateOnly.FromDateTime(now.DateTime);
            string path = Path.Combine(Folder, BackupRotation.NameFor(today));
            string temporary = path + ".tmp";

            Directory.CreateDirectory(Folder);
            _file.BackupTo(temporary);
            File.Move(temporary, path, overwrite: true);
            _erased = false;
            Problem = null;

            foreach (string old in BackupRotation.ToRemove(Directory.EnumerateFiles(Folder, "Windsock-*.db"), _settings.Usage.Backup.KeepHeld))
            {
                File.Delete(old);
            }

            BackupInfo made = new(path, today, new FileInfo(path).Length);
            LogBackedUp(made.Bytes);
            Changed?.Invoke(this, EventArgs.Empty);
            return made;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || HistoryFailure.IsStorage(ex))
        {
            Problem = ex.Message;
            LogBackupFailed(ex);
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// Deletes every copy, and any copy left half made, since they hold history
    /// that is no longer there. Each file is tried on its own; any that could
    /// not go is tried again at the next look.
    /// </summary>
    private void Forget()
    {
        _one.Wait();
        bool all = true;

        try
        {
            IEnumerable<string> files = Directory.Exists(Folder)
                ? Directory.EnumerateFiles(Folder, "Windsock-*.db*")
                    .Where(path => BackupRotation.DateOf(path) is not null
                        || (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && BackupRotation.DateOf(path[..^4]) is not null))
                    .ToList()
                : [];

            foreach (string path in files)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    all = false;
                    LogForgetFailed(ex);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            all = false;
            LogForgetFailed(ex);
        }
        finally
        {
            _forgetPending = !all;
            _one.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public override void Dispose()
    {
        _history.Erased -= OnErased;
        base.Dispose();
        _one.Dispose();
        _nudge.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Backed up the history file ({Bytes} bytes)")]
    private partial void LogBackedUp(long bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not back up the history file")]
    private partial void LogBackupFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the history backups after an erase")]
    private partial void LogForgetFailed(Exception exception);
}
