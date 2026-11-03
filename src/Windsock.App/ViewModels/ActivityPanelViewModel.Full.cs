#if !STORE
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Windsock.App.Services;
using Windsock.Core.Control;
using Windsock.Core.Formatting;
using Windsock.Core.Processes;
using Windsock.Core.Settings;
using Windsock.Core.Timeline;

namespace Windsock.App.ViewModels;

/// <summary>A choice of how far back the timeline list reaches.</summary>
public enum TimelinePeriod
{
    LastHour,
    Today,
    LastSevenDays,
    Everything,
}

/// <summary>One entry in the timeline's period picker.</summary>
public sealed record TimelinePeriodOption(TimelinePeriod Period, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One entry in the timeline's retention picker.</summary>
public sealed record KeptDaysOption(int Days, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One entry in the places' day picker.</summary>
public sealed record PlaceDayOption(DateOnly Day, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One conversation of the timeline: an app, a far end, when, and how much.</summary>
public sealed class TimelineRowViewModel
{
    public TimelineRowViewModel(TimelineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entry = entry;
        App = string.IsNullOrWhiteSpace(entry.AppName) ? AppNameOf(entry.AttributionId) : entry.AppName;

        DateTimeOffset first = entry.First.ToLocalTime();
        DateTimeOffset last = entry.Last.ToLocalTime();
        ClockPatterns clock = ClockFormat.Current;

        When = first.Date == DateTimeOffset.Now.Date
            ? last - first < TimeSpan.FromMinutes(1) ? clock.Time(first) : string.Concat(clock.Time(first), "–", clock.Time(last))
            : last - first < TimeSpan.FromMinutes(1) ? clock.Day(first) : string.Concat(clock.Day(first), "–", clock.Time(last));

        string port = entry.Port > 0 ? string.Concat(":", entry.Port.ToString(CultureInfo.InvariantCulture)) : string.Empty;

        Where = entry.Address is null
            ? "Other places, folded together"
            : entry.Host is { Length: > 0 } host
                ? string.Concat(host, " · ", entry.Address, port)
                : string.Concat(entry.Address, port);

        List<string> facts = [];

        if (entry.IsLocal)
        {
            facts.Add("Local network");
        }

        if (entry.Country is { Length: > 0 } country)
        {
            facts.Add(ActivityPanelViewModel.CountryName(country));
        }

        if (entry.Organisation is { Length: > 0 } organisation)
        {
            facts.Add(organisation);
        }

        facts.Add(string.Concat("↓ ", SizeFormatter.Format(entry.Received), "  ↑ ", SizeFormatter.Format(entry.Sent)));
        Facts = string.Join(" · ", facts);
    }

    public TimelineEntry Entry { get; }

    public string App { get; }

    /// <summary>When it began and last moved anything.</summary>
    public string When { get; }

    /// <summary>The far end: its name where known, its address and port.</summary>
    public string Where { get; }

    /// <summary>Where it is, who runs it, and how much moved.</summary>
    public string Facts { get; }

    public string Spoken => string.Concat(App, " to ", Where, ", ", When, ", ", Facts);

    /// <summary>What a screen reader announces for the item.</summary>
    public override string ToString() => Spoken;

    private static string AppNameOf(string attributionId)
    {
        int slash = attributionId.LastIndexOf('\\');
        return slash >= 0 ? attributionId[(slash + 1)..] : attributionId;
    }
}

/// <summary>One of Windsock's firewall rules, as a tile.</summary>
public sealed class FirewallRuleRowViewModel(FirewallRule rule)
{
    public FirewallRule Rule { get; } = rule;

    public string Name { get; } = rule.Name;

    /// <summary>What it blocks, and whether it is on.</summary>
    public string Detail { get; } = string.Concat(
        rule.ServiceName is { } service ? string.Concat("The ", service, " service") : rule.ApplicationPath ?? "—",
        rule.Enabled ? " · outbound, every network" : " · switched off in Windows");

    public string Spoken => string.Concat(Name, ", ", Detail);

    /// <summary>What a screen reader announces for the item.</summary>
    public override string ToString() => Spoken;
}

public sealed partial class ActivityPanelViewModel
{
    private const int TimelinePage = 50;

    private ITimelineStore? _timeline;
    private DnsLookupLog? _lookupLog;
    private InsightActions? _actions;
    private IProcessTrafficSource? _trace;
    private WindsockSettings? _settings;
    private ISettingsStore? _store;
    private int _timelinePage = 1;
    private int _readingFull;
    private bool _loading;

    // Set when the timeline or the rules may have changed, and taken by the
    // next read: the timeline's queries (a LIKE over every conversation) and
    // the firewall's 600-rule walk run only then, not every five seconds.
    private int _timelineStale = 1;
    private int _rulesStale = 1;

    /// <summary>Whether the kernel trace is attributing packets now.</summary>
    [ObservableProperty]
    public partial bool IsTracing { get; private set; }

    /// <summary>What the parts that need the trace say when it is not running.</summary>
    [ObservableProperty]
    public partial string TraceNote { get; private set; } = string.Empty;

    // ---------------------------------------------------------------- timeline

    /// <summary>Whether the timeline is kept; the switch on the page.</summary>
    [ObservableProperty]
    public partial bool KeepTimeline { get; set; }

    /// <summary>How long the timeline keeps what it records.</summary>
    public IReadOnlyList<KeptDaysOption> KeptChoices { get; } =
    [
        .. InsightSettings.TimelineChoices.Select(days => new KeptDaysOption(days, days == 1 ? "1 day" : string.Create(CultureInfo.CurrentCulture, $"{days} days"))),
    ];

    /// <summary>The retention chosen.</summary>
    [ObservableProperty]
    public partial int KeptDays { get; set; } = 30;

    /// <summary>The periods the list can show.</summary>
    public IReadOnlyList<TimelinePeriodOption> TimelinePeriods { get; } =
    [
        new(TimelinePeriod.LastHour, "Last hour"),
        new(TimelinePeriod.Today, "Today"),
        new(TimelinePeriod.LastSevenDays, "Last 7 days"),
        new(TimelinePeriod.Everything, "Everything kept"),
    ];

    /// <summary>The period shown.</summary>
    [ObservableProperty]
    public partial TimelinePeriod SelectedTimelinePeriod { get; set; } = TimelinePeriod.Today;

    /// <summary>Words to find in the app, the address, the name, the country or the network's owner.</summary>
    [ObservableProperty]
    public partial string TimelineSearch { get; set; } = string.Empty;

    /// <summary>One page of the timeline, the latest first.</summary>
    public ObservableCollection<TimelineRowViewModel> TimelineRows { get; } = [];

    /// <summary>The bar under the timeline.</summary>
    public PagerViewModel? TimelinePager { get; private set; }

    [ObservableProperty]
    public partial bool IsTimelineEmpty { get; private set; } = true;

    [ObservableProperty]
    public partial string TimelineEmptyTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string TimelineEmptyNote { get; private set; } = string.Empty;

    /// <summary>Conversations kept today, for the plate.</summary>
    [ObservableProperty]
    public partial string TimelineTodayText { get; private set; } = "—";

    // ------------------------------------------------------------------ places

    /// <summary>The days the places can show: today, and the six before it.</summary>
    public IReadOnlyList<PlaceDayOption> PlaceDays { get; private set; } = [];

    /// <summary>The day shown.</summary>
    [ObservableProperty]
    public partial DateOnly SelectedPlaceDay { get; set; }

    /// <summary>The day's countries, the busiest first.</summary>
    public ObservableCollection<PlaceRowViewModel> Countries { get; } = [];

    /// <summary>The day's sites, the busiest first.</summary>
    public ObservableCollection<PlaceRowViewModel> Sites { get; } = [];

    [ObservableProperty]
    public partial bool IsPlacesEmpty { get; private set; } = true;

    [ObservableProperty]
    public partial string PlacesEmptyNote { get; private set; } = string.Empty;

    // ----------------------------------------------------------------- lookups

    /// <summary>Words to find in an app or a name looked up.</summary>
    [ObservableProperty]
    public partial string LookupSearch { get; set; } = string.Empty;

    /// <summary>The latest names looked up, each with the app that asked.</summary>
    public ObservableCollection<LookupRowViewModel> Lookups { get; } = [];

    [ObservableProperty]
    public partial bool IsLookupsEmpty { get; private set; } = true;

    [ObservableProperty]
    public partial string LookupsEmptyTitle { get; private set; } = string.Empty;

    // ------------------------------------------------------------------- rules

    /// <summary>Every firewall rule Windsock made.</summary>
    public ObservableCollection<FirewallRuleRowViewModel> Rules { get; } = [];

    [ObservableProperty]
    public partial bool HasRules { get; private set; }

    /// <summary>Whether the firewall was read and holds none of Windsock's rules.</summary>
    [ObservableProperty]
    public partial bool IsRulesEmpty { get; private set; }

    /// <summary>Whether the rules would take effect, or why not; empty when they would.</summary>
    [ObservableProperty]
    public partial string RulesNote { get; private set; } = string.Empty;

    /// <summary>How many rules, for the plate.</summary>
    [ObservableProperty]
    public partial string RulesText { get; private set; } = "—";

    partial void BuildFull(IServiceProvider provider)
    {
        _timeline = provider.GetService<ITimelineStore>();
        _lookupLog = provider.GetService<DnsLookupLog>();
        _actions = provider.GetService<InsightActions>();
        _trace = provider.GetService<IProcessTrafficSource>();
        _settings = provider.GetService<WindsockSettings>();
        _store = provider.GetService<ISettingsStore>();

        _loading = true;
        KeepTimeline = _settings?.Insight.Timeline ?? false;
        KeptDays = _settings?.Insight.KeptDays ?? 30;
        _loading = false;

        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        PlaceDays = [.. Enumerable.Range(0, 7).Select(back => new PlaceDayOption(
            today.AddDays(-back),
            back switch
            {
                0 => "Today",
                1 => "Yesterday",
                _ => today.AddDays(-back).ToString("ddd d MMM", ClockFormat.Current.Format),
            }))];
        SelectedPlaceDay = today;

        TimelinePager = new PagerViewModel(
            TimelinePage,
            page =>
            {
                _timelinePage = page;
                Requery();
            },
            _ => { },
            "conversation",
            "conversations");

        if (_actions is not null)
        {
            _actions.FirewallChanged += OnFirewallChanged;
        }

        if (_timeline is not null)
        {
            _timeline.Changed += OnTimelineChanged;
        }
    }

    partial void ShowFull() => ReadTrace();

    // Whatever happened while the page was out of sight is read on its return.
    partial void HideFull()
    {
        Interlocked.Exchange(ref _timelineStale, 1);
        Interlocked.Exchange(ref _rulesStale, 1);
    }

    partial void DisposeFull()
    {
        if (_actions is not null)
        {
            _actions.FirewallChanged -= OnFirewallChanged;
        }

        if (_timeline is not null)
        {
            _timeline.Changed -= OnTimelineChanged;
        }
    }

    private void OnFirewallChanged(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _rulesStale, 1);
        _dispatcher.BeginInvoke(ReadFull);
    }

    // Raised on the recorder's thread; the next tick reads.
    private void OnTimelineChanged(object? sender, EventArgs e) => Interlocked.Exchange(ref _timelineStale, 1);

    /// <summary>Marks the timeline to be read again and reads, after an input changed.</summary>
    private void Requery()
    {
        Interlocked.Exchange(ref _timelineStale, 1);
        ReadFull();
    }

    partial void OnKeepTimelineChanged(bool value)
    {
        if (_loading || _settings is null || _settings.Insight.Timeline == value)
        {
            return;
        }

        _settings.Insight.Timeline = value;
        _store?.Save(_settings);
        Requery();

        if (!value)
        {
            // After the switch has moved, not while it is moving.
            _dispatcher.BeginInvoke(OfferToForget);
        }
    }

    /// <summary>
    /// Once the timeline is switched off, offers to delete what it kept rather
    /// than leave it to age out over the days chosen.
    /// </summary>
    private void OfferToForget()
    {
        if (_timeline is null || KeepTimeline)
        {
            return;
        }

        int kept;

        try
        {
            kept = _timeline.Count(new TimelineQuery(DateTimeOffset.UnixEpoch, DateTimeOffset.Now.AddDays(1)));
        }
        catch (Exception ex) when (Core.History.HistoryFailure.IsStorage(ex))
        {
            return;
        }

        if (kept == 0)
        {
            return;
        }

        System.Windows.Window? owner = System.Windows.Application.Current?.MainWindow;

        if (!Views.InsightDialog.Ask(
            owner,
            "Connection timeline",
            "Delete the timeline kept so far?",
            string.Format(
                CultureInfo.CurrentCulture,
                "Windsock has stopped keeping it. Left alone, the {0:N0} conversations already kept and their places age out over the next {1} days. Each service host's daily totals, which name no place, are kept either way.",
                kept,
                KeptDays),
            [],
            "Delete them",
            "Keep them"))
        {
            return;
        }

        try
        {
            _timeline.ForgetConversations();
        }
        catch (Exception ex) when (Core.History.HistoryFailure.IsStorage(ex))
        {
            Views.InsightDialog.Tell(owner, "Connection timeline", "Not deleted", "The timeline's file could not be changed: " + ex.Message, [], warning: true);
        }

        Requery();
    }

    partial void OnKeptDaysChanged(int value)
    {
        if (_loading || _settings is null || _settings.Insight.TimelineDays == value)
        {
            return;
        }

        _settings.Insight.TimelineDays = value;
        _store?.Save(_settings);
    }

    partial void OnSelectedTimelinePeriodChanged(TimelinePeriod value)
    {
        _timelinePage = 1;
        Requery();
    }

    partial void OnTimelineSearchChanged(string value)
    {
        _timelinePage = 1;
        Requery();
    }

    partial void OnSelectedPlaceDayChanged(DateOnly value) => Requery();

    partial void OnLookupSearchChanged(string value) => ReadFull();

    private void ReadTrace()
    {
        IsTracing = _trace?.State == TrafficSourceState.Running;
        TraceNote = IsTracing
            ? string.Empty
            : IsElevated
                ? "The kernel trace is not running, so nothing here is being recorded."
                : "These come from the kernel trace and the DNS client's events, which Windows opens only for an administrator. Restart Windsock as administrator to see them.";
    }

    /// <summary>Reads the timeline, the places, the lookups and the rules in the background.</summary>
    partial void ReadFull()
    {
        if (!_visible || Interlocked.CompareExchange(ref _readingFull, 1, 0) != 0)
        {
            return;
        }

        ReadTrace();

        bool readTimeline = Interlocked.Exchange(ref _timelineStale, 0) == 1;
        bool readRules = Interlocked.Exchange(ref _rulesStale, 0) == 1;
        DateTimeOffset now = DateTimeOffset.Now;
        TimelineQuery query = new(
            SelectedTimelinePeriod switch
            {
                TimelinePeriod.LastHour => now.AddHours(-1),
                TimelinePeriod.Today => new DateTimeOffset(now.Date, now.Offset),
                TimelinePeriod.LastSevenDays => now.AddDays(-7),
                _ => DateTimeOffset.UnixEpoch,
            },
            now.AddMinutes(1),
            string.IsNullOrWhiteSpace(TimelineSearch) ? null : TimelineSearch,
            Offset: (_timelinePage - 1) * TimelinePage,
            Limit: TimelinePage);
        DateOnly day = SelectedPlaceDay;
        string lookupSearch = LookupSearch;

        _ = Task.Run(() =>
        {
            try
            {
                TimelineRead? timeline = null;
                RulesRead? rules = null;

                if (readTimeline)
                {
                    IReadOnlyList<TimelineEntry> entries = [];
                    int total = 0;
                    int today = 0;
                    IReadOnlyList<PlaceDay> places = [];

                    if (_timeline is not null)
                    {
                        try
                        {
                            total = _timeline.Count(query);
                            entries = _timeline.Search(query);
                            today = _timeline.Count(new TimelineQuery(new DateTimeOffset(now.Date, now.Offset), now.AddMinutes(1)));
                            places = _timeline.ReadPlaces(day, day);
                        }
                        catch (Exception ex) when (Core.History.HistoryFailure.IsStorage(ex))
                        {
                        }
                    }

                    timeline = new TimelineRead(entries, total, today, places);
                }

                if (readRules)
                {
                    bool readable = FirewallBlocks.TryList(out IReadOnlyList<FirewallRule> list);
                    rules = new RulesRead(list, readable, readable && list.Count > 0 ? FirewallBlocks.Standing() : null);
                }

                List<LookupRowViewModel> lookups = ReadLookups(lookupSearch);

                _dispatcher.BeginInvoke(() => PresentFull(timeline, lookups, rules));
            }
            finally
            {
                Interlocked.Exchange(ref _readingFull, 0);
            }
        });
    }

    private sealed record TimelineRead(IReadOnlyList<TimelineEntry> Entries, int Total, int Today, IReadOnlyList<PlaceDay> Places);

    private sealed record RulesRead(IReadOnlyList<FirewallRule> Rules, bool Readable, FirewallStanding? Standing);

    private List<LookupRowViewModel> ReadLookups(string search)
    {
        if (_lookupLog is null)
        {
            return [];
        }

        Dictionary<int, string> names = [];
        List<LookupRowViewModel> rows = [];

        foreach ((int processId, DnsLookup lookup) in _lookupLog.All(400))
        {
            if (!names.TryGetValue(processId, out string? app))
            {
                ProcessIdentity identity = _identities.Resolve(processId);
                app = string.IsNullOrWhiteSpace(identity.Description) ? identity.Name : identity.Description;
                names[processId] = app;
            }

            if (search.Length > 0
                && !app.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                && !lookup.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rows.Add(new LookupRowViewModel(
                app,
                processId,
                lookup.Name,
                lookup.TypeText,
                lookup.Count,
                lookup.Last,
                lookup.StatusText,
                string.Join(", ", lookup.Addresses.Take(4))));

            if (rows.Count >= 120)
            {
                break;
            }
        }

        return rows;
    }

    private void PresentFull(TimelineRead? timeline, List<LookupRowViewModel> lookups, RulesRead? rules)
    {
        if (timeline is not null)
        {
            PresentTimeline(timeline.Entries, timeline.Total, timeline.Today, timeline.Places);
        }

        PresentLookups(lookups);

        if (rules is not null)
        {
            PresentRules(rules);
        }
    }

    private void PresentTimeline(IReadOnlyList<TimelineEntry> entries, int total, int today, IReadOnlyList<PlaceDay> places)
    {
        TimelineRows.Clear();

        foreach (TimelineEntry entry in entries)
        {
            TimelineRows.Add(new TimelineRowViewModel(entry));
        }

        int pages = Math.Max(1, (int)Math.Ceiling(total / (double)TimelinePage));
        _timelinePage = Math.Clamp(_timelinePage, 1, pages);
        TimelinePager?.Show(new PageWindow(_timelinePage, pages, (_timelinePage - 1) * TimelinePage, entries.Count, total));

        TimelineTodayText = KeepTimeline ? today.ToString("N0", CultureInfo.CurrentCulture) : "Off";
        IsTimelineEmpty = entries.Count == 0;
        (TimelineEmptyTitle, TimelineEmptyNote) = (KeepTimeline, IsTracing) switch
        {
            (false, _) => ("The timeline is off", "Switch it on to keep a record of which app connected where and when, on this PC only. It is close to a browsing history, so it starts off."),
            (true, false) => ("Nothing is being recorded", TraceNote),
            _ => string.IsNullOrWhiteSpace(TimelineSearch)
                ? ("Nothing in this period yet", "Conversations appear here within ten seconds of an app reaching anywhere, while history recording is on.")
                : ("Nothing matches", "Try fewer words, or a longer period."),
        };

        PresentPlaces(places);
    }

    private void PresentLookups(List<LookupRowViewModel> lookups)
    {
        Lookups.Clear();

        foreach (LookupRowViewModel row in lookups)
        {
            Lookups.Add(row);
        }

        IsLookupsEmpty = lookups.Count == 0;
        LookupsEmptyTitle = !IsTracing
            ? TraceNote
            : LookupSearch.Length > 0
                ? "Nothing matches"
                : "No names looked up since Windsock started as administrator. They are kept in memory only, for two hours, and never written to disk.";
    }

    private void PresentRules(RulesRead read)
    {
        Rules.Clear();

        foreach (FirewallRule rule in read.Rules)
        {
            Rules.Add(new FirewallRuleRowViewModel(rule));
        }

        HasRules = read.Rules.Count > 0;
        IsRulesEmpty = read.Readable && read.Rules.Count == 0;
        RulesText = read.Readable ? read.Rules.Count.ToString("N0", CultureInfo.CurrentCulture) : "—";
        RulesNote = !read.Readable
            ? "Windows Firewall could not be read, so Windsock cannot say whether any of its rules are there. Look for the group Windsock in Windows Defender Firewall with Advanced Security."
            : read.Standing switch
            {
                { LocalRulesIgnored: true } => "Group Policy on this PC overrides its own firewall rules, so Windows keeps these rules but does not apply them.",
                { FirewallOn: false } => "Windows Firewall is off on this network, so these rules do nothing until it is turned back on.",
                { OutboundAllowedByDefault: false } => "This network blocks outbound traffic unless allowed, so these rules change nothing here.",
                _ => string.Empty,
            };
    }

    private void PresentPlaces(IReadOnlyList<PlaceDay> places)
    {
        Countries.Clear();
        Sites.Clear();

        var countries = places
            .GroupBy(each => each.Country)
            .Select(group => (Code: group.Key, Bytes: group.Sum(each => each.Total), Sites: group.Count()))
            .OrderByDescending(each => each.Bytes)
            .Take(10)
            .ToList();

        long busiest = countries.Count > 0 ? countries[0].Bytes : 1;

        foreach (var country in countries)
        {
            Countries.Add(new PlaceRowViewModel(
                country.Code.Length == 0 ? "Not known" : CountryName(country.Code),
                country.Sites == 1 ? "1 site" : string.Create(CultureInfo.CurrentCulture, $"{country.Sites} sites"),
                country.Bytes,
                busiest > 0 ? country.Bytes / (double)busiest : 0));
        }

        List<PlaceDay> sites = [.. places
            .GroupBy(each => each.Site, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(each => each.Total).First() with
            {
                Received = group.Sum(each => each.Received),
                Sent = group.Sum(each => each.Sent),
                Conversations = group.Sum(each => each.Conversations),
            })
            .OrderByDescending(each => each.Total)
            .Take(12)];

        long top = sites.Count > 0 ? sites[0].Total : 1;

        foreach (PlaceDay site in sites)
        {
            Sites.Add(new PlaceRowViewModel(
                site.Site,
                string.Concat(
                    site.Country.Length == 0 ? "Country not known" : CountryName(site.Country),
                    " · ",
                    site.Conversations == 1 ? "1 conversation" : string.Create(CultureInfo.CurrentCulture, $"{site.Conversations} conversations")),
                site.Total,
                top > 0 ? site.Total / (double)top : 0));
        }

        IsPlacesEmpty = places.Count == 0;
        PlacesEmptyNote = !KeepTimeline
            ? "Each day's places come from the timeline. Switch it on above to start keeping them."
            : "Nothing recorded for this day.";
    }

    [RelayCommand]
    private void RemoveRule(FirewallRuleRowViewModel? row)
    {
        if (row is not null && _actions is not null)
        {
            _actions.Unblock(null, row.Rule);
        }
    }

    [RelayCommand]
    private void RemoveAllRules() => _actions?.RemoveAllBlocks(null);

    [RelayCommand]
    private static void RestartElevated() => InsightActions.RestartElevated();
}
#endif
