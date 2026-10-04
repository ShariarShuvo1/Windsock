using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Windsock.App.Services;
using Windsock.App.Theming;
using Windsock.App.Views;
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Settings;
using Windsock.Core.Startup;

namespace Windsock.App.ViewModels;

/// <summary>One entry in the theme picker.</summary>
public sealed record ThemeOption(ThemePreference Preference, string Label)
{
    /// <summary>
    /// Returns the label. A record's generated ToString prints every member,
    /// which is what UI Automation would otherwise announce for the item.
    /// </summary>
    public override string ToString() => Label;
}

/// <summary>One entry in the close-behaviour picker.</summary>
public sealed record CloseOption(CloseAction Action, string Label)
{
    /// <summary>Returns the label, so automation announces it by name.</summary>
    public override string ToString() => Label;
}

/// <summary>One entry in the minute-detail picker; zero days keeps it for good.</summary>
public sealed record DetailOption(int Days, string Label)
{
    /// <summary>Returns the label, so automation announces it by name.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// Backs the Settings panel.
/// </summary>
public sealed partial class SettingsPanelViewModel : ObservableObject, IDisposable
{
    private const string HistoryUnreadable =
        "The history file could not be read, so what it holds cannot be shown. It may be damaged.";

    private readonly ISettingsStore _store;
    private readonly WindsockSettings _settings;
    private readonly ThemeManager _theme;
    private readonly IpHelperTotalsSource _totals;
    private readonly ThroughputMonitor _monitor;
#if !STORE
    private readonly NetworkRouteCache _routes;
#endif
    private readonly UpdateService _updates;
    private readonly UsageRecorder _recorder;
    private readonly IStartupSwitch _startup;
    private readonly IUsageHistoryStore _history;
    private readonly IAppDetailStore _detail;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<SettingsPanelViewModel> _logger;
    private bool _mirroring;
    private bool _historyUnreadable;
    private volatile bool _disposed;

    public SettingsPanelViewModel(
        ISettingsStore store,
        WindsockSettings settings,
        ThemeManager theme,
        IpHelperTotalsSource totals,
        ThroughputMonitor monitor,
#if !STORE
        NetworkRouteCache routes,
#endif
        UsageRecorder recorder,
        IStartupSwitch startup,
        UpdateService updates,
        IUsageHistoryStore history,
        IAppDetailStore detail,
        Dispatcher dispatcher,
        ILogger<SettingsPanelViewModel> logger,
        NotificationsCardViewModel notifications)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(detail);

        _detail = detail;

        Notifications = notifications;
#if !STORE
        ArgumentNullException.ThrowIfNull(routes);
#endif

        // Before any property below is set: setting one saves, and a user who
        // had switched update checks off made that save run against a store
        // and settings not yet assigned, which threw as the window opened.
        _store = store;
        _settings = settings;

        _dispatcher = dispatcher;
        _updates = updates;
        _updates.Changed += OnUpdatesChanged;

        CheckForUpdates = settings.CheckForUpdates;
        DescribeUpdates();

        _history = history;
        _logger = logger;
        DescribeHistory();

        recorder.Recorded += OnRecorded;
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(startup);

        _theme = theme;
        _totals = totals;
        _monitor = monitor;
#if !STORE
        _routes = routes;
#endif
        _recorder = recorder;
        _startup = startup;

        Themes =
        [
            new ThemeOption(ThemePreference.System, "System"),
            new ThemeOption(ThemePreference.Light, "Light"),
            new ThemeOption(ThemePreference.Dark, "Dark"),
        ];

        Adapters = [];
        SelectedTheme = settings.Theme;
#if !STORE
        FollowRoutes = settings.FollowRoutes;
        routes.IsEnabled = settings.FollowRoutes;
#endif

        Closings =
        [
            new CloseOption(CloseAction.Quit, "Quit Windsock"),
            new CloseOption(CloseAction.KeepRunning, "Keep running in the background"),
        ];

        SelectedClosing = settings.OnClose;

        List<DetailOption> keeping =
        [
            new DetailOption(0, "Forever"),
            new DetailOption(730, "2 years"),
            new DetailOption(365, "1 year"),
            new DetailOption(182, "6 months"),
            new DetailOption(90, "3 months"),
            new DetailOption(30, "1 month"),
        ];

        // A value written into settings.json by hand is what compaction uses,
        // so it is shown as itself rather than as "Forever".
        int kept = settings.KeepMinuteDetailDays;

        if (kept > 0 && !keeping.Exists(option => option.Days == kept))
        {
            keeping.Add(new DetailOption(kept, string.Format(CultureInfo.CurrentCulture, "{0:N0} days", kept)));
        }

        DetailOptions = keeping;

        _mirroring = true;
        SelectedDetailDays = Math.Max(0, kept);
        _mirroring = false;

        IsRecording = recorder.IsRecording;
        recorder.RecordingChanged += OnRecordingChanged;
        StartWithWindows = startup.State == StartupState.On;
        StartupNote = startup.Detail;
        Refresh();
    }

    /// <summary>
    /// The Notifications card. Its own view model, disposed with the window's
    /// scope, because it listens to a service that outlives the window.
    /// </summary>
    public NotificationsCardViewModel Notifications { get; }

    /// <summary>Whether Windsock looks for a newer version on its own.</summary>
    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; } = true;

    /// <summary>What the search for an update has found, in one line.</summary>
    [ObservableProperty]
    public partial string UpdateStatus { get; private set; } = string.Empty;

    /// <summary>Whether this copy is one the updater can replace at all.</summary>
    public bool CanUpdate => _updates.CanUpdate;

    /// <summary>Why it cannot, when it cannot.</summary>
    public static string NoUpdateReason =>
        "This copy was not installed by the updater, so it cannot replace itself. "
        + "Installed from the Microsoft Store, Windows keeps it up to date; "
        + "unzipped by hand, download a newer one from the releases page.";

    /// <summary>The version running now.</summary>
    public string CurrentVersion => _updates.CurrentVersion;

    /// <summary>Who wrote it.</summary>
    public static string Developer => "Shariar Shuvo";

    /// <summary>Where the source lives.</summary>
    public static string SourceUrl => "https://github.com/ShariarShuvo1/Windsock";

    /// <summary>Whether a check or a download is in flight.</summary>
    public bool IsBusy =>
        _updates.Stage is UpdateStage.Checking or UpdateStage.Downloading;

    /// <summary>Whether there is something to fetch.</summary>
    public bool CanDownload => _updates.Stage == UpdateStage.Available;

    /// <summary>Whether a fetched version is waiting for a restart.</summary>
    public bool CanRestart => _updates.Stage == UpdateStage.Ready;

    [RelayCommand]
    private async Task CheckNowAsync() => await _updates.CheckAsync(quietly: false).ConfigureAwait(true);

    [RelayCommand]
    private async Task DownloadUpdateAsync() => await _updates.DownloadAsync().ConfigureAwait(true);

    [RelayCommand]
    private void RestartForUpdate() => _updates.ApplyAndRestart();

    [RelayCommand]
    private static void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Stops listening to the updater and the recorder, which outlive the
    /// window this panel belongs to and would otherwise keep it.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _updates.Changed -= OnUpdatesChanged;
        _recorder.Recorded -= OnRecorded;
        _recorder.RecordingChanged -= OnRecordingChanged;
    }

    // Raised on the UI thread: the updater is only ever driven from it, and
    // awaits with ConfigureAwait(true), so every change comes back there.
    private void OnUpdatesChanged(object? sender, EventArgs e) => DescribeUpdates();

    /// <summary>
    /// A recording that has just started is history where there was none.
    /// </summary>
    /// <remarks>
    /// Raised on the recorder's writer thread. Describing the history there
    /// re-evaluates the erase button's command, and a button refuses that off
    /// its own thread: the exception landed in the recorder, which reported a
    /// write that had succeeded as failed and skipped every handler after this
    /// one.
    /// </remarks>
    private void OnRecorded(object? sender, EventArgs e)
    {
        if (_disposed || HasHistory)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_disposed)
            {
                DescribeHistory();
            }
        }));
    }

    private void DescribeUpdates()
    {
        UpdateStatus = _updates.Detail;

        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanRestart));
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        _settings.CheckForUpdates = value;
        _store.Save(_settings);
    }

    /// <summary>The schemes on offer.</summary>
    public IReadOnlyList<ThemeOption> Themes { get; }

#if !STORE
    /// <summary>Whether the way to each far end is found and drawn.</summary>
    [ObservableProperty]
    public partial bool FollowRoutes { get; set; } = true;
#endif

    /// <summary>What closing the window can be made to do.</summary>
    public IReadOnlyList<CloseOption> Closings { get; } = [];

    /// <summary>What closing the window does.</summary>
    [ObservableProperty]
    public partial CloseAction SelectedClosing { get; set; }

    /// <summary>Whether usage is being written to the history database.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordingLabel))]
    public partial bool IsRecording { get; set; } = true;

    /// <summary>
    /// What the button offers to do, which is the opposite of what is
    /// happening.
    /// </summary>
    public string RecordingLabel => IsRecording ? "Pause recording" : "Resume recording";

    /// <summary>Stops writing new minutes to history, or starts again.</summary>
    /// <summary>How much history there is, said in the settings themselves.</summary>
    [ObservableProperty]
    public partial string HistorySize { get; private set; } = string.Empty;

    /// <summary>
    /// Whether there is any history to delete. False when the file cannot be
    /// read, since there is then nothing the delete could be shown to cover.
    /// </summary>
    /// <remarks>
    /// As last read, not asked afresh: a command refresh asks this on the UI
    /// thread, and the file can be busy for a long while being rewritten by
    /// Reclaim space, which would hold the window still for all of it.
    /// </remarks>
    public bool HasHistory => _hasHistory;

    private volatile bool _hasHistory;
    private int _describes;

    /// <summary>Whether everything is being deleted right now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EraseHistoryCommand), nameof(ReclaimSpaceCommand))]
    public partial bool IsErasing { get; private set; }

    private bool CanErase => _hasHistory && !IsReclaiming && !IsErasing;

    /// <summary>
    /// Deletes all history, after asking. The file is read and erased away
    /// from the UI thread: erasing rewrites it, which takes a while now that
    /// every app's minutes are kept.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanErase))]
    private async Task EraseHistoryAsync()
    {
        IsErasing = true;

        try
        {
            UsageExtent extent;
            UsageSummary summary;

            try
            {
                (extent, summary) = await Task.Run(() =>
                {
                    UsageExtent reach = _history.Extent();
                    UsageSummary total = reach is { First: { } first, Last: { } last }
                        ? _history.Summarise(first, last.AddMinutes(1))
                        : UsageSummary.Empty;
                    return (reach, total);
                }).ConfigureAwait(true);
            }
            catch (Exception ex) when (HistoryFailure.IsStorage(ex))
            {
                LogHistoryUnreadable(ex);
                _historyUnreadable = true;
                HistorySize = HistoryUnreadable;
                return;
            }

            if (!extent.HasData || !EraseHistoryDialog.Ask(extent, summary, Application.Current?.MainWindow))
            {
                return;
            }

            try
            {
                await Task.Run(_history.RemoveEverything).ConfigureAwait(true);
            }
            catch (Exception ex) when (HistoryFailure.IsStorage(ex))
            {
                LogEraseFailed(ex);
                HistorySize = "The history could not be deleted, because the file could not be written. It may be damaged.";
                return;
            }

            DescribeHistory();
        }
        finally
        {
            IsErasing = false;
        }
    }

    /// <summary>Reads how far history reaches and how big the file is, away from the UI thread.</summary>
    private void DescribeHistory() => _ = DescribeHistoryAsync();

    private async Task DescribeHistoryAsync()
    {
        int token = ++_describes;

        (bool readable, UsageExtent extent, string size) = await Task.Run(() =>
        {
            bool ok = TryReadExtent(out UsageExtent reach);
            return (ok, reach, FileSize());
        }).ConfigureAwait(true);

        if (token != _describes || _disposed)
        {
            return;
        }

        HistorySize = !readable
            ? HistoryUnreadable
            : extent is { First: { } first, Last: { } last }
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    "Recorded from {0:d MMMM yyyy} to {1:d MMMM yyyy}. The file takes {2}.",
                    first.LocalDateTime,
                    last.LocalDateTime,
                    size)
                : "Nothing is recorded yet.";

        _hasHistory = readable && extent.HasData;
        OnPropertyChanged(nameof(HasHistory));
        EraseHistoryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Asks how far history reaches, answering false rather than throwing when
    /// the file cannot say. A damaged file must not be described as an empty
    /// one, and must not stop the window opening.
    /// </summary>
    private bool TryReadExtent(out UsageExtent extent)
    {
        try
        {
            extent = _history.Extent();
            _historyUnreadable = false;
            return true;
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            extent = default;

            // Asked on every command refresh, so said once rather than each time.
            if (!_historyUnreadable)
            {
                _historyUnreadable = true;
                LogHistoryUnreadable(ex);
            }

            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the usage history")]
    private partial void LogHistoryUnreadable(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the usage history")]
    private partial void LogEraseFailed(Exception exception);

    [RelayCommand]
    private void ToggleRecording() => IsRecording = !IsRecording;

    /// <summary>Whether Windows starts Windsock when this user signs in.</summary>
    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    /// <summary>
    /// Why the tick above will not stay on, when it will not. Empty when there
    /// is nothing to say, which is the ordinary case.
    /// </summary>
    [ObservableProperty]
    public partial string StartupNote { get; set; } = string.Empty;

    /// <summary>Every adapter, each with its own tick.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AdapterChoiceViewModel> Adapters { get; private set; }

    /// <summary>What the closed adapter button says.</summary>
    [ObservableProperty]
    public partial string AdapterSummary { get; private set; } = "All adapters";

    /// <summary>Whether the adapter list is showing.</summary>
    [ObservableProperty]
    public partial bool IsAdapterListOpen { get; set; }

    /// <summary>The scheme in use.</summary>
    [ObservableProperty]
    public partial ThemePreference SelectedTheme { get; set; }

    /// <summary>
    /// Re-reads the adapters, keeping whatever is selected.
    /// </summary>
    public void Refresh()
    {
        Notifications.Refresh();

        // Asking Windows takes a round trip in the packaged edition, so the
        // cached answer goes up first and the fresh one follows.
        _mirroring = true;
        StartWithWindows = _startup.State == StartupState.On;
        _mirroring = false;
        _ = ReadStartupAsync();

        HashSet<ulong> chosen = [.. _settings.Adapters];
        bool takeEverything = chosen.Count == 0;
        List<AdapterChoiceViewModel> choices = [];

        foreach (NetworkAdapter adapter in _totals.ListAdapters())
        {
            choices.Add(new AdapterChoiceViewModel(
                adapter.Id,
                adapter.IsUp
                    ? adapter.Name
                    : string.Format(CultureInfo.CurrentCulture, "{0} (disconnected)", adapter.Name),
                takeEverything || chosen.Contains(adapter.Id)));

            chosen.Remove(adapter.Id);
        }
        foreach (ulong missing in chosen)
        {
            choices.Add(new AdapterChoiceViewModel(missing, "Chosen adapter (not present)", selected: true));
        }

        foreach (AdapterChoiceViewModel choice in choices)
        {
            choice.Changed += OnChoiceChanged;
        }

        Adapters = choices;
        Apply(save: false);
    }

    private void OnChoiceChanged(object? sender, EventArgs e)
    {
        if (Adapters.Count > 0 && !Adapters.Any(choice => choice.IsSelected))
        {
            if (sender is AdapterChoiceViewModel last)
            {
                last.IsSelected = true;
            }

            return;
        }

        Apply(save: true);
    }

    private void Apply(bool save)
    {
        ulong[] selection = [.. Adapters.Where(choice => choice.IsSelected).Select(choice => choice.Id)];
        bool everything = selection.Length == Adapters.Count;

        _totals.Selection = everything ? [] : selection;
        _monitor.Rebase();
        Lock(selection.Length);
        Describe(selection.Length);

        if (!save)
        {
            return;
        }

        _settings.Adapters = everything ? [] : [.. selection];
        _store.Save(_settings);
    }

    private void Lock(int selected)
    {
        foreach (AdapterChoiceViewModel choice in Adapters)
        {
            choice.IsLocked = selected == 1 && choice.IsSelected;
        }
    }

    private void Describe(int selected) =>
        AdapterSummary = selected == Adapters.Count
            ? "All adapters"
            : selected == 1
                ? Adapters.First(choice => choice.IsSelected).Label
                : string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} of {1} adapters",
                    selected,
                    Adapters.Count);

    partial void OnSelectedThemeChanged(ThemePreference value)
    {
        _theme.Apply(value);

        _settings.Theme = value;
        _store.Save(_settings);
    }

    partial void OnSelectedClosingChanged(CloseAction value)
    {
        if (_mirroring)
        {
            return;
        }

        _settings.OnClose = value;
        _store.Save(_settings);
    }

    /// <summary>How long minute-by-minute detail is kept.</summary>
    public IReadOnlyList<DetailOption> DetailOptions { get; } = [];

    /// <summary>Days of minute detail kept; zero for good.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailNote))]
    public partial int SelectedDetailDays { get; set; }

    /// <summary>What the chosen keeping does, in words.</summary>
    public string DetailNote => SelectedDetailDays <= 0
        ? "Every minute is kept for good."
        : "Each app's minutes older than that are folded into quarter hours. No total is lost.";

    /// <summary>Whether space is being given back to the disk right now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReclaimSpaceCommand), nameof(EraseHistoryCommand))]
    public partial bool IsReclaiming { get; private set; }

    partial void OnSelectedDetailDaysChanged(int value)
    {
        if (_mirroring)
        {
            return;
        }

        _settings.KeepMinuteDetailDays = Math.Max(0, value);
        _store.Save(_settings);
    }

    private bool CanReclaim => !IsReclaiming && !IsErasing;

    /// <summary>
    /// Folds away the minutes the chosen keeping no longer wants, then rewrites
    /// the file so the space goes back to the disk. The folding happens by
    /// itself within a few hours; this does it now, and shrinks the file, which
    /// nothing else does.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReclaim))]
    private async Task ReclaimSpaceAsync()
    {
        IsReclaiming = true;
        int days = _settings.KeepMinuteDetailDays;
        string before = FileSize();

        try
        {
            await Task.Run(() =>
            {
                if (days > 0)
                {
                    _detail.CompactAppDetail(EpochMinute.From(DateTimeOffset.UtcNow) - ((long)days * 24 * 60));
                }

                _detail.Reclaim();
            }).ConfigureAwait(true);

            ReclaimNote = string.Format(CultureInfo.CurrentCulture, "Was {0}, now {1}.", before, FileSize());
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogHistoryUnreadable(ex);
            ReclaimNote = "The file could not be rewritten. It may be in use or damaged; nothing was lost.";
        }
        finally
        {
            IsReclaiming = false;
            DescribeHistory();
        }
    }

    /// <summary>What the last reclaim did.</summary>
    [ObservableProperty]
    public partial string ReclaimNote { get; private set; } = string.Empty;

    private string FileSize() => Windsock.Core.Formatting.SizeFormatter.Format(_detail.FileBytes);

    partial void OnIsRecordingChanged(bool value)
    {
        if (_mirroring)
        {
            return;
        }

        _recorder.IsRecording = value;

        _settings.RecordHistory = value;
        _store.Save(_settings);
    }

    private void OnRecordingChanged(object? sender, EventArgs e)
    {
        _mirroring = true;
        IsRecording = _recorder.IsRecording;
        _mirroring = false;
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_mirroring)
        {
            return;
        }

        _ = WriteStartupAsync(value);
    }

    /// <summary>
    /// Asks Windows to change the setting, then shows what it actually became.
    /// The tick is not taken as truth: a user who turned Windsock off under
    /// Startup apps has the final say, and the tick goes back down.
    /// </summary>
    private async Task WriteStartupAsync(bool wanted)
    {
        StartupState state = await _startup.SetAsync(wanted).ConfigureAwait(true);
        Show(state);
    }

    private async Task ReadStartupAsync()
    {
        StartupState state = await _startup.RefreshAsync().ConfigureAwait(true);
        Show(state);
    }

    private void Show(StartupState state)
    {
        StartupNote = _startup.Detail;

        bool on = state == StartupState.On;

        if (on == StartWithWindows)
        {
            return;
        }

        _mirroring = true;
        StartWithWindows = on;
        _mirroring = false;
    }

#if !STORE
    partial void OnFollowRoutesChanged(bool value)
    {
        _routes.IsEnabled = value;

        _settings.FollowRoutes = value;
        _store.Save(_settings);
    }
#endif
}
