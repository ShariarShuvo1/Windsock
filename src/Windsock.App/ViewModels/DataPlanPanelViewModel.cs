using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Windsock.Core.Connectivity;
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Plans;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>How the person marked a network: let Windows decide, or say so.</summary>
public enum HotspotMarking
{
    Automatic,
    Hotspot,
    NotHotspot,
}

/// <summary>A network history knows, with the person's name for it and their hotspot marking.</summary>
public sealed partial class NetworkChoiceRow : ObservableObject
{
    private readonly Action<NetworkChoiceRow> _changed;
    private readonly bool _loaded;

    public NetworkChoiceRow(NetworkRecord network, NetworkChoice? choice, bool isCurrent, string verdict, Action<NetworkChoiceRow> changed)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(changed);

        Key = network.Key;
        WindowsName = network.Name;
        IsCurrent = isCurrent;
        _changed = changed;
        Label = choice?.Label ?? string.Empty;
        Marking = choice?.Hotspot switch
        {
            true => HotspotMarking.Hotspot,
            false => HotspotMarking.NotHotspot,
            _ => HotspotMarking.Automatic,
        };

        string kind = network.Kind switch
        {
            NetworkKind.WiFi => "Wi-Fi",
            NetworkKind.Ethernet => "Wired",
            NetworkKind.Cellular => "Mobile",
            _ => "Network",
        };

        Details = string.Join(
            " · ",
            new[]
            {
                kind,
                network.Metered == true ? "metered" : string.Empty,
                UsageText.Bytes(network.BytesTotal) + " recorded",
                network.LastMinute is { } last ? "last seen " + UsageText.Date(DateOnly.FromDateTime(EpochMinute.ToLocal(last).DateTime)) : string.Empty,
                isCurrent ? "in use now" : string.Empty,
                verdict,
            }.Where(part => part.Length > 0));

        _loaded = true;
    }

    public string Key { get; }

    /// <summary>What Windows calls it.</summary>
    public string WindowsName { get; }

    /// <summary>Whether the PC is on it now.</summary>
    public bool IsCurrent { get; }

    /// <summary>Its kind, how much was recorded on it, and whether it counts as a hotspot.</summary>
    public string Details { get; }

    /// <summary>The person's own name for it; empty for Windows'.</summary>
    [ObservableProperty]
    public partial string Label { get; set; }

    [ObservableProperty]
    public partial HotspotMarking Marking { get; set; }

    /// <summary>The name to show.</summary>
    public string Name => string.IsNullOrWhiteSpace(Label) ? WindowsName : Label.Trim();

    /// <summary>What a screen reader says for the network's place in the list: its tile's two lines.</summary>
    public override string ToString() => string.Concat(WindowsName, ", ", Details);

    partial void OnLabelChanged(string value)
    {
        OnPropertyChanged(nameof(Name));

        if (_loaded)
        {
            _changed(this);
        }
    }

    partial void OnMarkingChanged(HotspotMarking value)
    {
        if (_loaded)
        {
            _changed(this);
        }
    }
}

/// <summary>
/// Backs Usage › Data plan: each plan's cycle - what is used and left, where it
/// is heading, each day, the split by network and earlier cycles - the plan
/// editor, and the networks with their names and hotspot markings.
/// </summary>
/// <remarks>
/// Figures come from <see cref="PlanMonitor"/>, which works them out on its own
/// thread after every minute; the page only follows its <c>Changed</c> while it
/// is on screen. Plans and markings are written by replacing the settings'
/// lists, never by changing them in place, since the monitor reads them from
/// its own thread.
/// </remarks>
public sealed partial class DataPlanPanelViewModel : ObservableObject, IDisposable
{
    private readonly PlanMonitor _monitor;
    private readonly PlanCalculator _calculator;
    private readonly CurrentNetwork _network;
    private readonly IUsageAggregates _aggregates;
    private readonly IHistoryFile _file;
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<DataPlanPanelViewModel> _logger;
    private readonly DispatcherTimer _saveLater;
    private bool _shown;
    private int _earlierLoads;
    private IReadOnlyDictionary<long, NetworkTag> _tags = new Dictionary<long, NetworkTag>();
    private int _refreshes;
    private string? _chosenId;

    public DataPlanPanelViewModel(
        PlanMonitor monitor,
        PlanCalculator calculator,
        CurrentNetwork network,
        IUsageAggregates aggregates,
        IHistoryFile file,
        WindsockSettings settings,
        ISettingsStore store,
        Dispatcher dispatcher,
        ILogger<DataPlanPanelViewModel> logger)
    {
        _monitor = monitor;
        _calculator = calculator;
        _network = network;
        _aggregates = aggregates;
        _file = file;
        _settings = settings;
        _store = store;
        _dispatcher = dispatcher;
        _logger = logger;

        // A name typed into a network's row is saved once typing pauses, not at every key.
        _saveLater = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) => SaveChoices(), dispatcher);
        _saveLater.Stop();

        Plans = [];
        Earlier = [];
        NetworkRows = [];
        Markings =
        [
            new(HotspotMarking.Automatic, "Let Windows decide"),
            new(HotspotMarking.Hotspot, "A phone's hotspot"),
            new(HotspotMarking.NotHotspot, "Not a hotspot"),
        ];
    }

    /// <summary>Every plan's cycle, in the order the plans are listed.</summary>
    public ObservableCollection<PlanCardViewModel> Plans { get; }

    /// <summary>The plan the page leads with: the one chosen, else the one for the network in use.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelected))]
    public partial PlanCardViewModel? Selected { get; private set; }

    public bool HasSelected => Selected is not null;

    /// <summary>The chosen plan's earlier cycles, newest first.</summary>
    public ObservableCollection<EarlierCycleRow> Earlier { get; }

    [ObservableProperty]
    public partial bool HasEarlier { get; private set; }

    /// <summary>The networks history knows, the one in use first.</summary>
    public ObservableCollection<NetworkChoiceRow> NetworkRows { get; }

    public IReadOnlyList<EditorOption<HotspotMarking>> Markings { get; }

    /// <summary>Whether there are no plans at all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPlan), nameof(ShowsEmpty))]
    public partial bool IsEmpty { get; private set; } = true;

    /// <summary>Whether a plan is on show: there is one, and the editor is not open.</summary>
    public bool ShowsPlan => !IsEmpty && !IsEditing;

    /// <summary>Whether the page says there is no plan yet: there is none, and the editor is not open.</summary>
    public bool ShowsEmpty => IsEmpty && !IsEditing;

    /// <summary>Whether there is more than one plan, so the list of them is worth showing.</summary>
    [ObservableProperty]
    public partial bool HasManyPlans { get; private set; }

    /// <summary>Whether history knows any network to name or mark.</summary>
    [ObservableProperty]
    public partial bool HasNetworkRows { get; private set; }

    /// <summary>The network in use and its plan, for the heading.</summary>
    [ObservableProperty]
    public partial string Subtitle { get; private set; } = string.Empty;

    /// <summary>Said while the network in use is a phone's hotspot.</summary>
    [ObservableProperty]
    public partial string HotspotNotice { get; private set; } = string.Empty;

    /// <summary>Said while the history file belongs to a newer Windsock.</summary>
    [ObservableProperty]
    public partial string FileNotice { get; private set; } = string.Empty;

    /// <summary>What the last action did, or why it could not.</summary>
    [ObservableProperty]
    public partial string Notice { get; private set; } = string.Empty;

    /// <summary>The plan being written, while the editor is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(ShowsPlan), nameof(ShowsEmpty))]
    public partial PlanEditorViewModel? Editor { get; private set; }

    public bool IsEditing => Editor is not null;

    /// <summary>The plan waiting for "Delete" to be confirmed, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingDelete))]
    public partial PlanCardViewModel? Deleting { get; private set; }

    public bool IsConfirmingDelete => Deleting is not null;

    /// <summary>Starts following the plans, as the page comes on screen.</summary>
    public void Show()
    {
        if (_shown)
        {
            return;
        }

        _shown = true;
        _monitor.Changed += OnMonitorChanged;
        _network.Changed += OnMonitorChanged;
        _monitor.Refresh();
        Refresh();
        _ = LoadNetworksAsync();
    }

    /// <summary>Stops following while the page is out of sight.</summary>
    public void Hide()
    {
        if (!_shown)
        {
            return;
        }

        _shown = false;
        _monitor.Changed -= OnMonitorChanged;
        _network.Changed -= OnMonitorChanged;
    }

    public void Dispose()
    {
        Hide();

        // A name typed just before the window closed is still kept.
        if (_saveLater.IsEnabled)
        {
            SaveChoices();
        }

        _saveLater.Stop();
    }

    private void OnMonitorChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));

    /// <summary>Takes the monitor's latest figures onto the page.</summary>
    private void Refresh() => _ = RefreshAsync();

    /// <summary>
    /// Reads the networks' names off the interface thread - the file can be
    /// held for seconds by a backup or a VACUUM - then shows the figures.
    /// </summary>
    private async Task RefreshAsync()
    {
        int token = ++_refreshes;
        IReadOnlyDictionary<long, NetworkTag> tags;

        try
        {
            tags = await Task.Run(_aggregates.ReadNetworkTags).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            // The plans' figures stand; only the networks' names go without.
            tags = new Dictionary<long, NetworkTag>();
        }

        if (token != _refreshes)
        {
            return;
        }

        _tags = tags;
        IReadOnlyList<PlanStatus> statuses = _monitor.Statuses;
        PlanStatus? active = _monitor.Active;

        Plans.Clear();

        foreach (PlanStatus status in statuses)
        {
            Plans.Add(new PlanCardViewModel(status, ReferenceEquals(status.Plan, active?.Plan), NameOfRow, NameOfKey));
        }

        IsEmpty = _settings.Usage.Plans.Count == 0;
        HasManyPlans = Plans.Count > 1;

        PlanCardViewModel? chosen = Plans.FirstOrDefault(card => card.Id == _chosenId)
            ?? Plans.FirstOrDefault(card => card.IsActive)
            ?? Plans.FirstOrDefault();

        bool changedPlan = chosen?.Id != Selected?.Id;
        Selected = chosen;

        foreach (PlanCardViewModel card in Plans)
        {
            card.IsChosen = ReferenceEquals(card, chosen);
        }

        if (changedPlan || Earlier.Count == 0)
        {
            _ = LoadEarlierAsync();
        }

        Subtitle = SubtitleFor(active);
        HotspotNotice = HotspotText(active);
        FileNotice = _file.State switch
        {
            HistoryFileState.NewerVersion => string.Format(CultureInfo.CurrentCulture, "The history file was written by a newer version of Windsock (format {0}), so this one only reads it: plans show what was recorded before, and nothing new is added until that version runs again or this one is updated.", _file.FileSchema),
            HistoryFileState.Unavailable when _file.FileSchema > 0 => string.Format(CultureInfo.CurrentCulture, "The history file was written by a newer version of Windsock (format {0}) that this one cannot read, so plans have nothing to count. The file is left as it is.", _file.FileSchema),
            _ => string.Empty,
        };
    }

    private string? NameOfRow(long id) => _tags.TryGetValue(id, out NetworkTag? tag) ? _network.NameOf(tag) : null;

    private string NameOfKey(string key)
    {
        foreach (NetworkTag tag in _tags.Values)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal))
            {
                return _network.NameOf(tag);
            }
        }

        string? label = _settings.Usage.ChoiceFor(key)?.Label;
        return string.IsNullOrWhiteSpace(label) ? string.Empty : label.Trim();
    }

    private string SubtitleFor(PlanStatus? active)
    {
        string where = _network.Current is { } current
            ? "On " + _network.NameOf(current)
            : "Not on a network";

        return active is null
            ? (_settings.Usage.Plans.Count == 0 ? where : where + " · no plan covers it")
            : string.Concat(where, " · ", string.IsNullOrWhiteSpace(active.Plan.Name) ? "Data plan" : active.Plan.Name.Trim());
    }

    private string HotspotText(PlanStatus? active)
    {
        HotspotVerdict verdict = _network.Hotspot;

        if (!verdict.IsHotspot || _network.Current is not { } current)
        {
            return string.Empty;
        }

        string why = verdict.Reason switch
        {
            HotspotReason.MarkedByYou => "you marked it as one",
            HotspotReason.Mobile => "it is a mobile broadband link",
            _ => "Windows treats it as metered, as a phone sharing its connection says it is",
        };

        string plan = active is null
            ? "No plan covers it yet; add one to keep count of the phone's allowance."
            : string.Format(CultureInfo.CurrentCulture, "Its plan comes first: {0}.", PlanWords.Left(active) ?? active.Plan.Name);

        return string.Format(CultureInfo.CurrentCulture, "You are on {0}, a phone's hotspot - {1}. {2}", _network.NameOf(current), why, plan);
    }

    [RelayCommand]
    private void Choose(PlanCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        _chosenId = card.Id;
        Selected = card;

        foreach (PlanCardViewModel each in Plans)
        {
            each.IsChosen = ReferenceEquals(each, card);
        }

        _ = LoadEarlierAsync();
    }

    private async Task LoadEarlierAsync()
    {
        int token = ++_earlierLoads;
        Earlier.Clear();
        HasEarlier = false;

        if (Selected is not { } card || card.Status.Plan.Cycle == PlanCycle.OneOff)
        {
            return;
        }

        DataPlan plan = card.Status.Plan;
        DateTimeOffset now = DateTimeOffset.Now;
        IReadOnlyList<PlanStatus> earlier;

        try
        {
            earlier = await Task.Run(() => _calculator.Earlier(plan, now, 6)).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogEarlierFailed(ex);
            return;
        }

        if (token != _earlierLoads)
        {
            return;
        }

        foreach (PlanStatus status in earlier)
        {
            string period = string.Format(
                CultureInfo.CurrentCulture,
                "{0} to {1}",
                UsageText.FullDate(DateOnly.FromDateTime(status.Period.Start.LocalDateTime)),
                UsageText.FullDate(DateOnly.FromDateTime(status.Period.End.AddMinutes(-1).LocalDateTime)));

            string verdict = !status.HasLimit
                ? string.Empty
                : status.Over > 0
                    ? "Over by " + UsageText.Bytes(status.Over)
                    : UsageText.Bytes(status.Left ?? 0) + " to spare";

            Earlier.Add(new EarlierCycleRow(period, UsageText.Bytes(status.Used), verdict, status.Over > 0));
        }

        HasEarlier = Earlier.Count > 0;
    }

    private async Task LoadNetworksAsync()
    {
        IReadOnlyList<NetworkRecord> networks;

        try
        {
            networks = await Task.Run(_aggregates.ReadNetworks).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogEarlierFailed(ex);
            return;
        }

        string? currentKey = _network.Current?.Key;
        NetworkRows.Clear();

        foreach (NetworkRecord network in networks
            .OrderByDescending(each => string.Equals(each.Key, currentKey, StringComparison.Ordinal))
            .ThenByDescending(each => each.LastMinute ?? 0))
        {
            bool current = string.Equals(network.Key, currentKey, StringComparison.Ordinal);
            NetworkChoice? choice = _settings.Usage.ChoiceFor(network.Key);
            HotspotVerdict verdict = HotspotJudge.Judge(
                new NetworkSighting(network.Tag, network.Metered == true ? NetworkCost.Fixed : NetworkCost.Unrestricted),
                choice?.Hotspot);

            NetworkRows.Add(new NetworkChoiceRow(network, choice, current, verdict.IsHotspot ? "counts as a hotspot" : string.Empty, OnChoiceChanged));
        }

        HasNetworkRows = NetworkRows.Count > 0;
    }

    private void OnChoiceChanged(NetworkChoiceRow row) => _saveLater.Start();

    /// <summary>Writes the networks' names and markings, replacing the list.</summary>
    private void SaveChoices()
    {
        _saveLater.Stop();

        Dictionary<string, NetworkChoice> choices = new(StringComparer.Ordinal);

        foreach (NetworkChoice kept in _settings.Usage.Networks)
        {
            choices[kept.Key] = kept;
        }

        foreach (NetworkChoiceRow row in NetworkRows)
        {
            string? label = string.IsNullOrWhiteSpace(row.Label) ? null : row.Label.Trim();
            bool? hotspot = row.Marking switch
            {
                HotspotMarking.Hotspot => true,
                HotspotMarking.NotHotspot => false,
                _ => null,
            };

            if (label is null && hotspot is null)
            {
                choices.Remove(row.Key);
            }
            else
            {
                choices[row.Key] = new NetworkChoice { Key = row.Key, Label = label, Hotspot = hotspot };
            }
        }

        _settings.Usage.Networks = [.. choices.Values];
        _store.Save(_settings);
        _network.Refresh();
        _monitor.Refresh();
    }

    [RelayCommand]
    private void AddPlan()
    {
        Deleting = null;
        Notice = string.Empty;

        DataPlan plan = new()
        {
            Name = _settings.Usage.Plans.Count == 0 ? "Data plan" : "Data plan " + (_settings.Usage.Plans.Count + 1).ToString(CultureInfo.CurrentCulture),
            Since = DateTimeOffset.Now,
        };

        Editor = new PlanEditorViewModel(plan, isNew: true, NetworkOptions(), _network.Sighting);
    }

    [RelayCommand]
    private void EditPlan(PlanCardViewModel? card)
    {
        DataPlan? plan = card is null ? null : _settings.Usage.Plans.Find(each => each.Id == card.Id);

        if (plan is null)
        {
            return;
        }

        Deleting = null;
        Notice = string.Empty;
        Editor = new PlanEditorViewModel(plan, isNew: false, NetworkOptions(), _network.Sighting);
    }

    [RelayCommand]
    private void UseWindowsLimit() => Editor?.UseWindowsLimit();

    [RelayCommand]
    private void SavePlan()
    {
        if (Editor is not { CanSave: true } editor)
        {
            return;
        }

        DataPlan plan = editor.Build();
        List<DataPlan> plans = [.. _settings.Usage.Plans];
        int at = plans.FindIndex(each => each.Id == plan.Id);

        if (at >= 0)
        {
            plans[at] = plan;
        }
        else
        {
            plans.Add(plan);
        }

        _settings.Usage.Plans = plans;
        _store.Save(_settings);
        _chosenId = plan.Id;
        Editor = null;
        Notice = string.Format(CultureInfo.CurrentCulture, "Saved {0}.", plan.Name);
        _monitor.Refresh();
    }

    [RelayCommand]
    private void CancelEdit() => Editor = null;

    [RelayCommand]
    private void AskDelete(PlanCardViewModel? card) => Deleting = card;

    [RelayCommand]
    private void KeepPlan() => Deleting = null;

    [RelayCommand]
    private void DeletePlan()
    {
        if (Deleting is not { } card)
        {
            return;
        }

        _settings.Usage.Plans = [.. _settings.Usage.Plans.Where(each => each.Id != card.Id)];
        _store.Save(_settings);
        Deleting = null;
        Notice = string.Format(CultureInfo.CurrentCulture, "Deleted {0}. History is untouched.", card.Name);

        if (_chosenId == card.Id)
        {
            _chosenId = null;
        }

        _monitor.Refresh();
    }

    private List<PlanNetworkOption> NetworkOptions()
    {
        string? currentKey = _network.Current?.Key;
        List<PlanNetworkOption> options = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        if (_network.Current is { } current)
        {
            options.Add(new PlanNetworkOption(current.Key, _network.NameOf(current), "In use now"));
            seen.Add(current.Key);
        }

        foreach (NetworkChoiceRow row in NetworkRows)
        {
            if (seen.Add(row.Key))
            {
                options.Add(new PlanNetworkOption(row.Key, row.Name, row.Details));
            }
        }

        // Networks a plan already names stay offered even after their history was erased.
        foreach (PlanNetwork named in _settings.Usage.Plans.SelectMany(plan => plan.Networks))
        {
            if (seen.Add(named.Key))
            {
                options.Add(new PlanNetworkOption(named.Key, named.Name, "No history left"));
            }
        }

        return options;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read history for the Data plan page")]
    private partial void LogEarlierFailed(Exception exception);
}
