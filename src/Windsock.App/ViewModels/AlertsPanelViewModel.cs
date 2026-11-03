using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Windsock.App.Services.Alerts;
using Windsock.Core.Alerts;
using Windsock.Core.Formatting;
using Windsock.Core.Notifications;
using Windsock.Core.Reports;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>One day of the week, ticked when quiet hours begin on it.</summary>
public sealed partial class DayToggle : ObservableObject
{
    private readonly Action _changed;
    private bool _loaded;

    public DayToggle(DayOfWeek day, bool isOn, Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);

        Day = day;
        _changed = changed;
        Abbreviation = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day);
        Name = CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day);
        IsOn = isOn;
        _loaded = true;
    }

    /// <summary>The day.</summary>
    public DayOfWeek Day { get; }

    /// <summary>Its short name, on the toggle.</summary>
    public string Abbreviation { get; }

    /// <summary>Its name, for a screen reader.</summary>
    public string Name { get; }

    /// <summary>Whether quiet hours begin on it.</summary>
    [ObservableProperty]
    public partial bool IsOn { get; set; }

    /// <summary>What a screen reader says for the day's place in the list: its name.</summary>
    public override string ToString() => Name;

    /// <summary>Sets the tick without counting it as a change.</summary>
    public void Set(bool isOn)
    {
        _loaded = false;
        IsOn = isOn;
        _loaded = true;
    }

    partial void OnIsOnChanged(bool value)
    {
        if (_loaded)
        {
            _changed();
        }
    }
}

/// <summary>
/// Backs the Alerts page: whether Windsock may interrupt at all, when it holds
/// back, what it interrupts for, what it has said, and where else it says it.
/// </summary>
/// <remarks>
/// <para>
/// One place for everything that can interrupt, where there used to be two
/// half-places: the notification switch in Settings and the connection alerts
/// on the Connection page. The page is drawn from <see cref="AlertCatalogue"/>,
/// so a kind of alert added there appears here with its switch.
/// </para>
/// <para>
/// A change is saved at once and handed to the alert service, which starts or
/// stops the one source it concerns: switching off every hardware alert, say,
/// lets go of the machine's quiet reading.
/// </para>
/// </remarks>
public sealed partial class AlertsPanelViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _store;
    private readonly AlertDesk _desk;
    private readonly AlertService _service;
    private readonly TimeProvider _clock;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _ticker;
    private readonly List<AlertLevelChoice> _levels = [];
    private bool _loaded;
    private bool _visible;
    private bool _disposed;

    public AlertsPanelViewModel(
        NotificationsCardViewModel notifications,
        WindsockSettings settings,
        ISettingsStore store,
        AlertDesk desk,
        AlertService service,
        HttpAlertForwarder forwarder,
        TimeProvider clock,
        Dispatcher dispatcher,
        ILogger<AlertsPanelViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(desk);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(dispatcher);

        Notifications = notifications;
        _settings = settings;
        _store = store;
        _desk = desk;
        _service = service;
        _clock = clock;
        _dispatcher = dispatcher;

        Times = [.. Enumerable.Range(0, QuietHoursSettings.Day / QuietHoursSettings.Step)
            .Select(step => step * QuietHoursSettings.Step)
            .Select(minute => new LevelOption(minute, ClockFormat.Time(DateTime.Today.AddMinutes(minute))))];

        // The week as the region starts it.
        DayOfWeek first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        Days = [.. Enumerable.Range(0, 7)
            .Select(offset => (DayOfWeek)(((int)first + offset) % 7))
            .Select(day => new DayToggle(day, settings.Alerts.Quiet.Days.Contains(day), SaveDays))];

        Groups = BuildGroups();
        Recent = new AlertListViewModel(desk, settings, store, dispatcher, logger);
        Forwarding = new ForwardingViewModel(desk, forwarder, settings, dispatcher);
        Forwarding.Saved += OnForwardingSaved;
        Recent.PropertyChanged += OnRecentChanged;

        _ticker = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Tick };
        _ticker.Tick += OnTick;
        _desk.Changed += OnDeskChanged;
        TemperatureScale.Changed += OnTemperatureScaleChanged;

        Read();
    }

    /// <summary>The switch that governs every notification, and the way to test it.</summary>
    public NotificationsCardViewModel Notifications { get; }

    /// <summary>Every kind of alert, a band to a group.</summary>
    public IReadOnlyList<AlertGroupViewModel> Groups { get; }

    /// <summary>The alerts sent.</summary>
    public AlertListViewModel Recent { get; }

    /// <summary>Where alerts are forwarded.</summary>
    public ForwardingViewModel Forwarding { get; }

    /// <summary>The times quiet hours may begin and end at, every quarter of an hour.</summary>
    public IReadOnlyList<LevelOption> Times { get; }

    /// <summary>The days quiet hours begin on, in the order the region's week runs.</summary>
    public IReadOnlyList<DayToggle> Days { get; }

    /// <summary>Whether quiet hours are on.</summary>
    [ObservableProperty]
    public partial bool QuietEnabled { get; set; }

    /// <summary>When they begin, in minutes past midnight.</summary>
    [ObservableProperty]
    public partial int QuietStart { get; set; }

    /// <summary>When they end.</summary>
    [ObservableProperty]
    public partial int QuietEnd { get; set; }

    /// <summary>Where quiet hours and the snooze stand, in a sentence.</summary>
    [ObservableProperty]
    public partial string QuietStatus { get; private set; } = string.Empty;

    /// <summary>Whether alerts are being held right now.</summary>
    [ObservableProperty]
    public partial bool IsHolding { get; private set; }

    /// <summary>Whether a snooze is running.</summary>
    [ObservableProperty]
    public partial bool IsSnoozed { get; private set; }

    /// <summary>The snooze-until-morning button's words: "Until 07:00 tomorrow".</summary>
    [ObservableProperty]
    public partial string MorningLabel { get; private set; } = string.Empty;

    /// <summary>How many alerts are new, for the page's first figure.</summary>
    [ObservableProperty]
    public partial string UnreadValue { get; private set; } = "—";

    /// <summary>How many alerts wait for quiet hours or the snooze to end.</summary>
    [ObservableProperty]
    public partial string HeldValue { get; private set; } = "0";

    /// <summary>Until when they wait, or that nothing does.</summary>
    [ObservableProperty]
    public partial string HeldNote { get; private set; } = string.Empty;

    /// <summary>Quiet hours in a figure: "22:00–07:00", or "Off".</summary>
    [ObservableProperty]
    public partial string QuietValue { get; private set; } = "Off";

    /// <summary>A word under it: snoozed, quiet now, or which days.</summary>
    [ObservableProperty]
    public partial string QuietNote { get; private set; } = string.Empty;

    /// <summary>The page comes into view.</summary>
    public void Show()
    {
        if (_disposed)
        {
            return;
        }

        _visible = true;
        Notifications.Refresh();
        Read();

        // Bytes or bits may have changed on the network chart meanwhile.
        RefreshLevels();
        Recent.Show();
        Forwarding.Show();
        _ticker.Start();
    }

    /// <summary>The page leaves the screen.</summary>
    public void Hide()
    {
        _visible = false;
        _ticker.Stop();
        Recent.Hide();
    }

    /// <summary>Asks Windows again and reads every switch again, as the page comes into view.</summary>
    public void Refresh() => Show();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Hide();
        _ticker.Tick -= OnTick;
        _desk.Changed -= OnDeskChanged;
        TemperatureScale.Changed -= OnTemperatureScaleChanged;
        Forwarding.Saved -= OnForwardingSaved;
        Recent.PropertyChanged -= OnRecentChanged;
        Recent.Dispose();
        Forwarding.Dispose();
    }

    partial void OnQuietEnabledChanged(bool value) => SaveQuiet(quiet => quiet.Enabled = value);

    partial void OnQuietStartChanged(int value) => SaveQuiet(quiet => quiet.StartMinute = value);

    partial void OnQuietEndChanged(int value) => SaveQuiet(quiet => quiet.EndMinute = value);

    [RelayCommand]
    private void SnoozeHour() => _desk.Snooze(TimeSpan.FromHours(1));

    [RelayCommand]
    private void SnoozeMorning() => _desk.SnoozeUntilMorning();

    [RelayCommand]
    private void EndSnooze() => _desk.EndSnooze();

    private void SaveDays()
    {
        DayOfWeek[] chosen = [.. Days.Where(day => day.IsOn).Select(day => day.Day)];

        // No day at all is quiet hours off in all but name; keep the last one ticked.
        if (chosen.Length == 0)
        {
            foreach (DayToggle day in Days)
            {
                day.Set(_settings.Alerts.Quiet.Days.Contains(day.Day));
            }

            return;
        }

        SaveQuiet(quiet => quiet.Days = [.. chosen]);
    }

    private void SaveQuiet(Action<QuietHoursSettings> change)
    {
        if (!_loaded)
        {
            return;
        }

        // Replaced whole: the desk reads quiet hours as alerts arrive.
        QuietHoursSettings was = _settings.Alerts.Quiet;
        QuietHoursSettings next = new()
        {
            Enabled = was.Enabled,
            StartMinute = was.StartMinute,
            EndMinute = was.EndMinute,
            Days = [.. was.Days],
        };

        change(next);
        _settings.Alerts.Quiet = next;
        _store.Save(_settings);
        _desk.Refresh();
        Describe();
    }

    /// <summary>A kind's switch, level, sound or choice changed: keep it, and start or stop what it needs.</summary>
    private void KindChanged()
    {
        _store.Save(_settings);
        _service.Apply();
        DescribeWatching();
    }

    private void Read()
    {
        _loaded = false;

        QuietHoursSettings quiet = _settings.Alerts.Quiet;
        QuietEnabled = quiet.Enabled;
        QuietStart = quiet.Start;
        QuietEnd = quiet.End;

        foreach (DayToggle day in Days)
        {
            day.Set(quiet.Days.Contains(day.Day));
        }

        bool forwarding = _settings.Alerts.Forward.Enabled;

        foreach (AlertKindViewModel kind in Groups.SelectMany(group => group.Kinds))
        {
            kind.Read();
            kind.IsForwardingOn = forwarding;
        }

        _loaded = true;

        Describe();
        DescribeWatching();
    }

    private void Describe()
    {
        DateTimeOffset now = _clock.GetLocalNow();
        TimeZoneInfo zone = _clock.LocalTimeZone;
        QuietHoursSettings quiet = _settings.Alerts.Quiet;
        DateTimeOffset? snoozed = _desk.SnoozedUntil;
        DateTimeOffset? heldUntil = _desk.HeldUntil;
        int held = _desk.HeldCount;

        IsSnoozed = snoozed is not null;
        IsHolding = heldUntil is not null;

        DateTimeOffset morning = QuietHours.Morning(quiet, now, zone);
        MorningLabel = string.Concat(
            "Until ",
            ClockFormat.Time(TimeZoneInfo.ConvertTime(morning, zone)),
            TimeZoneInfo.ConvertTime(morning, zone).Date > TimeZoneInfo.ConvertTime(now, zone).Date ? " tomorrow" : string.Empty);

        List<string> said = [];

        if (heldUntil is { } until)
        {
            said.Add(string.Concat(
                snoozed is not null ? "Snoozed" : "Quiet hours",
                ": alerts are held until ",
                When(until, now, zone),
                " and then shown together."));
        }
        else if (quiet.Enabled)
        {
            said.Add(string.Concat(
                "Alerts are held from ",
                ClockFormat.Time(DateTime.Today.AddMinutes(quiet.Start)),
                " to ",
                ClockFormat.Time(DateTime.Today.AddMinutes(quiet.End)),
                quiet.End <= quiet.Start ? " the next morning" : string.Empty,
                DaysText(quiet.Days),
                ", and shown together when the hours end."));
        }
        else
        {
            said.Add("Alerts are shown as they happen.");
        }

        if (held > 0)
        {
            said.Add(held == 1 ? "1 alert is waiting." : string.Create(CultureInfo.CurrentCulture, $"{held:N0} alerts are waiting."));
        }

        QuietStatus = string.Join(' ', said);

        HeldValue = held.ToString("N0", CultureInfo.CurrentCulture);
        HeldNote = heldUntil is { } end ? string.Concat("Until ", When(end, now, zone)) : "Shown as they happen";
        QuietValue = quiet.Enabled
            ? string.Concat(ClockFormat.Time(DateTime.Today.AddMinutes(quiet.Start)), "–", ClockFormat.Time(DateTime.Today.AddMinutes(quiet.End)))
            : "Off";
        QuietNote = snoozed is { } snooze
            ? string.Concat("Snoozed until ", When(snooze, now, zone))
            : !quiet.Enabled
                ? "No quiet hours"
                : heldUntil is not null
                    ? "Quiet now"
                    : quiet.Days.Count >= 7 ? "Every day" : "Some days";
    }

    private void OnRecentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlertListViewModel.Unread))
        {
            UnreadValue = Recent.Unread.ToString("N0", CultureInfo.CurrentCulture);
        }
    }

    private static string When(DateTimeOffset until, DateTimeOffset now, TimeZoneInfo zone)
    {
        DateTime local = TimeZoneInfo.ConvertTime(until, zone).DateTime;
        DateTime today = TimeZoneInfo.ConvertTime(now, zone).Date;

        return local.Date == today
            ? ClockFormat.Time(local)
            : local.Date == today.AddDays(1)
                ? string.Concat(ClockFormat.Time(local), " tomorrow")
                : ClockFormat.Day(local);
    }

    private string DaysText(List<DayOfWeek> days)
    {
        if (days.Count >= 7)
        {
            return string.Empty;
        }

        string[] names = [.. Days.Where(day => days.Contains(day.Day)).Select(day => day.Name)];

        return names.Length switch
        {
            0 => string.Empty,
            1 => string.Concat(", beginning on ", names[0]),
            _ => string.Concat(", beginning on ", string.Join(", ", names[..^1]), " and ", names[^1]),
        };
    }

    /// <summary>The connection's alerts ride on background watching; say so when it is off.</summary>
    private void DescribeWatching()
    {
        string warning = _settings.Connection.WatchInBackground
            ? string.Empty
            : "Watching the connection in the background is off, so these fire only while a Connection page is open. Turn it on under Connection › Status.";

        foreach (AlertGroupViewModel group in Groups.Where(group => group.Group == AlertGroup.Connection))
        {
            group.Warning = warning;
        }
    }

    private void OnDeskChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            Describe();
        }
        else
        {
            _dispatcher.BeginInvoke(new Action(Describe));
        }
    }

    private void OnForwardingSaved(object? sender, EventArgs e)
    {
        bool forwarding = _settings.Alerts.Forward.Enabled;

        foreach (AlertKindViewModel kind in Groups.SelectMany(group => group.Kinds))
        {
            kind.IsForwardingOn = forwarding;
        }
    }

    /// <summary>Works every level's values and words out again: a unit changed somewhere.</summary>
    private void RefreshLevels()
    {
        foreach (AlertLevelChoice level in _levels)
        {
            level.Refresh();
        }
    }

    // Raised on whichever thread changed the scale.
    private void OnTemperatureScaleChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            RefreshLevels();
        }
        else
        {
            _dispatcher.BeginInvoke(new Action(RefreshLevels));
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_visible)
        {
            Describe();
        }
    }

    private List<AlertGroupViewModel> BuildGroups()
    {
        (AlertGroup Group, string Title, string Note)[] heads =
        [
            (AlertGroup.Connection, "Connection", "When the internet or the network this PC is on changes, or answers slowly. How Windsock decides is under Connection › Status."),
            (AlertGroup.Speed, "Speed", "Downloads that crawl, and uploads that do not stop."),
            (AlertGroup.Usage, "Usage", "Data plan levels, and a word on the week."),
            (AlertGroup.Hardware, "This PC", "Temperatures and drives. While one of these is on, the machine is read every few seconds even with the window closed."),
            (AlertGroup.Changes, "Changes someone else may have made", "To the network or the proxy. A network is remembered the first time it is seen, and compared on every visit after."),
            (AlertGroup.Windsock, "Windsock", "Updates, and a word when you come back to the PC."),
        ];

        List<AlertGroupViewModel> groups = [];

        foreach ((AlertGroup group, string title, string note) in heads)
        {
            AlertKindViewModel[] kinds = [.. AlertCatalogue.Listed
                .Where(kind => kind.Group == group)
                .Select(kind => new AlertKindViewModel(kind, _settings, LevelsFor(kind.Topic), NoteFor(kind.Topic), KindChanged))];

            if (kinds.Length > 0)
            {
                groups.Add(new AlertGroupViewModel(group, title, note, kinds));
            }
        }

        return groups;
    }

    private static string NoteFor(string topic) => topic switch
    {
        NoticeTopics.PlanLevel => "Each plan's levels - 80% and 100% unless you change them - are set with the plan, under Usage › Data plan.",
        WeeklyDigest.Topic => "Sent on the first day of the week, about the week before.",
        NoticeTopics.ProcessorHot => "Read only while Windsock runs as administrator with PawnIO installed; otherwise this alert has nothing to go on.",
        NoticeTopics.NetworkChanged => "Every change is still marked on the charts with this off.",
        _ => string.Empty,
    };

    private IReadOnlyList<AlertLevelChoice> LevelsFor(string topic)
    {
        AlertSettings alerts = _settings.Alerts;
        HardwareAlertSettings hardware = alerts.Hardware;

        return topic switch
        {
            NoticeTopics.PingHigh =>
            [
                Level("Response time over", "Response time level", () => [0, 50, 80, 100, 150, 200, 300, 500], () => alerts.Ping.Milliseconds, Milliseconds, value => alerts.Ping.Milliseconds = value),
                Level("Unanswered checks", "Unanswered checks level", () => [0, 2, 5, 10, 20, 50], () => alerts.Ping.LossPercent, Percent, value => alerts.Ping.LossPercent = value),
                Level("For", "How long slow responses must last", () => [1, 2, 3, 5, 10, 15, 30], () => alerts.Ping.Minutes, Minutes, value => alerts.Ping.Minutes = value),
            ],
            NoticeTopics.SpeedDrop =>
            [
                Level("Below", "Slow download level", Rates, () => (int)Math.Min(int.MaxValue, alerts.SpeedDrop.BelowBytesPerSecond), Rate, value => alerts.SpeedDrop.BelowBytesPerSecond = value),
                Level("For", "How long it must stay slow", () => [2, 3, 5, 10, 15, 30], () => alerts.SpeedDrop.Minutes, Minutes, value => alerts.SpeedDrop.Minutes = value),
            ],
            NoticeTopics.UploadHigh =>
            [
                Level("Above", "Upload level", Rates, () => (int)Math.Min(int.MaxValue, alerts.Upload.AboveBytesPerSecond), Rate, value => alerts.Upload.AboveBytesPerSecond = value),
                Level("For", "How long the upload must last", () => [2, 5, 10, 15, 30, 60], () => alerts.Upload.Minutes, Minutes, value => alerts.Upload.Minutes = value),
            ],
            NoticeTopics.GraphicsHot =>
            [
                Level("At", "Graphics temperature level", () => [65, 70, 75, 80, 85, 90, 95, 100], () => hardware.GraphicsCelsius, Degrees, value => hardware.GraphicsCelsius = value),
            ],
            NoticeTopics.ProcessorHot =>
            [
                Level("At", "Processor temperature level", () => [70, 75, 80, 85, 90, 95, 100, 105], () => hardware.ProcessorCelsius, Degrees, value => hardware.ProcessorCelsius = value),
            ],
            NoticeTopics.DriveHot =>
            [
                Level("At", "Drive temperature level", () => [45, 50, 55, 60, 65, 70, 75], () => hardware.DriveCelsius, Degrees, value => hardware.DriveCelsius = value),
            ],
            NoticeTopics.DriveFull =>
            [
                Level("Fuller than", "Drive fullness level", () => [75, 80, 85, 90, 95, 98], () => hardware.DriveFullPercent, Percent, value => hardware.DriveFullPercent = value),
            ],
            NoticeTopics.DriveWear =>
            [
                Level("Life used", "Drive wear level", () => [50, 60, 70, 80, 90, 100], () => hardware.DriveWearPercent, Percent, value => hardware.DriveWearPercent = value),
            ],
            NoticeTopics.AwaySummary =>
            [
                Level("Locked for at least", "Shortest absence for a summary", () => [1, 5, 10, 15, 30, 60, 120], () => alerts.Away.MinimumMinutes, Minutes, value => alerts.Away.MinimumMinutes = value),
            ],
            _ => [],
        };
    }

    /// <summary>
    /// The speeds offered for the speed alerts, in bytes a second: round in the
    /// unit the chart counts in - binary megabytes, or decimal megabits.
    /// </summary>
    private int[] Rates()
    {
        int[] steps = [128, 256, 512, 1024, 2048, 5120, 10240, 25600, 51200];
        int[] bits = [1, 2, 5, 10, 20, 50, 100, 250, 500];

        return _settings.ChartFamily == RateFamily.Bits
            ? [.. bits.Select(megabits => megabits * 125_000)]
            : [.. steps.Select(kilobytes => kilobytes * 1024)];
    }

    private AlertLevelChoice Level(string label, string name, Func<int[]> values, Func<int> current, Func<int, string> words, Action<int> store)
    {
        AlertLevelChoice choice = new(label, name, values, current, words, value =>
        {
            store(value);
            KindChanged();
        });

        _levels.Add(choice);
        return choice;
    }

    private static string Milliseconds(int value) =>
        value <= 0 ? "Not checked" : string.Create(CultureInfo.CurrentCulture, $"{value} ms");

    private static string Percent(int value) =>
        value <= 0 ? "Not checked" : string.Create(CultureInfo.CurrentCulture, $"{value}%");

    private static string Minutes(int value) => value switch
    {
        1 => "1 minute",
        < 60 => string.Create(CultureInfo.CurrentCulture, $"{value} minutes"),
        60 => "1 hour",
        _ when value % 60 == 0 => string.Create(CultureInfo.CurrentCulture, $"{value / 60} hours"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{value} minutes"),
    };

    private static string Degrees(int celsius) => TemperatureScale.Format(celsius);

    private string Rate(int bytesPerSecond)
    {
        (string value, string unit) = RateFormatter.Split(bytesPerSecond, _settings.ChartFamily, RateScale.Auto);

        // "1.00 MB/s" reads as a measurement; a level is a round number.
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            ? string.Concat(number.ToString("0.##", CultureInfo.CurrentCulture), " ", unit)
            : string.Concat(value, " ", unit);
    }
}
