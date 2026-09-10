using System.Diagnostics;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

/// <summary>
/// Backs the Settings panel.
/// </summary>
public sealed partial class SettingsPanelViewModel : ObservableObject
{
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
    private bool _mirroring;

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
        IUsageHistoryStore history)
    {
        ArgumentNullException.ThrowIfNull(settings);
#if !STORE
        ArgumentNullException.ThrowIfNull(routes);
#endif

        _updates = updates;
        _updates.Changed += (_, _) => DescribeUpdates();

        CheckForUpdates = settings.CheckForUpdates;
        DescribeUpdates();

        _history = history;
        DescribeHistory();

        // A recording that has just started is history where there was none.
        recorder.Recorded += (_, _) =>
        {
            if (!HasHistory)
            {
                DescribeHistory();
            }
        };
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(startup);

        _store = store;
        _settings = settings;
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
        IsRecording = recorder.IsRecording;
        recorder.RecordingChanged += OnRecordingChanged;
        StartWithWindows = startup.State == StartupState.On;
        StartupNote = startup.Detail;
        Refresh();
    }

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

    /// <summary>Whether there is any history to delete.</summary>
    public bool HasHistory => _history.Extent().HasData;

    [RelayCommand(CanExecute = nameof(HasHistory))]
    private void EraseHistory()
    {
        UsageExtent extent = _history.Extent();

        if (extent is not { First: { } first, Last: { } last })
        {
            return;
        }

        UsageSummary summary = _history.Summarise(first, last.AddMinutes(1));

        if (!EraseHistoryDialog.Ask(extent, summary, Application.Current?.MainWindow))
        {
            return;
        }

        _history.RemoveEverything();
        DescribeHistory();
    }

    private void DescribeHistory()
    {
        UsageExtent extent = _history.Extent();

        HistorySize = extent is { First: { } first, Last: { } last }
            ? string.Format(
                CultureInfo.CurrentCulture,
                "Recorded from {0:d MMMM yyyy} to {1:d MMMM yyyy}.",
                first.LocalDateTime,
                last.LocalDateTime)
            : "Nothing is recorded yet.";

        OnPropertyChanged(nameof(HasHistory));
        EraseHistoryCommand.NotifyCanExecuteChanged();
    }

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
