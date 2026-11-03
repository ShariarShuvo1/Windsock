using System.Globalization;
using System.Text.Json.Serialization;
using Windsock.Core.Connectivity;
using Windsock.Core.Networking;

namespace Windsock.Core.Plans;

/// <summary>How often a plan's allowance starts again.</summary>
public enum PlanCycle
{
    /// <summary>On a day of the month.</summary>
    Monthly,

    /// <summary>On a day of the week.</summary>
    Weekly,

    /// <summary>Every midnight.</summary>
    Daily,

    /// <summary>Once, between two moments: a pass bought for a trip, a 30-day pack.</summary>
    OneOff,
}

/// <summary>What happens to allowance left unused at the end of a cycle.</summary>
public enum RolloverRule
{
    /// <summary>It is gone.</summary>
    None,

    /// <summary>
    /// It is added to the next cycle only, used before that cycle's own, and
    /// gone if that cycle does not use it - the way most carriers do rollover.
    /// </summary>
    NextCycleOnly,

    /// <summary>It goes on adding up from cycle to cycle, as long as the plan runs.</summary>
    Accumulate,
}

/// <summary>Which direction a plan counts.</summary>
public enum PlanCounts
{
    /// <summary>Download and upload, as nearly every plan does.</summary>
    Both,

    /// <summary>Download only.</summary>
    DownloadOnly,
}

/// <summary>A network a plan applies to, with the name it had when it was chosen.</summary>
/// <remarks>Text is kept as text: a null in a hand-edited file becomes empty as it is read.</remarks>
public sealed class PlanNetwork
{
    /// <summary>The network's key (see <see cref="NetworkTag.Key"/>).</summary>
    public string Key
    {
        get;
        set => field = value ?? string.Empty;
    } = string.Empty;

    /// <summary>Its name when chosen, so the plan can still name it after history is erased.</summary>
    public string Name
    {
        get;
        set => field = value ?? string.Empty;
    } = string.Empty;

    /// <summary>
    /// What kind of network it is, when known: filled in from a sighting, and
    /// by a settings export from what history knows, so another PC can tell a
    /// Wi-Fi network from a cable of the same name.
    /// </summary>
    public NetworkKind? Kind { get; set; }
}

/// <summary>
/// Hours each night that a plan does not count against its allowance, as many
/// providers give.
/// </summary>
/// <remarks>
/// Kept as minutes after local midnight, in steps of a quarter hour. A window
/// whose end is before its start runs over midnight, as 23:00 to 07:00 does.
/// It follows the wall clock: on the night the clocks change it is an hour
/// shorter or longer, as the provider's is.
/// </remarks>
public sealed class OffPeakWindow
{
    /// <summary>Minutes in a day.</summary>
    public const int Day = 24 * 60;

    /// <summary>The step a window's ends are held to.</summary>
    public const int Step = 15;

    /// <summary>Whether the plan has such hours at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>When they begin, in minutes after midnight.</summary>
    public int StartMinute { get; set; } = 60;

    /// <summary>When they end, in minutes after midnight. Exclusive.</summary>
    public int EndMinute { get; set; } = 7 * 60;

    /// <summary>
    /// An allowance of their own, in bytes; zero, the usual, when what is used
    /// in them is free.
    /// </summary>
    public long LimitBytes { get; set; }

    /// <summary>The start held to a quarter hour within a day.</summary>
    [JsonIgnore]
    public int Start => Hold(StartMinute);

    /// <summary>The end held to a quarter hour within a day.</summary>
    [JsonIgnore]
    public int End => Hold(EndMinute);

    /// <summary>Whether a time of day, in minutes after midnight, falls inside the window.</summary>
    public bool Contains(int minuteOfDay)
    {
        if (!Enabled || Start == End)
        {
            return false;
        }

        return Start < End
            ? minuteOfDay >= Start && minuteOfDay < End
            : minuteOfDay >= Start || minuteOfDay < End;
    }

    /// <summary>How long the window lasts on an ordinary night.</summary>
    [JsonIgnore]
    public TimeSpan Length => !Enabled || Start == End
        ? TimeSpan.Zero
        : TimeSpan.FromMinutes(Start < End ? End - Start : Day - Start + End);

    private static int Hold(int minute)
    {
        int within = ((minute % Day) + Day) % Day;
        return within - (within % Step);
    }
}

/// <summary>
/// A data plan: an allowance that starts again every cycle, on the networks it
/// covers, with rollover and free night hours if the provider gives them.
/// </summary>
/// <remarks>
/// Kept in the settings, as everything the person chooses is. Built on the
/// minutes history records, so a plan made today counts the cycle already under
/// way. A page that changes the list should replace it rather than change it in
/// place: <see cref="PlanMonitor"/> reads it from another thread.
/// </remarks>
public sealed class DataPlan
{
    /// <summary>The most levels a plan raises a crossing at.</summary>
    public const int MostLevels = 8;

    /// <summary>A stable id, so a renamed plan keeps its crossings.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>What the person calls it.</summary>
    public string Name { get; set; } = "Data plan";

    /// <summary>The allowance each cycle, in bytes; zero for a plan that only keeps count.</summary>
    public long LimitBytes { get; set; }

    /// <summary>How often the allowance starts again.</summary>
    public PlanCycle Cycle { get; set; } = PlanCycle.Monthly;

    /// <summary>
    /// For a monthly plan, the day of the month it starts again, 1 to 31; a
    /// month too short for the day starts again on its last day.
    /// </summary>
    public int ResetDay { get; set; } = 1;

    /// <summary>For a weekly plan, the day of the week it starts again.</summary>
    public DayOfWeek ResetWeekday { get; set; } = DayOfWeek.Monday;

    /// <summary>For a one-off plan, when it starts.</summary>
    public DateTimeOffset? OneOffStart { get; set; }

    /// <summary>For a one-off plan, when it ends. Exclusive.</summary>
    public DateTimeOffset? OneOffEnd { get; set; }

    /// <summary>
    /// When the plan began. Rollover is counted from the cycle holding this
    /// moment; with none, the current cycle is the first.
    /// </summary>
    public DateTimeOffset? Since { get; set; }

    /// <summary>What happens to allowance left unused.</summary>
    public RolloverRule Rollover { get; set; } = RolloverRule.None;

    /// <summary>The most that can be carried into a cycle, in bytes; zero for no cap.</summary>
    public long RolloverCapBytes { get; set; }

    /// <summary>Allowance carried into the first cycle from before Windsock knew of the plan, in bytes.</summary>
    public long CarriedInBytes { get; set; }

    /// <summary>Which direction counts.</summary>
    public PlanCounts Counts { get; set; } = PlanCounts.Both;

    /// <summary>The networks the plan covers; none means every network, including ones not known.</summary>
    /// <remarks>A null in a hand-edited file reads as none, as the settings' own groups do.</remarks>
    public List<PlanNetwork> Networks
    {
        get => field ??= [];
        set => field = value ?? [];
    }

    /// <summary>Hours that do not count.</summary>
    public OffPeakWindow OffPeak
    {
        get => field ??= new OffPeakWindow();
        set => field = value ?? new OffPeakWindow();
    }

    /// <summary>The levels, in percent of the allowance, at which a crossing is raised.</summary>
    public List<int> AlertLevels
    {
        get => field ??= [80, 100];
        set => field = value ?? [];
    }

    /// <summary>
    /// Whether what Windows counted while Windsock was closed counts too. Only a
    /// plan on every network can use it: Windows' figures do not say which
    /// network they were on.
    /// </summary>
    public bool CountWindowsEstimates { get; set; } = true;

    /// <summary>Whether the plan was filled in from the limit set in Windows.</summary>
    public bool FromWindows { get; set; }

    /// <summary>Whether the plan covers every network rather than chosen ones.</summary>
    [JsonIgnore]
    public bool CoversEveryNetwork => Networks.Count == 0;

    /// <summary>Whether the plan covers a network, by its key.</summary>
    public bool Covers(string? networkKey) =>
        CoversEveryNetwork || (networkKey is not null && Networks.Exists(each => string.Equals(each.Key, networkKey, StringComparison.Ordinal)));

    /// <summary>The levels to raise crossings at: distinct, between 1% and 1000%, lowest first.</summary>
    [JsonIgnore]
    public IReadOnlyList<int> Levels =>
        [.. AlertLevels.Where(level => level is > 0 and <= 1000).Distinct().Order().Take(MostLevels)];

    /// <summary>What is wrong with the plan as written, in plain words; empty when nothing is.</summary>
    public IReadOnlyList<string> Problems()
    {
        List<string> problems = [];

        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("The plan needs a name.");
        }

        if (Networks.Exists(network => network is null))
        {
            problems.Add("A network the plan counts is missing.");
        }

        if (LimitBytes < 0)
        {
            problems.Add("The allowance cannot be less than nothing.");
        }

        if (Cycle == PlanCycle.Monthly && ResetDay is < 1 or > 31)
        {
            problems.Add("A monthly plan starts again on a day from 1 to 31.");
        }

        if (Cycle == PlanCycle.OneOff && (OneOffStart is not { } start || OneOffEnd is not { } end || end <= start))
        {
            problems.Add("A one-off plan needs a start and an end after it.");
        }

        if (RolloverCapBytes < 0 || CarriedInBytes < 0)
        {
            problems.Add("Rollover cannot be less than nothing.");
        }

        if (OffPeak.Enabled && OffPeak.Start == OffPeak.End)
        {
            problems.Add("Free hours need an end different from their start.");
        }

        if (OffPeak.LimitBytes < 0)
        {
            problems.Add("The free hours' allowance cannot be less than nothing.");
        }

        return problems;
    }

    /// <summary>
    /// A plan filled in from the limit Windows holds for a network - set under
    /// Settings, Data usage, or provisioned by a mobile operator - or null when
    /// Windows holds none.
    /// </summary>
    public static DataPlan? FromWindowsLimit(NetworkSighting sighting)
    {
        ArgumentNullException.ThrowIfNull(sighting);

        if (sighting.WindowsPlan is not { LimitBytes: > 0 } windows)
        {
            return null;
        }

        DataPlan plan = new()
        {
            Name = sighting.Network.Name,
            LimitBytes = windows.LimitBytes.Value,
            Networks = [new PlanNetwork { Key = sighting.Network.Key, Name = sighting.Network.Name, Kind = sighting.Network.Kind }],
            FromWindows = true,
        };

        if (windows.NextBillingCycle is { } next)
        {
            plan.ResetDay = next.ToLocalTime().Day;
        }

        return plan;
    }

    /// <summary>
    /// Takes out missing plans and gives every plan an id of its own: a blank
    /// one, or one an earlier plan already has, takes <paramref name="fresh"/>
    /// (asked with the plan's place in the list). A hand-edited or imported
    /// file can hold either, and an id is what the crossings already told are
    /// kept under and what a page finds its plan by. Says whether anything
    /// changed.
    /// </summary>
    public static bool Tidy(List<DataPlan> plans, Func<int, string> fresh)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(fresh);

        bool changed = plans.RemoveAll(plan => plan is null) > 0;
        HashSet<string> taken = new(StringComparer.Ordinal);

        for (int index = 0; index < plans.Count; index++)
        {
            DataPlan plan = plans[index];

            if (!string.IsNullOrWhiteSpace(plan.Id) && taken.Add(plan.Id))
            {
                continue;
            }

            string id = fresh(index);

            for (int extra = 2; string.IsNullOrWhiteSpace(id) || !taken.Add(id); extra++)
            {
                id = string.Concat(fresh(index), "-", extra.ToString(CultureInfo.InvariantCulture));
            }

            plan.Id = id;
            changed = true;
        }

        return changed;
    }

    /// <summary>The plan for a network: the first naming it, else the first covering every network.</summary>
    public static DataPlan? For(IEnumerable<DataPlan> plans, string? networkKey)
    {
        ArgumentNullException.ThrowIfNull(plans);

        DataPlan? anyNetwork = null;

        foreach (DataPlan plan in plans)
        {
            if (!plan.CoversEveryNetwork && plan.Covers(networkKey))
            {
                return plan;
            }

            anyNetwork ??= plan.CoversEveryNetwork ? plan : null;
        }

        return anyNetwork;
    }
}
