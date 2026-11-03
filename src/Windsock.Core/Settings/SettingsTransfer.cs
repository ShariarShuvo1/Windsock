using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Windsock.Core.Alerts;
using Windsock.Core.Connectivity;
using Windsock.Core.Formatting;
using Windsock.Core.Meter;
using Windsock.Core.Networking;
using Windsock.Core.Plans;

namespace Windsock.Core.Settings;

/// <summary>One choice an import would change, said in words.</summary>
/// <param name="Name">What the choice is called, as Settings calls it.</param>
/// <param name="Was">What it is now.</param>
/// <param name="Becomes">What the file would make it.</param>
public sealed record SettingChange(string Name, string Was, string Becomes)
{
    /// <summary>
    /// The change in words, "Theme: Dark to Light", which is what a screen
    /// reader names the row by; a record's own ToString lists its members.
    /// </summary>
    public override string ToString() => string.Concat(Name, ": ", Was, " to ", Becomes);
}

/// <summary>The changes a list has room for, and how many are left for one closing line.</summary>
/// <param name="Shown">The changes to list, in order.</param>
/// <param name="More">How many changes are not listed.</param>
public sealed record SettingChangeListing(IReadOnlyList<SettingChange> Shown, int More)
{
    /// <summary>The closing line for the changes not listed, or empty when every change is.</summary>
    public string MoreText => More switch
    {
        0 => string.Empty,
        1 => "…and 1 more.",
        _ => string.Create(CultureInfo.CurrentCulture, $"…and {More:N0} more."),
    };
}

/// <summary>What reading a settings file found: the settings it holds, or why it could not be read.</summary>
public sealed class SettingsImport
{
    private SettingsImport(WindsockSettings? settings, string problem, IReadOnlyList<SettingChange> changes, IReadOnlyList<string> notes)
    {
        Settings = settings;
        Problem = problem;
        Changes = changes;
        Notes = notes;
    }

    /// <summary>The settings the file holds, checked and settled, or null when it could not be read.</summary>
    public WindsockSettings? Settings { get; }

    /// <summary>Why the file could not be read, in a sentence; empty when it could.</summary>
    public string Problem { get; }

    /// <summary>Every choice the file would change, in the order Settings lists them.</summary>
    public IReadOnlyList<SettingChange> Changes { get; }

    /// <summary>
    /// What the file holds that this PC will not take as it is, each a
    /// sentence: a data plan for networks this PC does not know, meter places
    /// for readings it cannot show, feed places without an address. Said
    /// beside the changes, never counted among them.
    /// </summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>Whether the file was read.</summary>
    public bool IsValid => Settings is not null;

    internal static SettingsImport Refused(string problem) => new(null, problem, [], []);

    internal static SettingsImport Read(WindsockSettings settings, IReadOnlyList<SettingChange> changes, IReadOnlyList<string> notes) =>
        new(settings, string.Empty, changes, notes);
}

/// <summary>
/// Moves the settings to another PC as one file, and back.
/// </summary>
/// <remarks>
/// <para>
/// The file is the settings and nothing else: never history, outages or logs,
/// which live in their own files and are not this feature's to move. It is
/// wrapped in a small envelope naming what it is and which version wrote it,
/// so a file that is not one is refused before any of it is believed.
/// </para>
/// <para>
/// What belongs to this PC stays on it, by one list,
/// <see cref="StaysOnThisPc"/>: the export leaves it out, an import neither
/// reads it nor lists it as a change, and <see cref="WindsockSettings.CopyFrom"/>
/// keeps this PC's own. Three things to a list, so they cannot drift apart: an
/// earlier cut kept a separate rule in each, and the change list then showed
/// choices the import never made.
/// </para>
/// <para>
/// Reading is strict where the settings store is forgiving. A settings file
/// that cannot be read falls back to the defaults, because the app has to
/// start; an import that cannot be read is refused and changes nothing,
/// because the person has settings of their own that it would replace. Enum
/// values must be names, not numbers, and a data plan must be one Windsock
/// can count, and a file Windsock cannot take whole - a key written twice, a
/// lone surrogate, a null where a list belongs - is refused rather than thrown
/// at whoever asked: every failure <see cref="Read"/> can meet becomes a
/// refusal.
/// </para>
/// <para>
/// Sizes are held too: a settings file is a few kilobytes, so the file is
/// read only up to <see cref="LargestFile"/>, a file with more data plans than
/// <see cref="MostPlans"/> is refused, and lists that only grow by hand -
/// alert levels, the days of the week, alert choices for kinds there are not
/// - are cut to what they can hold.
/// </para>
/// </remarks>
public static class SettingsTransfer
{
    /// <summary>What an exported file says it is.</summary>
    public const string FormatName = "Windsock settings";

    /// <summary>The version of the envelope this build writes, and the newest it reads.</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// The largest file an import will read, in characters. A settings file is
    /// a few kilobytes; this is room for every data plan it may hold.
    /// </summary>
    public const int LargestFile = 256 * 1024;

    /// <summary>The most data plans a file may hold.</summary>
    public const int MostPlans = 32;

    /// <summary>
    /// The most changes the import dialog lists one by one; the rest are one
    /// closing line, so a file that touches everything builds no more rows.
    /// </summary>
    public const int MostListed = 40;

    private const string FormatKey = "Format";
    private const string VersionKey = "Version";
    private const string ExportedKey = "Exported";
    private const string SettingsKey = "Settings";
    private const string TaskbarKey = nameof(WindsockSettings.Taskbar);
    private const string PlansPath = "Usage.Plans";
    private const string Every = "[*]";
    private const string MapMark = "map:";

    private const string NotSettings =
        "This is not a Windsock settings file. Choose a file made by Export settings.";

    private const string NotReadable =
        "Part of the file could not be read, so none of it was used. It may have been changed by hand.";

    private static readonly JsonDocumentOptions Parsing = new() { AllowDuplicateProperties = false };

    private static readonly JsonSerializerOptions Writing = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions Reading = new()
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>
    /// Every setting that belongs to this PC rather than to the person: never
    /// exported, never read from a file, never listed as a change, and kept as
    /// it is by <see cref="WindsockSettings.CopyFrom"/>. A dotted path into the
    /// settings as they are written; <c>[*]</c> stands for each item of a
    /// list, matched to this PC's item in the same place.
    /// </summary>
    /// <remarks>
    /// Three kinds. This PC's hardware and screens - adapters, monitors, a
    /// graphics card by name - which mean nothing on another. What this PC
    /// records and keeps - history, its networks' names, the timeline, the
    /// copies of history, the record of alerts - which an import must neither
    /// start nor cut short: a shorter keep deletes what was kept. And
    /// every contact beyond this PC with its address: the switches under Data
    /// and privacy, the meter's feeds, the local feed, the agreement with M-Lab.
    /// An import never makes this PC contact anyone it did not already, or
    /// stop. Where alerts are forwarded is not here because it is never in a
    /// settings file at all (see <see cref="AlertSettings.Forward"/>).
    /// </remarks>
    public static IReadOnlyList<string> StaysOnThisPc { get; } =
    [
        // This PC's hardware and screens.
        "Adapters",
        "Window.Places",
        "Window.LastPage",
        "Window.LastMonitor",
        "Taskbar.MiniSpot",
        "Taskbar.Spots",
        "Taskbar.Welcomed",
        "Taskbar.Monitors",
        "Taskbar.ChosenMonitor",
        "Taskbar.Slots[*].Card",

        // What this PC records and keeps.
        "RecordHistory",
        "KeepMinuteDetailDays",
        "Usage.Networks",
        "Usage.AutoExport.Enabled",
        "Usage.AutoExport.Folder",
        "Insight.Timeline",
        "Insight.TimelineDays",
        "Usage.Backup.Enabled",
        "Usage.Backup.Keep",
        "Alerts.KeepDays",

        // Contacts beyond this PC, and where they go.
        "CheckForUpdates",
        "TraceRoutes",
        "ReverseLookups",
        "Connection.InternetCheck",
        "Connection.CustomTarget",
        "ConnectionTools.PublicAddressLookups",
        "ConnectionTools.PublicAddressService",
        "ConnectionTools.PublicAddressCustomUrl",
        "ConnectionTools.DnsTiming",
        "ConnectionTools.StunTest",
        "ConnectionTools.StunServer",
        "ConnectionTools.StunCustomServer",
        "ConnectionTools.SpeedTestServer",
        "ConnectionTools.LibreSpeedUrl",
        "ConnectionTools.MeasurementLabConsentAt",
        "ConnectionTools.ScheduledSpeedTests",
        "ConnectionTools.ScheduledTestsADay",
        "ConnectionTools.ScheduledTestsOnMetered",
        "Taskbar.Feeds",
        "Taskbar.RefreshPublicAddress",
        "Taskbar.Slots[*].Feed.Address",
        "Insight.SpamhausDrop",
        "Insight.FeodoTracker",
        "Home.FindDevices",
        "Home.CheckRouter",
        "Home.ReadRouterTraffic",
        "Feed",
    ];

    /// <summary>
    /// The names Settings uses for each choice, in the order it lists them, so
    /// the list of changes reads like the pages it describes.
    /// </summary>
    private static readonly (string Path, string Name)[] Names =
    [
        ("OnClose", "When the window is closed"),
        ("Theme", "Theme"),
        ("Temperature", "Temperatures"),
        ("Appearance.UseWindowsAccent", "Use the Windows accent colour"),
        ("Appearance.Density", "Density"),
        ("Window.OpenOn", "Open the window on"),
        ("SidebarOpen", "Sidebar shows names"),
        ("ChartFamily", "Network chart counts in"),
        ("ChartScale", "Network chart scale"),
        ("AppsShowProcesses", "Apps page shows processes"),
        ("HistoryRowsPerPage", "History rows per page"),
        ("OutageRowsPerPage", "Outage rows per page"),
        ("Usage.AutoExport.Every", "How often to export"),
        ("Usage.AutoExport.Format", "Export format"),
        ("Usage.Digest.Enabled", "Weekly digest"),
        ("Usage.Digest.WeekStarts", "The day weeks start on"),
        ("ConnectionTools.PlanDownloadMbps", "Plan download, Mbit/s"),
        ("ConnectionTools.PlanUploadMbps", "Plan upload, Mbit/s"),
        ("Notifications.Enabled", "Notifications"),
        ("Notifications.Sound", "Notification sound"),
        ("Connection.NotifyLostAndRestored", "Alert when the internet drops and comes back"),
        ("Connection.NotifyNetworkChanges", "Alert when the network or a VPN changes"),
        ("Connection.WatchInBackground", "Watch the connection in the background"),
        ("Connection.CheckEverySeconds", "Seconds between checks"),
        ("Connection.OutageAfterSeconds", "Seconds before an outage is called"),
        ("Alerts.Quiet.Enabled", "Quiet hours"),
        ("Alerts.Quiet.StartMinute", "Quiet hours begin (minutes past midnight)"),
        ("Alerts.Quiet.EndMinute", "Quiet hours end (minutes past midnight)"),
        ("Alerts.Ping.Enabled", "Alert on slow responses or lost pings"),
        ("Alerts.Ping.Milliseconds", "Slow responses: response time over (ms)"),
        ("Alerts.Ping.LossPercent", "Slow responses: unanswered checks (%)"),
        ("Alerts.Ping.Minutes", "Slow responses: for (minutes)"),
        ("Alerts.SpeedDrop.Enabled", "Alert when a download stays slow"),
        ("Alerts.SpeedDrop.BelowBytesPerSecond", "Slow download: below (bytes a second)"),
        ("Alerts.SpeedDrop.Minutes", "Slow download: for (minutes)"),
        ("Alerts.Upload.Enabled", "Alert on unusual upload"),
        ("Alerts.Upload.AboveBytesPerSecond", "Unusual upload: above (bytes a second)"),
        ("Alerts.Upload.Minutes", "Unusual upload: for (minutes)"),
        ("Alerts.PlanLevels", "Alert at data plan levels"),
        ("Alerts.Hardware.GraphicsHot", "Alert when graphics are too hot"),
        ("Alerts.Hardware.GraphicsCelsius", "Graphics too hot at (°C)"),
        ("Alerts.Hardware.ProcessorHot", "Alert when the processor is too hot"),
        ("Alerts.Hardware.ProcessorCelsius", "Processor too hot at (°C)"),
        ("Alerts.Hardware.DriveHot", "Alert when a drive is too hot"),
        ("Alerts.Hardware.DriveCelsius", "Drive too hot at (°C)"),
        ("Alerts.Hardware.DriveFull", "Alert when a drive is nearly full"),
        ("Alerts.Hardware.DriveFullPercent", "Drive nearly full at (%)"),
        ("Alerts.Hardware.DriveWear", "Alert when a drive is wearing out"),
        ("Alerts.Hardware.DriveWearPercent", "Drive wearing out at (% of life used)"),
        ("Alerts.Changes.Address", "Alert when the address changes"),
        ("Alerts.Changes.Dns", "Alert when the DNS servers change"),
        ("Alerts.Changes.Router", "Alert when the router changes"),
        ("Alerts.Changes.Proxy", "Alert when the proxy changes"),
        ("Insight.NewAppNotices", "Alert when a new app goes online"),
        ("Home.NotifyNewDevices", "Alert when a new device joins the home network"),
        ("Alerts.UpdateReady", "Alert when an update is ready"),
        ("Alerts.Away.Enabled", "Summary on unlocking the screen"),
        ("Alerts.Away.MinimumMinutes", "Summary after a lock of at least (minutes)"),
    ];

    /// <summary>
    /// Settings kept as lists or maps, each said as one line however many of
    /// its entries differ, as the meter's layout is.
    /// </summary>
    private static readonly (string Prefix, string Name, string Was, string Becomes)[] Wholes =
    [
        ("Alerts.Choices", "Each alert's sound, quiet hours and forwarding", "Your choices", "The file's choices"),
        ("Alerts.Quiet.Days", "Days quiet hours begin on", "Your days", "The file's days"),
    ];

    /// <summary>
    /// Every leaf written as an enum's name, as a path with <c>[*]</c> for a
    /// list's items: only these are spaced into words. Free text and numbers
    /// are shown as they are.
    /// </summary>
    private static readonly HashSet<string> EnumPaths = FindEnums();

    /// <summary>
    /// Every path that holds a map by key - each alert's choices by its topic -
    /// so a key in a leaf's path can be read as <c>*</c>, as a list's place is.
    /// </summary>
    private static readonly string[] MapPaths =
        [.. EnumPaths.Where(path => path.StartsWith(MapMark, StringComparison.Ordinal)).Select(path => path[MapMark.Length..])];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo[]> WrittenByType = new();

    /// <summary>
    /// The settings as an export file: the envelope, then every choice but
    /// what stays on this PC.
    /// </summary>
    /// <param name="settings">The settings to write.</param>
    /// <param name="at">When, for the envelope.</param>
    /// <param name="seen">
    /// The networks this PC's history knows: a data plan's network is written
    /// with its kind, so another PC can tell a Wi-Fi network from a cable of
    /// the same name.
    /// </param>
    public static string Export(WindsockSettings settings, DateTimeOffset at, IEnumerable<NetworkTag>? seen = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        JsonObject body = Tree(settings);
        Kinds(body, seen);

        JsonObject file = new()
        {
            [FormatKey] = FormatName,
            [VersionKey] = FormatVersion,
            [ExportedKey] = at.ToString("O", CultureInfo.InvariantCulture),
            [SettingsKey] = body,
        };

        return file.ToJsonString(Writing);
    }

    /// <summary>
    /// Reads an export file, checks it, and says what it would change against
    /// the settings in use. Nothing is applied: the caller decides that once
    /// the person has seen the list.
    /// </summary>
    /// <param name="text">The file.</param>
    /// <param name="current">The settings in use, which keep what stays on this PC.</param>
    /// <param name="seen">
    /// The networks this PC's history knows, so a data plan's networks can be
    /// found here by name; nothing for only those the settings name.
    /// </param>
    /// <param name="offered">
    /// Whether this PC and edition can show a reading on the meter, so a
    /// meter made elsewhere cannot bring a battery, an NPU or a reading this
    /// edition leaves out (see <see cref="MeterLayoutFile.Fit"/>); nothing to
    /// take every reading as it is.
    /// </param>
    public static SettingsImport Read(
        string text,
        WindsockSettings current,
        IEnumerable<NetworkTag>? seen = null,
        Func<MeterField, bool>? offered = null)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (string.IsNullOrWhiteSpace(text))
        {
            return SettingsImport.Refused("The file is empty.");
        }

        if (text.Length > LargestFile)
        {
            return SettingsImport.Refused("The file is far too large to be Windsock's settings.");
        }

        // Whatever a crafted file makes the reader throw - a key twice, a lone
        // surrogate, a value of the wrong kind where it is not expected - is a
        // file that cannot be read, never a failure for the caller to meet.
        try
        {
            return Checked(text, current, seen, offered);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException
            or FormatException or NotSupportedException or ArithmeticException)
        {
            return SettingsImport.Refused(NotReadable);
        }
    }

    private static SettingsImport Checked(
        string text,
        WindsockSettings current,
        IEnumerable<NetworkTag>? seen,
        Func<MeterField, bool>? offered)
    {
        JsonObject? file;

        try
        {
            file = JsonNode.Parse(text, documentOptions: Parsing) as JsonObject;
        }
        catch (JsonException)
        {
            return SettingsImport.Refused(NotSettings);
        }

        if (file is null
            || file[FormatKey] is not JsonValue format
            || !format.TryGetValue(out string? name)
            || !string.Equals(name, FormatName, StringComparison.Ordinal)
            || file[SettingsKey] is not JsonObject body)
        {
            return SettingsImport.Refused(NotSettings);
        }

        if (file[VersionKey] is not JsonValue version || !version.TryGetValue(out int number) || number < 1)
        {
            return SettingsImport.Refused(NotSettings);
        }

        if (number > FormatVersion)
        {
            return SettingsImport.Refused(
                "This file was made by a newer Windsock. Update this copy first, then import it.");
        }

        // What belongs to this PC is not read at all, so a file cannot be
        // refused, or believed, over something this PC will keep anyway.
        Strip(body);

        // One name a choice: the reader would take "Monday, Friday" apart at
        // its comma and quietly keep whichever value the two add up to.
        if (Joined(body) is { } joined)
        {
            return SettingsImport.Refused(string.Concat(
                "The file holds a choice Windsock does not know, at ", Spoken(joined), ", so none of it was used."));
        }

        WindsockSettings? read;

        try
        {
            read = body.Deserialize<WindsockSettings>(Reading);
        }
        catch (JsonException error)
        {
            return SettingsImport.Refused(string.Concat(
                "Part of the file could not be read, so none of it was used. ",
                Where(body, error)));
        }
        catch (NotSupportedException)
        {
            return SettingsImport.Refused(NotSettings);
        }

        if (read is null)
        {
            return SettingsImport.Refused(NotSettings);
        }

        if (Problem(read) is { } problem)
        {
            return SettingsImport.Refused(problem);
        }

        // The meter is held to what it can show before anything else looks at
        // it: places past its rows and columns go, so a file of a hundred
        // thousand places costs no more than one of six from here on.
        read.Taskbar.Settle();

        Trim(read);

        WindsockSettings taken = ForThisPc(read, current);
        Hold(taken);

        List<string> notes = PlansHere(taken, current, seen);

        if (offered is not null)
        {
            Fit(taken.Taskbar, offered, notes);
        }

        FeedsWithoutAddress(taken.Taskbar, notes);

        return SettingsImport.Read(taken, Compare(current, taken), notes);
    }

    /// <summary>
    /// Every choice that differs between two sets of settings, in words. The
    /// taskbar meter is one line, however many of its cells differ: it is
    /// taken whole or not at all. What stays on this PC is never a difference.
    /// </summary>
    public static IReadOnlyList<SettingChange> Compare(WindsockSettings current, WindsockSettings next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);

        Dictionary<string, Leaf> before = Flatten(Tree(current));
        Dictionary<string, Leaf> after = Flatten(Tree(next));

        List<SettingChange> changes = [];
        HashSet<string> said = new(StringComparer.Ordinal);

        foreach ((string path, string label) in Names)
        {
            said.Add(path);

            Leaf was = before.GetValueOrDefault(path);
            Leaf becomes = after.GetValueOrDefault(path);

            if (was != becomes)
            {
                changes.Add(new SettingChange(label, Words(path, was), Words(path, becomes)));
            }
        }

        int meter = 0;
        bool plans = false;
        Dictionary<string, int> wholes = new(StringComparer.Ordinal);

        foreach (string path in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            if (said.Contains(path))
            {
                continue;
            }

            Leaf was = before.GetValueOrDefault(path);
            Leaf becomes = after.GetValueOrDefault(path);

            if (was == becomes)
            {
                continue;
            }

            if (path.StartsWith(TaskbarKey + ".", StringComparison.Ordinal))
            {
                meter++;
                continue;
            }

            if (path.StartsWith(PlansPath, StringComparison.Ordinal))
            {
                plans = true;
                continue;
            }

            if (Array.Find(Wholes, whole => path.StartsWith(whole.Prefix, StringComparison.Ordinal)) is { Prefix: not null } part)
            {
                wholes[part.Prefix] = wholes.GetValueOrDefault(part.Prefix) + 1;
                continue;
            }

            // A choice newer than this list: named from its key rather than left out.
            changes.Add(new SettingChange(Spoken(path), Words(path, was), Words(path, becomes)));
        }

        if (plans)
        {
            changes.Add(new SettingChange("Data plans", PlanNames(current.Usage.Plans), PlanNames(next.Usage.Plans)));
        }

        foreach ((string prefix, string name, string was, string becomes) in Wholes)
        {
            if (wholes.GetValueOrDefault(prefix) > 0)
            {
                changes.Add(new SettingChange(name, was, becomes));
            }
        }

        if (meter > 0)
        {
            changes.Add(new SettingChange(
                "Taskbar meter",
                "Your layout",
                meter == 1 ? "The file's layout (1 difference)" : string.Create(CultureInfo.CurrentCulture, $"The file's layout ({meter} differences)")));
        }

        return changes;
    }

    /// <summary>The changes a list shows one by one, at most <see cref="MostListed"/>, and how many more there are.</summary>
    public static SettingChangeListing Listing(IReadOnlyList<SettingChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        return changes.Count <= MostListed
            ? new SettingChangeListing(changes, 0)
            : new SettingChangeListing([.. changes.Take(MostListed)], changes.Count - MostListed);
    }

    /// <summary>
    /// Another set of settings with this PC's own in every place
    /// <see cref="StaysOnThisPc"/> names: a new object, sharing nothing with
    /// either.
    /// </summary>
    internal static WindsockSettings ForThisPc(WindsockSettings incoming, WindsockSettings here)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(here);

        JsonObject theirs = Whole(incoming);
        JsonObject mine = Whole(here);

        foreach (string path in StaysOnThisPc)
        {
            Keep(theirs, mine, path.Split('.'), 0);
        }

        return theirs.Deserialize<WindsockSettings>(Writing) ?? new WindsockSettings();
    }

    /// <summary>
    /// What makes a read file one Windsock cannot use, in a sentence, or null.
    /// A plan Windsock cannot count is refused rather than mended: the person
    /// would be told it was imported and then find it wrong.
    /// </summary>
    private static string? Problem(WindsockSettings read)
    {
        if (read.Usage.Plans.Count > MostPlans)
        {
            return string.Create(
                CultureInfo.CurrentCulture,
                $"The file holds {read.Usage.Plans.Count:N0} data plans, more than the {MostPlans} Windsock keeps, so none of it was used.");
        }

        // A name the reader took apart at its commas ("Light, Dark") is a
        // number no choice has, which reading names was meant to rule out.
        if (Undefined(read, string.Empty, depth: 0) is { } where)
        {
            return string.Concat(
                "The file holds a choice Windsock does not know, at ", Spoken(where), ", so none of it was used.");
        }

        foreach (DataPlan plan in read.Usage.Plans)
        {
            if (plan is null)
            {
                return "The file holds an empty data plan, so none of it was used.";
            }

            if (plan.Problems() is { Count: > 0 } problems)
            {
                return string.Concat("A data plan in the file cannot be used (", problems[0], "), so none of it was used.");
            }
        }

        return null;
    }

    /// <summary>
    /// Cuts lists that only grow by hand to what they can hold, and gives every
    /// data plan an id of its own: a blank or repeated one would stop the plan
    /// being remembered, and lead a page to the wrong plan.
    /// </summary>
    private static void Trim(WindsockSettings read)
    {
        DataPlan.Tidy(read.Usage.Plans, _ => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        foreach (DataPlan plan in read.Usage.Plans)
        {
            if (plan.AlertLevels.Count > DataPlan.MostLevels)
            {
                plan.AlertLevels = [.. plan.Levels];
            }
        }

        QuietHoursSettings quiet = read.Alerts.Quiet;

        if (quiet.Days.Distinct().Count() != quiet.Days.Count)
        {
            quiet.Days = [.. quiet.Days.Distinct()];
        }

        Dictionary<string, AlertChoice> choices = read.Alerts.Choices;

        if (choices.Any(pair => pair.Value is null || AlertCatalogue.Find(pair.Key) is null))
        {
            read.Alerts.Choices = choices
                .Where(pair => pair.Value is not null && AlertCatalogue.Find(pair.Key) is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The path of the first value in the settings that is not a value its
    /// enum has, or null when every one is.
    /// </summary>
    private static string? Undefined(object? value, string path, int depth)
    {
        if (value is null || depth > 12)
        {
            return null;
        }

        Type type = value.GetType();

        if (type.IsEnum)
        {
            return Defined(type, value) ? null : path;
        }

        if (value is string || type.IsPrimitive || type.IsValueType)
        {
            return null;
        }

        if (value is IDictionary map)
        {
            foreach (DictionaryEntry entry in map)
            {
                if (Undefined(entry.Value, string.Concat(path, ".", Convert.ToString(entry.Key, CultureInfo.InvariantCulture)), depth + 1) is { } inside)
                {
                    return inside;
                }
            }

            return null;
        }

        if (value is IEnumerable items)
        {
            int index = 0;

            foreach (object? item in items)
            {
                if (Undefined(item, string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]"), depth + 1) is { } inside)
                {
                    return inside;
                }

                index++;
            }

            return null;
        }

        if (type.Namespace?.StartsWith("Windsock", StringComparison.Ordinal) != true)
        {
            return null;
        }

        foreach (PropertyInfo property in Written(type))
        {
            string name = path.Length == 0 ? property.Name : string.Concat(path, ".", property.Name);

            if (Undefined(property.GetValue(value), name, depth + 1) is { } inside)
            {
                return inside;
            }
        }

        return null;
    }

    /// <summary>
    /// The properties a settings type writes and reads back: public, settable,
    /// not ignored. Asked once a type, since a file can hold many places.
    /// </summary>
    private static PropertyInfo[] Written(Type type) => WrittenByType.GetOrAdd(
        type,
        static each => [.. each.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property =>
            property.CanRead
            && property.SetMethod?.IsPublic == true
            && property.GetIndexParameters().Length == 0
            && !property.IsDefined(typeof(JsonIgnoreAttribute)))]);

    /// <summary>The path of the first choice written as more than one name, or null.</summary>
    private static string? Joined(JsonObject body)
    {
        foreach ((string path, Leaf leaf) in Flatten(body))
        {
            if (leaf.Kind == JsonValueKind.String
                && leaf.Text.Contains(',', StringComparison.Ordinal)
                && EnumPaths.Contains(Pattern(path)))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>Whether a value is one its enum has; for flags, made of flags it has.</summary>
    private static bool Defined(Type type, object value)
    {
        if (!type.IsDefined(typeof(FlagsAttribute)))
        {
            return Enum.IsDefined(type, value);
        }

        long every = 0;

        foreach (object each in Enum.GetValues(type))
        {
            every |= Convert.ToInt64(each, CultureInfo.InvariantCulture);
        }

        return (Convert.ToInt64(value, CultureInfo.InvariantCulture) & ~every) == 0;
    }

    /// <summary>
    /// Holds a read file to what the app can use. Numbers out of reach are held
    /// to the nearest sensible value, as the settings themselves do on use.
    /// </summary>
    private static void Hold(WindsockSettings read)
    {
        ConnectionSettings connection = read.Connection;

        connection.CheckEverySeconds = Math.Clamp(connection.CheckEverySeconds, ConnectionSettings.FastestCheck, ConnectionSettings.SlowestCheck);
        connection.OutageAfterSeconds = Math.Clamp(connection.OutageAfterSeconds, ConnectionSettings.ShortestOutage, ConnectionSettings.LongestOutage);

        read.HistoryRowsPerPage = Math.Clamp(read.HistoryRowsPerPage, 1, 1000);
        read.OutageRowsPerPage = Math.Clamp(read.OutageRowsPerPage, 1, 1000);

        ConnectionToolsSettings tools = read.ConnectionTools;
        tools.PlanDownloadMbps = Math.Max(0, tools.PlanDownloadMbps);
        tools.PlanUploadMbps = Math.Max(0, tools.PlanUploadMbps);

        read.Taskbar.Settle();

        if (!string.IsNullOrEmpty(read.Window.OpenOn) && !Navigation.PageLink.IsName(read.Window.OpenOn))
        {
            read.Window.OpenOn = WindowSettings.LastPageChoice;
        }
    }

    /// <summary>
    /// Points the file's data plans at this PC's networks, and says which
    /// plans were not taken.
    /// </summary>
    /// <remarks>
    /// A network's key is the id Windows gave it on the PC that saw it, so
    /// another PC's key means nothing here. A key this PC knows is kept; one it
    /// does not is found by the network's name - and its kind, where both
    /// sides know it - when exactly one network here answers to them; any
    /// other is left out. A plan for chosen networks left with none of them is
    /// not taken at all, since an empty list would make it count every
    /// network. A plan for every network travels as it is.
    /// </remarks>
    private static List<string> PlansHere(WindsockSettings taken, WindsockSettings here, IEnumerable<NetworkTag>? seen)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        List<(string Key, string Name, NetworkKind? Kind)> named = [];

        void Know(string? key, string? name, NetworkKind? kind)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            keys.Add(key);

            if (!string.IsNullOrWhiteSpace(name))
            {
                named.Add((key, name, kind));
            }
        }

        // Exactly one network here with this name, of this kind where both say.
        string? ByName(string? name, NetworkKind? kind)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            string[] found = [.. named
                .Where(each => string.Equals(each.Name, name, StringComparison.Ordinal)
                    && (kind is null || each.Kind is null || each.Kind == kind))
                .Select(each => each.Key)
                .Distinct(StringComparer.Ordinal)
                .Take(2)];

            return found.Length == 1 ? found[0] : null;
        }

        foreach (NetworkTag tag in seen ?? [])
        {
            if (tag is not null)
            {
                Know(tag.Key, tag.Name, tag.Kind);
            }
        }

        foreach (PlanNetwork network in here.Usage.Plans.SelectMany(plan => plan.Networks))
        {
            Know(network.Key, network.Name, network.Kind);
        }

        foreach (NetworkChoice choice in here.Usage.Networks)
        {
            Know(choice.Key, null, null);
        }

        List<DataPlan> kept = [];
        List<string> notes = [];

        foreach (DataPlan plan in taken.Usage.Plans)
        {
            if (plan.CoversEveryNetwork)
            {
                kept.Add(plan);
                continue;
            }

            List<PlanNetwork> networks = [];
            HashSet<string> chosen = new(StringComparer.Ordinal);

            foreach (PlanNetwork network in plan.Networks)
            {
                string? key = keys.Contains(network.Key)
                    ? network.Key
                    : ByName(network.Name, network.Kind);

                if (key is not null && chosen.Add(key))
                {
                    networks.Add(new PlanNetwork { Key = key, Name = network.Name, Kind = network.Kind });
                }
            }

            if (networks.Count == 0)
            {
                notes.Add(string.Concat(
                    "The data plan “", plan.Name, "” is left out: it counts only networks this PC has not used."));
                continue;
            }

            plan.Networks = networks;
            kept.Add(plan);
        }

        taken.Usage.Plans = kept;

        return notes;
    }

    /// <summary>
    /// Empties the meter's places for readings this PC cannot show, as loading
    /// a layout file does, and says which readings went.
    /// </summary>
    private static void Fit(TaskbarSettings meter, Func<MeterField, bool> offered, List<string> notes)
    {
        (int emptied, IReadOnlyList<MeterField> missing) = MeterLayoutFile.Fit(
            new MeterLayout(meter.Rows, meter.Columns, meter.Slots, new MeterLook()),
            offered);

        if (missing.Count == 0)
        {
            return;
        }

        string left = emptied switch
        {
            0 => string.Empty,
            1 => " One place is left empty.",
            _ => string.Create(CultureInfo.CurrentCulture, $" {emptied} places are left empty."),
        };

        notes.Add(string.Concat(
            "Readings this PC cannot show are taken off the meter: ",
            string.Join(", ", missing.Select(MeterReadout.Name)),
            ".",
            left));
    }

    /// <summary>
    /// Says how many of the meter's feed places arrive with no address. A
    /// feed's address is never imported: a place takes the address this PC's
    /// meter already reads in the same place, or none.
    /// </summary>
    private static void FeedsWithoutAddress(TaskbarSettings meter, List<string> notes)
    {
        int bare = meter.Slots.Count(slot =>
            slot.Field == MeterField.Feed && string.IsNullOrWhiteSpace(slot.Feed?.Address));

        if (bare == 0)
        {
            return;
        }

        notes.Add(string.Concat(
            bare == 1 ? "One feed place on the meter arrives" : string.Create(CultureInfo.CurrentCulture, $"{bare} feed places on the meter arrive"),
            " without an address: a feed's address is never imported. Type it on the Taskbar page."));
    }

    /// <summary>
    /// Writes each data plan's network's kind where the settings do not hold
    /// it and this PC's history does.
    /// </summary>
    private static void Kinds(JsonObject body, IEnumerable<NetworkTag>? seen)
    {
        if (seen is null || body["Usage"]?["Plans"] is not JsonArray plans)
        {
            return;
        }

        Dictionary<string, NetworkKind> kinds = new(StringComparer.Ordinal);

        foreach (NetworkTag tag in seen)
        {
            if (tag is not null && !string.IsNullOrEmpty(tag.Key))
            {
                kinds.TryAdd(tag.Key, tag.Kind);
            }
        }

        foreach (JsonNode? plan in plans)
        {
            if (plan?["Networks"] is not JsonArray networks)
            {
                continue;
            }

            foreach (JsonNode? network in networks)
            {
                if (network is JsonObject each
                    && each["Kind"] is null
                    && each["Key"] is JsonValue key
                    && key.TryGetValue(out string? text)
                    && text is not null
                    && kinds.TryGetValue(text, out NetworkKind kind))
                {
                    each["Kind"] = kind.ToString();
                }
            }
        }
    }

    /// <summary>
    /// A copy of the settings sharing nothing with them, for reading a file
    /// against on another thread while the window goes on changing its own.
    /// </summary>
    public static WindsockSettings Snapshot(WindsockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return Whole(settings).Deserialize<WindsockSettings>(Writing) ?? new WindsockSettings();
    }

    /// <summary>The settings as written, every part of them.</summary>
    private static JsonObject Whole(WindsockSettings settings) =>
        JsonSerializer.SerializeToNode(settings, Writing) as JsonObject ?? [];

    /// <summary>The settings as they travel: everything but what stays on this PC.</summary>
    private static JsonObject Tree(WindsockSettings settings)
    {
        JsonObject body = Whole(settings);
        Strip(body);

        return body;
    }

    private static void Strip(JsonObject body)
    {
        foreach (string path in StaysOnThisPc)
        {
            Keep(body, null, path.Split('.'), 0, remove: true);
        }
    }

    /// <summary>
    /// Puts this PC's value at one path of another tree, or, with
    /// <paramref name="remove"/>, takes the path out. A list item is matched to
    /// this PC's item in the same place; nothing is made where the other tree
    /// has no parent for it.
    /// </summary>
    private static void Keep(JsonObject theirs, JsonObject? mine, string[] parts, int at, bool remove = false)
    {
        string part = parts[at];

        if (part.EndsWith(Every, StringComparison.Ordinal))
        {
            string name = part[..^Every.Length];

            if (theirs[name] is not JsonArray items)
            {
                return;
            }

            JsonArray? own = mine?[name] as JsonArray;

            for (int index = 0; index < items.Count; index++)
            {
                if (items[index] is JsonObject item)
                {
                    JsonObject? match = own is not null && index < own.Count ? own[index] as JsonObject : null;
                    Keep(item, match, parts, at + 1, remove);
                }
            }

            return;
        }

        if (at == parts.Length - 1)
        {
            if (!remove && mine is not null && mine.TryGetPropertyValue(part, out JsonNode? value))
            {
                theirs[part] = value?.DeepClone();
            }
            else
            {
                theirs.Remove(part);
            }

            return;
        }

        if (theirs[part] is JsonObject inner)
        {
            Keep(inner, mine?[part] as JsonObject, parts, at + 1, remove);
        }
    }

    /// <summary>Every leaf of the settings as they travel, by its path, said as the change list says it.</summary>
    internal static Dictionary<string, string> Spell(WindsockSettings settings) =>
        Flatten(Tree(settings)).ToDictionary(pair => pair.Key, pair => Words(pair.Key, pair.Value), StringComparer.Ordinal);

    /// <summary>A leaf of the settings: its text and what kind of value it is.</summary>
    private readonly record struct Leaf(string Text, JsonValueKind Kind);

    /// <summary>Every leaf of a tree, by its dotted path.</summary>
    private static Dictionary<string, Leaf> Flatten(JsonNode? node)
    {
        Dictionary<string, Leaf> leaves = new(StringComparer.Ordinal);
        Walk(node, string.Empty, leaves);

        return leaves;
    }

    private static void Walk(JsonNode? node, string path, Dictionary<string, Leaf> leaves)
    {
        switch (node)
        {
            case JsonObject branch:
                foreach ((string key, JsonNode? child) in branch)
                {
                    Walk(child, path.Length == 0 ? key : string.Concat(path, ".", key), leaves);
                }

                break;

            case JsonArray list:
                leaves[path + ".Count"] = new Leaf(list.Count.ToString(CultureInfo.InvariantCulture), JsonValueKind.Number);

                for (int index = 0; index < list.Count; index++)
                {
                    Walk(list[index], string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]"), leaves);
                }

                break;

            case null:
                leaves[path] = new Leaf(string.Empty, JsonValueKind.Null);
                break;

            default:
                JsonValue value = node.AsValue();
                JsonValueKind kind = value.GetValueKind();

                // A string's own text, never its JSON: no quotes, no escapes.
                string text = kind switch
                {
                    JsonValueKind.String => value.GetValue<string>(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => value.ToJsonString(),
                };

                leaves[path] = new Leaf(text, kind);
                break;
        }
    }

    /// <summary>A stored value as a person would say it.</summary>
    private static string Words(string path, Leaf leaf) => leaf.Kind switch
    {
        JsonValueKind.True => "On",
        JsonValueKind.False => "Off",
        _ when string.IsNullOrEmpty(leaf.Text) => "None",
        JsonValueKind.String when EnumPaths.Contains(Pattern(path)) => Spoken(leaf.Text),
        _ => leaf.Text,
    };

    /// <summary>The names of some data plans, as one line.</summary>
    private static string PlanNames(List<DataPlan> plans)
    {
        const int Named = 4;

        if (plans.Count == 0)
        {
            return "None";
        }

        string named = string.Join(", ", plans.Take(Named).Select(plan => plan.Name));

        return plans.Count <= Named
            ? named
            : string.Create(CultureInfo.CurrentCulture, $"{named} and {plans.Count - Named:N0} more");
    }

    /// <summary>A leaf's path with every list position written as <c>[*]</c> and every map key as <c>*</c>.</summary>
    private static string Pattern(string path)
    {
        string listed = Listed(path);

        foreach (string map in MapPaths)
        {
            if (listed.Length > map.Length + 1
                && listed.StartsWith(map, StringComparison.Ordinal)
                && listed[map.Length] == '.')
            {
                int end = listed.IndexOfAny(['.', '['], map.Length + 1);

                return string.Concat(map, ".*", end < 0 ? string.Empty : listed[end..]);
            }
        }

        return listed;
    }

    /// <summary>A path with every list position written as <c>[*]</c>.</summary>
    private static string Listed(string path)
    {
        if (!path.Contains('[', StringComparison.Ordinal))
        {
            return path;
        }

        StringBuilder pattern = new(path.Length);
        int index = 0;

        while (index < path.Length)
        {
            char each = path[index];

            if (each == '[')
            {
                int close = path.IndexOf(']', index);

                if (close > index)
                {
                    pattern.Append(Every);
                    index = close + 1;
                    continue;
                }
            }

            pattern.Append(each);
            index++;
        }

        return pattern.ToString();
    }

    /// <summary>Every path the settings write an enum's name at, found from their types.</summary>
    private static HashSet<string> FindEnums()
    {
        HashSet<string> found = new(StringComparer.Ordinal);
        Members(typeof(WindsockSettings), string.Empty, found, depth: 0);

        return found;
    }

    private static void Members(Type type, string path, HashSet<string> found, int depth)
    {
        if (depth > 8)
        {
            return;
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead
                || property.GetIndexParameters().Length != 0
                || property.IsDefined(typeof(JsonIgnoreAttribute)))
            {
                continue;
            }

            string name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            Kinds(property.PropertyType, path.Length == 0 ? name : string.Concat(path, ".", name), found, depth);
        }
    }

    private static void Kinds(Type type, string path, HashSet<string> found, int depth)
    {
        Type plain = Nullable.GetUnderlyingType(type) ?? type;

        if (plain.IsEnum)
        {
            found.Add(path);
            return;
        }

        if (plain == typeof(string) || plain.IsPrimitive)
        {
            return;
        }

        Type? map = plain.IsGenericType && plain.GetGenericTypeDefinition() == typeof(IDictionary<,>)
            ? plain
            : Array.Find(plain.GetInterfaces(), each => each.IsGenericType && each.GetGenericTypeDefinition() == typeof(IDictionary<,>));

        if (map is not null)
        {
            found.Add(MapMark + path);
            Kinds(map.GetGenericArguments()[1], path + ".*", found, depth + 1);
            return;
        }

        Type? item = plain.IsGenericType && plain.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? plain
            : Array.Find(plain.GetInterfaces(), each => each.IsGenericType && each.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (item is not null)
        {
            Kinds(item.GetGenericArguments()[0], path + Every, found, depth + 1);
            return;
        }

        if (plain.Namespace?.StartsWith("Windsock", StringComparison.Ordinal) == true)
        {
            Members(plain, path, found, depth + 1);
        }
    }

    /// <summary>
    /// Spaces a name written in capitals: <c>KeepRunning</c> becomes
    /// "Keep running", and a dotted path takes the arrow Settings uses.
    /// </summary>
    private static string Spoken(string name)
    {
        if (name.Length == 0 || !name.Any(char.IsAsciiLetterUpper) || name.Contains(' ', StringComparison.Ordinal))
        {
            return name;
        }

        StringBuilder spoken = new(name.Length + 8);

        for (int index = 0; index < name.Length; index++)
        {
            char each = name[index];

            if (each == '.')
            {
                spoken.Append(" › ");
                continue;
            }

            bool word = index > 0
                && char.IsAsciiLetterUpper(each)
                && name[index - 1] != '.'
                && !char.IsAsciiLetterUpper(name[index - 1]);

            if (word)
            {
                spoken.Append(' ').Append(char.ToLowerInvariant(each));
                continue;
            }

            spoken.Append(each);
        }

        return spoken.ToString();
    }

    /// <summary>
    /// Where in the file a reading failed. The reader's own path starts again
    /// inside a part read by a converter of its own - the meter's places say
    /// <c>$.Feed</c> - so the part is found by reading the file's parts one at
    /// a time, down to the smallest that will not read.
    /// </summary>
    private static string Where(JsonObject body, JsonException error)
    {
        string? found = Locate(body, typeof(WindsockSettings), string.Empty, depth: 0);

        if (!string.IsNullOrEmpty(found))
        {
            return string.Concat("The trouble is at ", Spoken(found), ".");
        }

        return string.IsNullOrEmpty(error.Path)
            ? "It may have been changed by hand."
            : string.Concat("The trouble is at ", error.Path, ".");
    }

    /// <summary>
    /// The path of the smallest part of an object that does not read as its
    /// type, or null when every part does on its own. Each part is read inside
    /// a copy of its parent holding it alone, so a part that has a converter
    /// of its own is read with it.
    /// </summary>
    private static string? Locate(JsonObject node, Type type, string path, int depth)
    {
        if (depth > 12)
        {
            return null;
        }

        foreach (PropertyInfo property in Written(type))
        {
            if (!node.TryGetPropertyValue(property.Name, out JsonNode? part))
            {
                continue;
            }

            string here = path.Length == 0 ? property.Name : string.Concat(path, ".", property.Name);

            if (Reads(type, property.Name, part))
            {
                continue;
            }

            Type plain = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (part is JsonObject inner && plain.IsClass && plain != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(plain))
            {
                return Locate(inner, plain, here, depth + 1) ?? here;
            }

            if (part is JsonArray items && plain.IsGenericType && plain.GetGenericArguments() is [{ } item])
            {
                for (int index = 0; index < items.Count; index++)
                {
                    JsonArray alone = [];
                    alone.Add(items[index]?.DeepClone());

                    if (Reads(type, property.Name, alone))
                    {
                        continue;
                    }

                    string at = string.Create(CultureInfo.InvariantCulture, $"{here}[{index}]");

                    return items[index] is JsonObject element && item.IsClass && item != typeof(string)
                        ? Locate(element, item, at, depth + 1) ?? at
                        : at;
                }
            }

            return here;
        }

        return null;
    }

    /// <summary>Whether one part reads, inside a copy of its parent holding it alone.</summary>
    private static bool Reads(Type parent, string name, JsonNode? part)
    {
        try
        {
            _ = new JsonObject { [name] = part?.DeepClone() }.Deserialize(parent, Reading);
            return true;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException)
        {
            return false;
        }
    }
}
