using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Windsock.App.Services;
using Windsock.Core.History;
using Windsock.Core.Reports;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>
/// Settings › Data and privacy, the history file's care: its state, weekly
/// copies kept beside it, export on a schedule to a folder, and the weekly
/// digest. Every one of these stays on this PC.
/// </summary>
public sealed partial class HistoryCareViewModel : ObservableObject, IDisposable
{
    private readonly IHistoryFile _file;
    private readonly HistoryBackup _backup;
    private readonly ScheduledExport _export;
    private readonly DigestService _digest;
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly bool _ready;

    public HistoryCareViewModel(
        IHistoryFile file,
        HistoryBackup backup,
        ScheduledExport export,
        DigestService digest,
        WindsockSettings settings,
        ISettingsStore store,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(settings);

        _file = file;
        _backup = backup;
        _export = export;
        _digest = digest;
        _settings = settings;
        _store = store;
        _dispatcher = dispatcher;

        KeepOptions = [.. Enumerable.Range(BackupSettings.FewestKept, BackupSettings.MostKept).Select(count =>
            new EditorOption<int>(count, UsageText.Count(count, "copy", "copies")))];
        EveryOptions =
        [
            new(ExportEvery.Day, "Each day"),
            new(ExportEvery.Month, "Each month"),
        ];
        FormatOptions =
        [
            new(ExportFormat.Csv, "CSV, network use"),
            new(ExportFormat.Json, "JSON, everything"),
        ];

        DayOfWeek regional = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        WeekStartOptions =
        [
            new(-1, string.Concat("As the region says (", CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(regional), ")")),
            .. Enumerable.Range(0, 7).Select(day => new EditorOption<int>(day, CultureInfo.CurrentCulture.DateTimeFormat.GetDayName((DayOfWeek)day))),
        ];

        UsageSettings usage = settings.Usage;
        BackupEnabled = usage.Backup.Enabled;
        Keep = usage.Backup.KeepHeld;
        ExportEnabled = usage.AutoExport.Enabled;
        ExportFolder = usage.AutoExport.Folder;
        Every = usage.AutoExport.Every;
        Format = usage.AutoExport.Format;
        DigestEnabled = usage.Digest.Enabled;
        WeekStarts = usage.Digest.WeekStarts is { } day ? (int)day : -1;

        _backup.Changed += OnBackupChanged;
        _export.Changed += OnExportChanged;

        DescribeFile();
        DescribeBackups();
        DescribeExport();
        _ready = true;
    }

    // ---- The file -----------------------------------------------------------

    /// <summary>Where the history file is.</summary>
    public string FilePath => _file.FilePath;

    /// <summary>What can be done with the file this run, in words.</summary>
    [ObservableProperty]
    public partial string FileState { get; private set; } = string.Empty;

    [RelayCommand]
    private void ShowFile() => FileReveal.Show(_file.FilePath);

    private void DescribeFile() => FileState = _file.State switch
    {
        HistoryFileState.NewerVersion => string.Format(
            CultureInfo.CurrentCulture,
            "Written by a newer version of Windsock (format {0}), so this version only reads it: nothing new is recorded until that version runs again. Nothing in it is damaged.",
            _file.FileSchema),
        HistoryFileState.Unavailable when _file.FileSchema > 0 => string.Format(
            CultureInfo.CurrentCulture,
            "Written by a newer version of Windsock (format {0}) in a way this version cannot read. It is left exactly as it is, and nothing is recorded this run; that version, or a later one, reads it again.",
            _file.FileSchema),
        HistoryFileState.Unavailable => "The file could not be opened, so nothing is being recorded this run.",
        _ => _file.RecoveredFromCorruption
            ? "Read and written as usual. The file before it could not be read and was set aside beside it."
            : "Read and written as usual.",
    };

    // ---- Copies -------------------------------------------------------------

    public IReadOnlyList<EditorOption<int>> KeepOptions { get; }

    [ObservableProperty]
    public partial bool BackupEnabled { get; set; }

    [ObservableProperty]
    public partial int Keep { get; set; }

    [ObservableProperty]
    public partial string BackupNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBackingUp { get; private set; }

    partial void OnBackupEnabledChanged(bool value)
    {
        if (_ready)
        {
            _settings.Usage.Backup.Enabled = value;
            Save();
        }
    }

    partial void OnKeepChanged(int value)
    {
        if (_ready)
        {
            _settings.Usage.Backup.Keep = value;
            Save();
        }
    }

    [RelayCommand]
    private async Task BackUpNowAsync()
    {
        IsBackingUp = true;

        try
        {
            await _backup.BackUpNowAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBackingUp = false;
            DescribeBackups();
        }
    }

    [RelayCommand]
    private void ShowBackups() => FileReveal.Show(Directory.Exists(_backup.Folder) ? _backup.Folder : Path.GetDirectoryName(_backup.Folder));

    private void OnBackupChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(DescribeBackups));

    private void DescribeBackups()
    {
        IReadOnlyList<BackupInfo> copies = _backup.Backups();

        string kept = copies.Count == 0
            ? "No copy yet."
            : string.Format(
                CultureInfo.CurrentCulture,
                "{0}, the newest from {1}, {2}.",
                UsageText.Count(copies.Count, "copy", "copies"),
                UsageText.FullDate(copies[0].Date),
                UsageText.Bytes(copies[0].Bytes));

        BackupNote = _backup.Problem is { } problem ? string.Concat(kept, " The last copy could not be made: ", problem) : kept;
    }

    // ---- Export on a schedule ------------------------------------------------

    public IReadOnlyList<EditorOption<ExportEvery>> EveryOptions { get; }

    public IReadOnlyList<EditorOption<ExportFormat>> FormatOptions { get; }

    [ObservableProperty]
    public partial bool ExportEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderText), nameof(HasFolder))]
    public partial string ExportFolder { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial ExportEvery Every { get; set; }

    [ObservableProperty]
    public partial ExportFormat Format { get; set; }

    [ObservableProperty]
    public partial string ExportNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsExporting { get; private set; }

    public bool HasFolder => ExportFolder.Length > 0;

    public string FolderText => HasFolder ? ExportFolder : "No folder chosen";

    partial void OnExportEnabledChanged(bool value)
    {
        if (!_ready)
        {
            return;
        }

        // Switching it on with nowhere to write asks where first.
        if (value && !HasFolder && !ChooseFolderNow())
        {
            ExportEnabled = false;
            return;
        }

        _settings.Usage.AutoExport.Enabled = value;
        Save();
        DescribeExport();

        if (value)
        {
            _ = ExportNowAsync();
        }
    }

    partial void OnEveryChanged(ExportEvery value)
    {
        if (_ready)
        {
            _settings.Usage.AutoExport.Every = value;
            Save();
        }
    }

    partial void OnFormatChanged(ExportFormat value)
    {
        if (_ready)
        {
            _settings.Usage.AutoExport.Format = value;
            Save();
        }
    }

    [RelayCommand]
    private void ChooseFolder()
    {
        if (ChooseFolderNow() && ExportEnabled)
        {
            _ = ExportNowAsync();
        }
    }

    private bool ChooseFolderNow()
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Where should Windsock write its exports?",
            InitialDirectory = HasFolder && Directory.Exists(ExportFolder) ? ExportFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        ExportFolder = dialog.FolderName;
        _settings.Usage.AutoExport.Folder = dialog.FolderName;
        Save();
        DescribeExport();
        return true;
    }

    [RelayCommand]
    private async Task ExportNowAsync()
    {
        if (!ExportEnabled || !HasFolder)
        {
            return;
        }

        IsExporting = true;

        try
        {
            await _export.RunNowAsync().ConfigureAwait(true);
        }
        finally
        {
            IsExporting = false;
            DescribeExport();
        }
    }

    [RelayCommand]
    private void ShowExports() => FileReveal.Show(ExportFolder);

    private void OnExportChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(DescribeExport));

    private void DescribeExport()
    {
        if (_export.LastRun is not { } run)
        {
            ExportNote = ExportEnabled
                ? "Each finished day or month is written within the hour, and any missed while the PC was off are caught up."
                : string.Empty;
            return;
        }

        string what = run.Written.Count switch
        {
            0 when run.Empty > 0 => "nothing was recorded to write",
            0 => "nothing new to write",
            1 => "wrote " + Path.GetFileName(run.Written[0]),
            _ => string.Format(CultureInfo.CurrentCulture, "wrote {0} files", run.Written.Count),
        };

        string text = string.Format(CultureInfo.CurrentCulture, "Last run {0}: {1}.", UsageText.When(run.At), what);

        if (run.LeftAlone.Count > 0)
        {
            text += string.Format(CultureInfo.CurrentCulture, " {0} already there and left alone.", UsageText.Count(run.LeftAlone.Count, "file was", "files were"));
        }

        if (run.Problem is { } problem)
        {
            text += " " + problem;
        }

        ExportNote = text;
    }

    // ---- The weekly digest ---------------------------------------------------

    public IReadOnlyList<EditorOption<int>> WeekStartOptions { get; }

    [ObservableProperty]
    public partial bool DigestEnabled { get; set; }

    /// <summary>The day weeks start on, or -1 for the region's.</summary>
    [ObservableProperty]
    public partial int WeekStarts { get; set; }

    [ObservableProperty]
    public partial string DigestPreview { get; private set; } = string.Empty;

    partial void OnDigestEnabledChanged(bool value)
    {
        if (_ready)
        {
            _settings.Usage.Digest.Enabled = value;
            Save();
        }
    }

    partial void OnWeekStartsChanged(int value)
    {
        if (_ready)
        {
            _settings.Usage.Digest.WeekStarts = value is >= 0 and < 7 ? (DayOfWeek)value : null;
            Save();
        }
    }

    [RelayCommand]
    private async Task PreviewDigestAsync()
    {
        try
        {
            Core.Notifications.Notice? notice = await Task.Run(_digest.Preview).ConfigureAwait(true);
            DigestPreview = notice is null
                ? "Nothing was recorded last week, so there would be no digest."
                : string.Concat(notice.Title, ": ", notice.Message);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            DigestPreview = "Last week could not be read from the history file.";
        }
    }

    public void Dispose()
    {
        _backup.Changed -= OnBackupChanged;
        _export.Changed -= OnExportChanged;
    }

    private void Save() => _store.Save(_settings);
}
