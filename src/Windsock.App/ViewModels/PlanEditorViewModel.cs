using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Networking;
using Windsock.Core.Plans;

namespace Windsock.App.ViewModels;

/// <summary>A choice in one of the editor's pickers.</summary>
public sealed record EditorOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A network the plan can cover, with a tick.</summary>
public sealed partial class PlanNetworkOption(string key, string name, string note) : ObservableObject
{
    public string Key { get; } = key;

    public string Name { get; } = name;

    /// <summary>Its kind, and whether it is the one in use.</summary>
    public string Note { get; } = note;

    [ObservableProperty]
    public partial bool IsChosen { get; set; }
}

/// <summary>
/// The plan being written: every field of a <see cref="DataPlan"/> in the form
/// a page edits, checked as it changes.
/// </summary>
/// <remarks>
/// Works on a copy; nothing reaches the settings until <see cref="Build"/> is
/// asked for the finished plan and the page saves it. Amounts are entered in
/// the same binary units the rest of Windsock shows, so "100 GB" here is the
/// "100 GB" the page then shows.
/// </remarks>
public sealed partial class PlanEditorViewModel : ObservableObject
{
    private const long Megabyte = 1024L * 1024;
    private readonly string _id;
    private readonly bool _isNew;
    private readonly NetworkSighting? _sighting;

    public PlanEditorViewModel(DataPlan plan, bool isNew, IEnumerable<PlanNetworkOption> networks, NetworkSighting? sighting)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(networks);

        _id = plan.Id;
        _isNew = isNew;
        _sighting = sighting;

        Units =
        [
            new(Megabyte, "MB"),
            new(Megabyte * 1024, "GB"),
            new(Megabyte * 1024 * 1024, "TB"),
        ];

        Cycles =
        [
            new(PlanCycle.Monthly, "Monthly"),
            new(PlanCycle.Weekly, "Weekly"),
            new(PlanCycle.Daily, "Daily"),
            new(PlanCycle.OneOff, "Once, between two dates"),
        ];

        ResetDays = [.. Enumerable.Range(1, 31).Select(day => new EditorOption<int>(day, day >= 29 ? PlanWords.Ordinal(day) + " (or the month's last day)" : PlanWords.Ordinal(day)))];
        Weekdays = [.. Enumerable.Range(0, 7).Select(day => new EditorOption<DayOfWeek>((DayOfWeek)day, CultureInfo.CurrentCulture.DateTimeFormat.GetDayName((DayOfWeek)day)))];

        Rollovers =
        [
            new(RolloverRule.None, "Unused data is lost"),
            new(RolloverRule.NextCycleOnly, "Rolls over to the next cycle only"),
            new(RolloverRule.Accumulate, "Keeps adding up while the plan runs"),
        ];

        Directions =
        [
            new(PlanCounts.Both, "Download and upload"),
            new(PlanCounts.DownloadOnly, "Download only"),
        ];

        Times = [.. Enumerable.Range(0, 96).Select(step => new EditorOption<int>(step * 15, UsageText.Time(new TimeOnly(step / 4, step % 4 * 15))))];
        Networks = new ObservableCollection<PlanNetworkOption>(networks);

        Name = plan.Name;
        (LimitAmount, LimitUnit) = Split(plan.LimitBytes);
        Cycle = plan.Cycle;
        ResetDay = Math.Clamp(plan.ResetDay, 1, 31);
        ResetWeekday = plan.ResetWeekday;
        OneOffStart = (plan.OneOffStart ?? DateTimeOffset.Now).LocalDateTime;
        OneOffEnd = (plan.OneOffEnd ?? DateTimeOffset.Now.AddDays(30)).LocalDateTime;
        Since = (plan.Since ?? DateTimeOffset.Now).LocalDateTime;
        Rollover = plan.Rollover;
        (RolloverCapAmount, _) = Split(plan.RolloverCapBytes, LimitUnit);
        (CarriedInAmount, _) = Split(plan.CarriedInBytes, LimitUnit);
        Counts = plan.Counts;
        OffPeakEnabled = plan.OffPeak.Enabled;
        OffPeakStart = plan.OffPeak.Start;
        OffPeakEnd = plan.OffPeak.End;
        (OffPeakLimitAmount, _) = Split(plan.OffPeak.LimitBytes, LimitUnit);
        AlertLevelsText = string.Join(", ", plan.Levels);
        CountWindowsEstimates = plan.CountWindowsEstimates;
        FromWindows = plan.FromWindows;
        EveryNetwork = plan.CoversEveryNetwork;

        foreach (PlanNetworkOption option in Networks)
        {
            option.IsChosen = plan.Covers(option.Key) && !plan.CoversEveryNetwork;
            option.PropertyChanged += (_, _) => Check();
        }

        Check();
    }

    /// <summary>"Add a plan" or "Change the plan".</summary>
    public string Title => _isNew ? "Add a data plan" : "Change the plan";

    public IReadOnlyList<EditorOption<long>> Units { get; }

    public IReadOnlyList<EditorOption<PlanCycle>> Cycles { get; }

    public IReadOnlyList<EditorOption<int>> ResetDays { get; }

    public IReadOnlyList<EditorOption<DayOfWeek>> Weekdays { get; }

    public IReadOnlyList<EditorOption<RolloverRule>> Rollovers { get; }

    public IReadOnlyList<EditorOption<PlanCounts>> Directions { get; }

    /// <summary>The quarter hours of a day, for the free hours' ends.</summary>
    public IReadOnlyList<EditorOption<int>> Times { get; }

    /// <summary>The networks history knows, the one in use first.</summary>
    public ObservableCollection<PlanNetworkOption> Networks { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>The allowance as typed; empty or 0 for a plan that only keeps count.</summary>
    [ObservableProperty]
    public partial string LimitAmount { get; set; }

    /// <summary>The unit every amount here is in.</summary>
    [ObservableProperty]
    public partial long LimitUnit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMonthly), nameof(IsWeekly), nameof(IsOneOff), nameof(HasCycles))]
    public partial PlanCycle Cycle { get; set; }

    [ObservableProperty]
    public partial int ResetDay { get; set; }

    [ObservableProperty]
    public partial DayOfWeek ResetWeekday { get; set; }

    [ObservableProperty]
    public partial DateTime OneOffStart { get; set; }

    [ObservableProperty]
    public partial DateTime OneOffEnd { get; set; }

    /// <summary>When the plan began: rollover counts from its cycle.</summary>
    [ObservableProperty]
    public partial DateTime Since { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRollover))]
    public partial RolloverRule Rollover { get; set; }

    [ObservableProperty]
    public partial string RolloverCapAmount { get; set; }

    [ObservableProperty]
    public partial string CarriedInAmount { get; set; }

    [ObservableProperty]
    public partial PlanCounts Counts { get; set; }

    [ObservableProperty]
    public partial bool OffPeakEnabled { get; set; }

    /// <summary>When the free hours begin, in minutes after midnight.</summary>
    [ObservableProperty]
    public partial int OffPeakStart { get; set; }

    /// <summary>When they end, in minutes after midnight.</summary>
    [ObservableProperty]
    public partial int OffPeakEnd { get; set; }

    [ObservableProperty]
    public partial string OffPeakLimitAmount { get; set; }

    /// <summary>The alert levels as typed: "80, 100".</summary>
    [ObservableProperty]
    public partial string AlertLevelsText { get; set; }

    [ObservableProperty]
    public partial bool CountWindowsEstimates { get; set; }

    /// <summary>Whether the plan covers every network rather than the ticked ones.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChoosesNetworks))]
    public partial bool EveryNetwork { get; set; }

    /// <summary>Whether the plan was filled in from Windows' limit.</summary>
    [ObservableProperty]
    public partial bool FromWindows { get; set; }

    /// <summary>What is wrong with the plan as written, one line each; empty when it can be saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial string Problems { get; private set; } = string.Empty;

    public bool CanSave => Problems.Length == 0;

    public bool IsMonthly => Cycle == PlanCycle.Monthly;

    public bool IsWeekly => Cycle == PlanCycle.Weekly;

    public bool IsOneOff => Cycle == PlanCycle.OneOff;

    /// <summary>Whether the plan has more than one cycle, so rollover and a start mean something.</summary>
    public bool HasCycles => Cycle != PlanCycle.OneOff;

    public bool HasRollover => Rollover != RolloverRule.None;

    public bool ChoosesNetworks => !EveryNetwork;

    /// <summary>Whether Windows holds a data limit for the network in use, to fill the plan in from.</summary>
    public bool CanUseWindowsLimit => _sighting is { WindowsPlan.LimitBytes: > 0 };

    /// <summary>What Windows holds for the network in use, said either way.</summary>
    public string WindowsLimitText => _sighting switch
    {
        null => "Not on a network, so there is no limit in Windows to use.",
        { WindowsPlan.LimitBytes: > 0 } seen => string.Format(
            CultureInfo.CurrentCulture,
            "Windows has a limit of {0} for {1}{2}.",
            UsageText.Bytes(seen.WindowsPlan.LimitBytes!.Value),
            seen.Network.Name,
            seen.WindowsPlan.NextBillingCycle is { } next ? ", resetting " + UsageText.FullDate(DateOnly.FromDateTime(next.LocalDateTime)) : string.Empty),
        { } seen => string.Format(
            CultureInfo.CurrentCulture,
            "Windows holds no data limit for {0}. One set under Settings, Network & internet, Data usage would show here.",
            seen.Network.Name),
    };

    /// <summary>Fills the plan in from the limit Windows holds for the network in use.</summary>
    public void UseWindowsLimit()
    {
        if (_sighting is null || DataPlan.FromWindowsLimit(_sighting) is not { } windows)
        {
            return;
        }

        (LimitAmount, LimitUnit) = Split(windows.LimitBytes);
        Cycle = PlanCycle.Monthly;
        ResetDay = windows.ResetDay;
        EveryNetwork = false;
        FromWindows = true;

        foreach (PlanNetworkOption option in Networks)
        {
            option.IsChosen = string.Equals(option.Key, _sighting.Network.Key, StringComparison.Ordinal);
        }

        if (string.IsNullOrWhiteSpace(Name) || Name == "Data plan")
        {
            Name = windows.Name;
        }
    }

    /// <summary>The plan as written. Check <see cref="CanSave"/> first.</summary>
    public DataPlan Build()
    {
        DataPlan plan = new()
        {
            Id = _id,
            Name = Name.Trim(),
            LimitBytes = Amount(LimitAmount),
            Cycle = Cycle,
            ResetDay = ResetDay,
            ResetWeekday = ResetWeekday,
            OneOffStart = Cycle == PlanCycle.OneOff ? Moment(OneOffStart) : null,
            OneOffEnd = Cycle == PlanCycle.OneOff ? Moment(OneOffEnd) : null,
            Since = Cycle == PlanCycle.OneOff ? null : Moment(Since),
            Rollover = Rollover,
            RolloverCapBytes = Amount(RolloverCapAmount),
            CarriedInBytes = Amount(CarriedInAmount),
            Counts = Counts,
            Networks = EveryNetwork
                ? []
                : [.. Networks.Where(option => option.IsChosen).Select(option => new PlanNetwork { Key = option.Key, Name = option.Name })],
            OffPeak = new OffPeakWindow
            {
                Enabled = OffPeakEnabled,
                StartMinute = OffPeakStart,
                EndMinute = OffPeakEnd,
                LimitBytes = Amount(OffPeakLimitAmount),
            },
            AlertLevels = Levels(AlertLevelsText),
            CountWindowsEstimates = CountWindowsEstimates,
            FromWindows = FromWindows,
        };

        return plan;
    }

    partial void OnNameChanged(string value) => Check();

    partial void OnLimitAmountChanged(string value) => Check();

    partial void OnCycleChanged(PlanCycle value) => Check();

    partial void OnOneOffStartChanged(DateTime value) => Check();

    partial void OnOneOffEndChanged(DateTime value) => Check();

    partial void OnRolloverCapAmountChanged(string value) => Check();

    partial void OnCarriedInAmountChanged(string value) => Check();

    partial void OnOffPeakEnabledChanged(bool value) => Check();

    partial void OnOffPeakStartChanged(int value) => Check();

    partial void OnOffPeakEndChanged(int value) => Check();

    partial void OnOffPeakLimitAmountChanged(string value) => Check();

    partial void OnAlertLevelsTextChanged(string value) => Check();

    partial void OnEveryNetworkChanged(bool value) => Check();

    private void Check()
    {
        // Called by every field as it is set, the first ones before the rest exist.
        if (Networks is null || Name is null || LimitAmount is null || RolloverCapAmount is null
            || CarriedInAmount is null || OffPeakLimitAmount is null || AlertLevelsText is null)
        {
            return;
        }

        List<string> problems = [];

        foreach ((string text, string what) in new[] { (LimitAmount, "allowance"), (RolloverCapAmount, "rollover cap"), (CarriedInAmount, "carried-in amount"), (OffPeakLimitAmount, "free hours' allowance") })
        {
            if (!IsAmount(text))
            {
                problems.Add(string.Format(CultureInfo.CurrentCulture, "The {0} must be a number, or empty for none.", what));
            }
        }

        if (!EveryNetwork && !Networks.Any(option => option.IsChosen))
        {
            problems.Add("Tick at least one network, or choose every network.");
        }

        if (!string.IsNullOrWhiteSpace(AlertLevelsText) && Levels(AlertLevelsText).Count == 0)
        {
            problems.Add("Alert levels are percentages, such as 80, 100.");
        }

        if (problems.Count == 0)
        {
            problems.AddRange(Build().Problems());
        }

        Problems = string.Join(Environment.NewLine, problems);
    }

    private static bool IsAmount(string text) =>
        string.IsNullOrWhiteSpace(text)
        || (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) && value >= 0 && double.IsFinite(value));

    private long Amount(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) && value > 0 && double.IsFinite(value)
            ? (long)Math.Round(Math.Min(value * LimitUnit, long.MaxValue / 2.0))
            : 0;

    private static List<int> Levels(string text) =>
        [.. text.Split([',', ';', ' ', '%'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.CurrentCulture, out int level) ? level : -1)
            .Where(level => level is > 0 and <= 1000)
            .Distinct()
            .Order()
            .Take(DataPlan.MostLevels)];

    /// <summary>An amount in the largest unit it is a whole or near-whole number of.</summary>
    private static (string Amount, long Unit) Split(long bytes, long? unit = null)
    {
        if (bytes <= 0)
        {
            return (string.Empty, unit ?? Megabyte * 1024);
        }

        long chosen = unit ?? (bytes >= Megabyte * 1024 * 1024 && bytes % (Megabyte * 1024 * 1024) == 0
            ? Megabyte * 1024 * 1024
            : bytes >= Megabyte * 1024 ? Megabyte * 1024 : Megabyte);

        return (Math.Round((double)bytes / chosen, 2).ToString("0.##", CultureInfo.CurrentCulture), chosen);
    }

    private static DateTimeOffset Moment(DateTime local)
    {
        DateTime minute = new(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(minute, TimeZoneInfo.Local.GetUtcOffset(minute));
    }
}
