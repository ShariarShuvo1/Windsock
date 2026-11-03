using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Windsock.Core.Connectivity;
using Windsock.Core.Formatting;
using Windsock.Core.Networking;
using Windsock.Core.Plans;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class SettingsTransferTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Written = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static WindsockSettings Changed()
    {
        WindsockSettings settings = new()
        {
            Theme = ThemePreference.Dark,
            OnClose = CloseAction.KeepRunning,
            SidebarOpen = false,
            AppsShowProcesses = true,
            CheckForUpdates = false,
            RecordHistory = false,
            KeepMinuteDetailDays = 90,
            HistoryRowsPerPage = 250,
            OutageRowsPerPage = 50,
            TraceRoutes = true,
            ReverseLookups = true,
            ChartFamily = RateFamily.Bits,
            ChartScale = RateScale.Mega,
        };

        settings.Notifications.Enabled = false;
        settings.Notifications.Sound = false;
        settings.Connection.InternetCheck = InternetCheck.Custom;
        settings.Connection.CustomTarget = "example.com";
        settings.Connection.CheckEverySeconds = 10;
        settings.Connection.NotifyNetworkChanges = true;
        settings.Usage.Backup.Keep = 6;
        settings.Usage.Digest.Enabled = true;
        settings.Usage.Plans = [new DataPlan { Name = "Home broadband", LimitBytes = 500_000_000_000 }];

        return settings;
    }

    private static string Json(WindsockSettings settings)
    {
        WindsockSettings copy = new();
        copy.CopyFrom(settings);
        copy.Adapters = [];

        return JsonSerializer.Serialize(copy, Written);
    }

    [Fact]
    public void TheWindowsPlaces_StayOnThisMachine_AndTheChoiceOfPageTravels()
    {
        WindsockSettings there = new();
        there.Window.OpenOn = "history";
        there.Window.LastPage = "map";
        there.Window.Remember(new WindowPlace { Monitor = @"\\.\DISPLAY2", Left = 3000, Width = 900, Height = 700 });
        there.Appearance.Density = Density.Compact;

        string file = SettingsTransfer.Export(there, Now);

        Assert.DoesNotContain("DISPLAY2", file, StringComparison.Ordinal);
        Assert.DoesNotContain("LastPage", file, StringComparison.Ordinal);

        WindsockSettings here = new();
        here.Window.LastPage = "outages";
        here.Window.Remember(new WindowPlace { Monitor = @"\\.\DISPLAY1", Width = 1100, Height = 760 });

        SettingsImport read = SettingsTransfer.Read(file, here);

        Assert.True(read.IsValid, read.Problem);
        Assert.Equal("history", read.Settings!.Window.OpenOn);
        Assert.Equal("outages", read.Settings.Window.LastPage);
        Assert.Equal(@"\\.\DISPLAY1", Assert.Single(read.Settings.Window.Places).Monitor);
        Assert.Equal(Density.Compact, read.Settings.Appearance.Density);
        Assert.Contains(read.Changes, change => change.Name == "Open the window on");
        Assert.Contains(read.Changes, change => change.Name == "Density" && change.Becomes == "Compact");
        Assert.DoesNotContain(read.Changes, change => change.Name.Contains("Places", StringComparison.Ordinal));
    }

    [Fact]
    public void AChoiceOfPageThatCannotBeOne_IsReadAsTheLastPage()
    {
        string file = SettingsTransfer.Export(new WindsockSettings(), Now)
            .Replace("\"OpenOn\": \"\"", "\"OpenOn\": \"C:\\\\Windows\"", StringComparison.Ordinal);

        Assert.Contains("C:\\\\Windows", file, StringComparison.Ordinal);

        SettingsImport read = SettingsTransfer.Read(file, new WindsockSettings());

        Assert.True(read.IsValid, read.Problem);
        Assert.Equal(WindowSettings.LastPageChoice, read.Settings!.Window.OpenOn);
    }

    [Fact]
    public void AnExport_ReadsBackAsTheSameChoices()
    {
        WindsockSettings mine = Changed();

        SettingsImport read = SettingsTransfer.Read(SettingsTransfer.Export(mine, Now), new WindsockSettings());

        Assert.True(read.IsValid, read.Problem);
        Assert.Equal(Json(mine), Json(read.Settings!));
    }

    [Fact]
    public void AnExport_SaysWhatItIs()
    {
        string file = SettingsTransfer.Export(new WindsockSettings(), Now);

        using JsonDocument document = JsonDocument.Parse(file);

        Assert.Equal(SettingsTransfer.FormatName, document.RootElement.GetProperty("Format").GetString());
        Assert.Equal(SettingsTransfer.FormatVersion, document.RootElement.GetProperty("Version").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("Settings", out _));
    }

    /// <summary>
    /// Adapters are named by this machine's interface ids. Carried to another PC
    /// they would narrow the meter to hardware that is not there.
    /// </summary>
    [Fact]
    public void TheAdapters_StayOnTheirOwnMachine()
    {
        WindsockSettings there = new() { Adapters = [11, 12] };
        WindsockSettings here = new() { Adapters = [42] };

        string file = SettingsTransfer.Export(there, Now);

        Assert.DoesNotContain("Adapters", file, StringComparison.Ordinal);

        SettingsImport read = SettingsTransfer.Read(file, here);

        Assert.Equal([42UL], read.Settings!.Adapters);
    }

    [Fact]
    public void AFileHoldingAdaptersAnyway_DoesNotBringThem()
    {
        string file = """
            { "Format": "Windsock settings", "Version": 1, "Settings": { "Adapters": [ 7 ], "Theme": "Light" } }
            """;

        SettingsImport read = SettingsTransfer.Read(file, new WindsockSettings { Adapters = [3] });

        Assert.True(read.IsValid, read.Problem);
        Assert.Equal([3UL], read.Settings!.Adapters);
        Assert.Equal(ThemePreference.Light, read.Settings.Theme);
    }

    [Fact]
    public void TheChanges_AreNamedAsSettingsNamesThem()
    {
        WindsockSettings mine = new();
        WindsockSettings theirs = new() { Theme = ThemePreference.Dark, OnClose = CloseAction.KeepRunning };

        SettingsImport read = SettingsTransfer.Read(SettingsTransfer.Export(theirs, Now), mine);

        Assert.Contains(read.Changes, change => change is { Name: "Theme", Was: "System", Becomes: "Dark" });
        Assert.Contains(read.Changes, change => change is { Name: "When the window is closed", Was: "Quit", Becomes: "Keep running" });
        Assert.Equal(2, read.Changes.Count);
    }

    [Fact]
    public void TheSameSettings_ChangeNothing()
    {
        WindsockSettings mine = Changed();

        SettingsImport read = SettingsTransfer.Read(SettingsTransfer.Export(mine, Now), mine);

        Assert.True(read.IsValid, read.Problem);
        Assert.Empty(read.Changes);
        Assert.Empty(read.Notes);
    }

    [Fact]
    public void OnAndOff_AreSaidInWords()
    {
        WindsockSettings theirs = new() { AppsShowProcesses = true };

        SettingsImport read = SettingsTransfer.Read(SettingsTransfer.Export(theirs, Now), new WindsockSettings());

        Assert.Contains(read.Changes, change => change is { Name: "Apps page shows processes", Was: "Off", Becomes: "On" });
    }

    /// <summary>
    /// A value is said as it was written: only an enum's name is spaced into
    /// words. Free text keeps its capitals and its quotes, with no JSON escapes
    /// around it, and a number is a number.
    /// </summary>
    [Fact]
    public void Values_AreSaidAsWritten_AndOnlyChoicesAreSpaced()
    {
        WindsockSettings settings = new() { OnClose = CloseAction.KeepRunning };
        settings.Usage.Plans =
        [
            new DataPlan
            {
                Name = "My \"FastNet\" PlanB\\Home",
                LimitBytes = 1_048_576,
                Cycle = PlanCycle.OneOff,
                OneOffStart = Now,
                OneOffEnd = Now.AddDays(30),
            },
        ];

        Dictionary<string, string> said = SettingsTransfer.Spell(settings);

        Assert.Equal("Keep running", said["OnClose"]);
        Assert.Equal("My \"FastNet\" PlanB\\Home", said["Usage.Plans[0].Name"]);
        Assert.Equal("1048576", said["Usage.Plans[0].LimitBytes"]);
        Assert.Equal("One off", said["Usage.Plans[0].Cycle"]);
        Assert.Equal("Sunday", said["Alerts.Quiet.Days[0]"]);
        Assert.Equal("On", said["SidebarOpen"]);
        Assert.Equal("None", said["Window.OpenOn"]);
    }

    [Fact]
    public void ADifferentMeter_IsOneLine()
    {
        WindsockSettings theirs = new();
        theirs.Taskbar.Enabled = !theirs.Taskbar.Enabled;
        theirs.Taskbar.Rows = theirs.Taskbar.Rows == 1 ? 2 : 1;

        SettingsImport read = SettingsTransfer.Read(SettingsTransfer.Export(theirs, Now), new WindsockSettings());

        Assert.Single(read.Changes, change => change.Name == "Taskbar meter");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ this is not json")]
    [InlineData("[ 1, 2, 3 ]")]
    [InlineData("""{ "Theme": "Dark" }""")]
    [InlineData("""{ "Format": "Something else", "Version": 1, "Settings": { } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Settings": { } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1 }""")]
    public void AFileThatIsNotOne_IsRefused(string text)
    {
        SettingsImport read = SettingsTransfer.Read(text, new WindsockSettings());

        Assert.False(read.IsValid);
        Assert.NotEmpty(read.Problem);
        Assert.Empty(read.Changes);
    }

    [Fact]
    public void AFileFromANewerWindsock_IsRefusedWithTheReason()
    {
        SettingsImport read = SettingsTransfer.Read(
            """{ "Format": "Windsock settings", "Version": 99, "Settings": { } }""",
            new WindsockSettings());

        Assert.False(read.IsValid);
        Assert.Contains("newer", read.Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The settings store accepts a number for a choice, which can name one
    /// that does not exist. An import is stricter: it would be replacing
    /// settings somebody chose, so it takes names or nothing.
    /// </summary>
    [Theory]
    [InlineData("""{ "Theme": 7 }""")]
    [InlineData("""{ "Theme": "Sepia" }""")]
    [InlineData("""{ "Appearance": { "Density": "Roomy" } }""")]
    [InlineData("""{ "HistoryRowsPerPage": "a while" }""")]
    [InlineData("""{ "Usage": { "Digest": { "WeekStarts": 3 } } }""")]
    public void AValueWindsockDoesNotKnow_RefusesTheWholeFile(string body)
    {
        WindsockSettings mine = Changed();
        string before = Json(mine);

        SettingsImport read = SettingsTransfer.Read(
            string.Concat("""{ "Format": "Windsock settings", "Version": 1, "Settings": """, body, " }"),
            mine);

        Assert.False(read.IsValid);
        Assert.Null(read.Settings);
        Assert.Equal(before, Json(mine));
    }

    /// <summary>A data plan Windsock could not count is refused, not mended behind the person's back.</summary>
    [Fact]
    public void ADataPlanThatCannotBeCounted_RefusesTheWholeFile()
    {
        WindsockSettings mine = Changed();
        string before = Json(mine);

        SettingsImport read = SettingsTransfer.Read(
            """{ "Format": "Windsock settings", "Version": 1, "Settings": { "Theme": "Light", "Usage": { "Plans": [ { "Name": "Broken", "Cycle": "OneOff" } ] } } }""",
            mine);

        Assert.False(read.IsValid);
        Assert.Contains("data plan", read.Problem, StringComparison.Ordinal);
        Assert.Equal(before, Json(mine));
    }

    /// <summary>
    /// An address to ping is a contact from this machine, so it is this
    /// machine's: a file's, valid or not, is never read.
    /// </summary>
    [Fact]
    public void AnAddressToPingInAFile_IsNeverTaken()
    {
        WindsockSettings mine = new();

        SettingsImport read = SettingsTransfer.Read(
            """{ "Format": "Windsock settings", "Version": 1, "Settings": { "Connection": { "InternetCheck": "Custom", "CustomTarget": "http://evil/?" } } }""",
            mine);

        Assert.True(read.IsValid, read.Problem);
        Assert.Equal(InternetCheck.WindowsOnly, read.Settings!.Connection.InternetCheck);
        Assert.Equal(string.Empty, read.Settings.Connection.CustomTarget);
        Assert.Empty(read.Changes);
    }

    [Fact]
    public void NumbersOutOfReach_AreHeldToWhatIsSensible()
    {
        SettingsImport read = SettingsTransfer.Read(
            """
            { "Format": "Windsock settings", "Version": 1,
              "Settings": { "HistoryRowsPerPage": 0,
                            "ConnectionTools": { "PlanDownloadMbps": -20 },
                            "Connection": { "CheckEverySeconds": 0, "OutageAfterSeconds": 100000 } } }
            """,
            new WindsockSettings());

        Assert.True(read.IsValid, read.Problem);
        WindsockSettings settings = read.Settings!;
        Assert.Equal(1, settings.HistoryRowsPerPage);
        Assert.Equal(0d, settings.ConnectionTools.PlanDownloadMbps);
        Assert.Equal(ConnectionSettings.FastestCheck, settings.Connection.CheckEverySeconds);
        Assert.Equal(ConnectionSettings.LongestOutage, settings.Connection.OutageAfterSeconds);
    }

    [Fact]
    public void ATooLargeFile_IsRefusedUnread()
    {
        string huge = new(' ', SettingsTransfer.LargestFile + 1);

        Assert.False(SettingsTransfer.Read(huge, new WindsockSettings()).IsValid);
    }

    [Fact]
    public void Reading_ChangesNothingInUse()
    {
        WindsockSettings mine = new();
        string before = Json(mine);

        _ = SettingsTransfer.Read(SettingsTransfer.Export(Changed(), Now), mine);

        Assert.Equal(before, Json(mine));
    }

    /// <summary>
    /// The import dialog lists so many changes one by one, then says how many
    /// more there are: a file that touches everything builds no more rows.
    /// </summary>
    [Fact]
    public void TheList_HasRoomForSoMany_AndCountsTheRest()
    {
        SettingChange[] many = [.. Enumerable.Range(0, SettingsTransfer.MostListed + 37)
            .Select(each => new SettingChange(each.ToString(CultureInfo.InvariantCulture), "Off", "On"))];

        SettingChangeListing listing = SettingsTransfer.Listing(many);

        Assert.Equal(SettingsTransfer.MostListed, listing.Shown.Count);
        Assert.Equal(37, listing.More);
        Assert.Equal("…and 37 more.", listing.MoreText);

        SettingChangeListing few = SettingsTransfer.Listing(many[..SettingsTransfer.MostListed]);

        Assert.Equal(0, few.More);
        Assert.Equal(string.Empty, few.MoreText);
    }

    /// <summary>
    /// A change is named for a screen reader by its ToString: in words, not
    /// the record's list of members.
    /// </summary>
    [Fact]
    public void AChange_IsSaidInWords()
    {
        Assert.Equal("Theme: Dark to Light", new SettingChange("Theme", "Dark", "Light").ToString());
    }

    /// <summary>
    /// The data plans travel, with the digest and how often to export. This
    /// PC's names for its networks, the folder it exports to and how many
    /// copies of its history it keeps do not. An earlier cut listed every one
    /// of these as a change and copied none.
    /// </summary>
    [Fact]
    public void Usage_Travels_ButNotThisPcsNetworksOrExportFolder()
    {
        WindsockSettings there = new();
        there.Usage.Plans = [new DataPlan { Name = "Home broadband", LimitBytes = 500_000_000_000 }];
        there.Usage.Backup.Keep = 5;
        there.Usage.Digest.Enabled = true;
        there.Usage.AutoExport = new AutoExportSettings { Enabled = true, Folder = @"D:\ThereExports", Every = ExportEvery.Month, Format = ExportFormat.Json };
        there.Usage.Networks = [new NetworkChoice { Key = "there-key", Label = "ThereLabel", Hotspot = true }];

        WindsockSettings here = new();
        here.Usage.AutoExport = new AutoExportSettings { Folder = @"C:\HereExports" };
        here.Usage.Networks = [new NetworkChoice { Key = "here-key", Label = "Upstairs" }];

        string file = SettingsTransfer.Export(there, Now);

        Assert.DoesNotContain("ThereExports", file, StringComparison.Ordinal);
        Assert.DoesNotContain("ThereLabel", file, StringComparison.Ordinal);

        SettingsImport read = SettingsTransfer.Read(file, here);

        Assert.True(read.IsValid, read.Problem);
        Assert.Contains(read.Changes, change => change is { Name: "Data plans", Was: "None", Becomes: "Home broadband" });
        Assert.Contains(read.Changes, change => change is { Name: "Weekly digest", Was: "Off", Becomes: "On" });
        Assert.DoesNotContain(read.Changes, change => change.Name.Contains("cop", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(read.Changes, change => change is { Name: "How often to export", Was: "Day", Becomes: "Month" });
        Assert.DoesNotContain(read.Changes, change => change.Name.Contains("Folder", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(read.Changes, change => change.Name.Contains("Networks", StringComparison.OrdinalIgnoreCase));

        here.CopyFrom(read.Settings!);

        Assert.Equal("Home broadband", Assert.Single(here.Usage.Plans).Name);
        Assert.Equal(3, here.Usage.Backup.Keep);
        Assert.True(here.Usage.Digest.Enabled);
        Assert.Equal(ExportEvery.Month, here.Usage.AutoExport.Every);
        Assert.Equal(ExportFormat.Json, here.Usage.AutoExport.Format);
        Assert.False(here.Usage.AutoExport.Enabled);
        Assert.Equal(@"C:\HereExports", here.Usage.AutoExport.Folder);
        Assert.Equal("Upstairs", Assert.Single(here.Usage.Networks).Label);
    }

    /// <summary>
    /// A network's key is the id Windows gave it on the PC that saw it. A plan
    /// keeps the networks this PC knows by key, finds the others by name when
    /// one network here has it, and a plan left with none is not taken - with
    /// an empty list it would count every network.
    /// </summary>
    [Fact]
    public void APlansNetworks_AreFoundHere_ByKeyOrName_OrThePlanIsLeftOut()
    {
        WindsockSettings there = new();
        there.Usage.Plans =
        [
            new DataPlan { Name = "Everywhere" },
            new DataPlan { Name = "Phone", Networks = [new PlanNetwork { Key = "there-hotspot", Name = "Galaxy A54" }] },
            new DataPlan { Name = "Office", Networks = [new PlanNetwork { Key = "known-key", Name = "Office" }] },
            new DataPlan { Name = "Cafe plan", Networks = [new PlanNetwork { Key = "there-cafe", Name = "Cafe" }] },
            new DataPlan { Name = "Twin plan", Networks = [new PlanNetwork { Key = "there-twin", Name = "Twin" }] },
        ];

        NetworkTag[] seen =
        [
            new("here-hotspot", "Galaxy A54", NetworkKind.WiFi),
            new("known-key", "Office", NetworkKind.Ethernet),
            new("twin-1", "Twin", NetworkKind.WiFi),
            new("twin-2", "Twin", NetworkKind.WiFi),
        ];

        SettingsImport read = SettingsTransfer.Read(SettingsTransfer.Export(there, Now), new WindsockSettings(), seen);

        Assert.True(read.IsValid, read.Problem);
        List<DataPlan> plans = read.Settings!.Usage.Plans;

        Assert.Equal(["Everywhere", "Phone", "Office"], plans.Select(plan => plan.Name));
        Assert.True(plans[0].CoversEveryNetwork);
        Assert.Equal("here-hotspot", Assert.Single(plans[1].Networks).Key);
        Assert.Equal("known-key", Assert.Single(plans[2].Networks).Key);

        Assert.Equal(2, read.Notes.Count);
        Assert.Contains(read.Notes, note => note.Contains("Cafe plan", StringComparison.Ordinal));
        Assert.Contains(read.Notes, note => note.Contains("Twin plan", StringComparison.Ordinal));
        Assert.Contains(read.Changes, change => change is { Name: "Data plans", Becomes: "Everywhere, Phone, Office" });
    }

    /// <summary>
    /// Everything under Data and privacy that reaches beyond this PC or keeps a
    /// record of it is this PC's: an import never switches it on, never
    /// switches it off, and never lists it as a change.
    /// </summary>
    [Fact]
    public void ThisPcsContactsAndRecords_AreNeitherTakenNorListed()
    {
        static WindsockSettings Opposite(bool on)
        {
            WindsockSettings settings = new()
            {
                CheckForUpdates = !on,
                TraceRoutes = on,
                ReverseLookups = on,
                RecordHistory = !on,
                KeepMinuteDetailDays = on ? 7 : 0,
            };

            settings.Connection.InternetCheck = on ? InternetCheck.Custom : InternetCheck.WindowsOnly;
            settings.Connection.CustomTarget = on ? "203.0.113.9" : string.Empty;
            settings.ConnectionTools.PublicAddressLookups = on;
            settings.ConnectionTools.PublicAddressService = on ? PublicAddressService.Custom : PublicAddressService.Ipify;
            settings.ConnectionTools.PublicAddressCustomUrl = on ? "https://there.example/ip" : string.Empty;
            settings.ConnectionTools.DnsTiming = on;
            settings.ConnectionTools.StunTest = on;
            settings.ConnectionTools.ScheduledSpeedTests = on;
            settings.ConnectionTools.MeasurementLabConsentAt = on ? Now : null;
            settings.Insight.SpamhausDrop = on;
            settings.Insight.FeodoTracker = on;
            settings.Insight.Timeline = on;
            settings.Home.FindDevices = on;
            settings.Home.CheckRouter = on;
            settings.Home.ReadRouterTraffic = on;
            settings.Feed.Enabled = on;
            settings.Feed.Port = on ? 41000 : FeedSettings.DefaultPort;
            settings.Taskbar.Feeds = on;
            settings.Taskbar.RefreshPublicAddress = on;
            settings.Taskbar.Monitors = on ? MeterMonitors.Chosen : MeterMonitors.Main;
            settings.Taskbar.ChosenMonitor = on ? @"\\.\DISPLAY3" : null;
            settings.Usage.Backup.Enabled = !on;
            settings.Usage.Backup.Keep = on ? 1 : 3;
            settings.Alerts.KeepDays = on ? 7 : 90;

            return settings;
        }

        foreach (bool hereOn in (bool[])[false, true])
        {
            WindsockSettings here = Opposite(hereOn);
            WindsockSettings there = Opposite(!hereOn);
            string before = JsonSerializer.Serialize(here, Written);

            string file = SettingsTransfer.Export(there, Now);

            Assert.DoesNotContain("there.example", file, StringComparison.Ordinal);
            Assert.DoesNotContain("203.0.113.9", file, StringComparison.Ordinal);

            SettingsImport read = SettingsTransfer.Read(file, here);

            Assert.True(read.IsValid, read.Problem);
            Assert.Empty(read.Changes);
            Assert.Empty(SettingsTransfer.Compare(here, there));

            here.CopyFrom(there);

            Assert.Equal(before, JsonSerializer.Serialize(here, Written));
            Assert.Equal(hereOn, here.ConnectionTools.PublicAddressLookups);
            Assert.Equal(hereOn ? "https://there.example/ip" : string.Empty, here.ConnectionTools.PublicAddressCustomUrl);
            Assert.Equal(hereOn, here.Feed.Enabled);
            Assert.Equal(hereOn, here.Insight.Timeline);
        }
    }

    /// <summary>
    /// Every path the list names must be somewhere in the settings: a setting
    /// renamed under the list would otherwise quietly start to travel.
    /// </summary>
    [Fact]
    public void EveryPathThatStays_NamesASetting()
    {
        WindsockSettings settings = new();
        settings.Taskbar.Slots = [new MeterSlot { Field = MeterField.Feed, Feed = new MeterFeed { Address = "https://example.com" } }];

        HashSet<string> paths = [.. Leaves(Whole(settings)).Keys.SelectMany(Prefixes)];

        foreach (string path in SettingsTransfer.StaysOnThisPc)
        {
            Assert.True(paths.Contains(path), string.Concat(path, " is not a setting"));
        }
    }

    /// <summary>
    /// The one list decides all three: what is exported, what the change list
    /// shows, and what <see cref="WindsockSettings.CopyFrom"/> keeps. Settings
    /// differing from the defaults only in what stays are exported without
    /// it, read as no change, and copied as nothing.
    /// </summary>
    [Fact]
    public void TheExport_TheChanges_AndTheCopy_AgreeOnWhatStays()
    {
        WindsockSettings flipped = Flipped();

        // The defaults, with only what stays on this PC taken from the flipped settings.
        WindsockSettings staying = Clone(flipped);
        staying.CopyFrom(new WindsockSettings());

        Dictionary<string, string> defaults = Leaves(Whole(new WindsockSettings()));
        Assert.Contains(Leaves(Whole(staying)), pair => defaults.GetValueOrDefault(pair.Key) != pair.Value);

        Assert.Empty(SettingsTransfer.Compare(new WindsockSettings(), staying));

        string file = SettingsTransfer.Export(staying, Now);
        JsonObject body = (JsonObject)JsonNode.Parse(file)!["Settings"]!;

        Assert.DoesNotContain(Leaves(body).Keys, Stays);

        SettingsImport read = SettingsTransfer.Read(file, new WindsockSettings());

        Assert.True(read.IsValid, read.Problem);
        Assert.Empty(read.Changes);
    }

    /// <summary>
    /// Every choice a person can make has to cross with an import, in every
    /// group and however deep, and nothing on the list of what stays. A setting
    /// added later and missed would silently stay behind; one listed as a change
    /// but never copied - the data plans were - would be agreed to and not
    /// happen. This finds both.
    /// </summary>
    [Fact]
    public void CopyFrom_TakesEveryLeafOfEveryGroup_ButWhatStays()
    {
        WindsockSettings source = Flipped();
        WindsockSettings target = new() { Adapters = [9] };
        target.Alerts.Forward = new ForwardSettings { Enabled = true, NtfyTopic = "mine" };

        Dictionary<string, string> defaults = Leaves(Whole(new WindsockSettings()));
        Dictionary<string, string> given = Leaves(Whole(source));
        Dictionary<string, string> before = Leaves(Whole(target));

        // Every group was given something to carry, at the deepest level it has.
        foreach (PropertyInfo property in typeof(WindsockSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            string group = property.Name;

            Assert.True(
                given.Any(pair => Under(pair.Key, group) && defaults.GetValueOrDefault(pair.Key) != pair.Value),
                string.Concat(group, " was not changed by the test"));
        }

        target.CopyFrom(source);

        Dictionary<string, string> after = Leaves(Whole(target));

        foreach ((string path, string value) in given)
        {
            if (Stays(path))
            {
                Assert.True(
                    before.TryGetValue(path, out string? kept) ? after.GetValueOrDefault(path) == kept : after.GetValueOrDefault(path) != value,
                    string.Concat(path, " stays on this PC but was taken"));
                continue;
            }

            Assert.True(after.TryGetValue(path, out string? copied), string.Concat(path, " was not copied"));
            Assert.True(copied == value, string.Concat(path, " was not copied: ", copied, " for ", value));
        }

        Assert.Equal([9UL], target.Adapters);
        Assert.Equal("mine", target.Alerts.Forward.NtfyTopic);
        Assert.NotSame(source.Usage, target.Usage);
        Assert.NotSame(source.Taskbar, target.Taskbar);
    }

    private static string Envelope(string body) =>
        string.Concat("""{ "Format": "Windsock settings", "Version": 1, "Settings": """, body, " }");

    /// <summary>
    /// Whatever a file holds, reading it answers: read, or refused. A key
    /// written twice, a lone surrogate, a null where a list or a group belongs
    /// once threw out of the import and closed Windsock. And what is read can
    /// be taken and saved without throwing either.
    /// </summary>
    [Theory]
    [InlineData("""{ "Format": "Windsock settings", "Format": "x", "Version": 1, "Settings": { } }""")]
    [InlineData("""{ "Format": "\uD800", "Version": 1, "Settings": { } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Theme": "Dark", "Theme": "Light" } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Taskbar": { "Enabled": true, "Enabled": false } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Window": { "Places": [], "Places": [] } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Window": { "OpenOn": "\uD800" } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "\uD800": 1 } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Taskbar": { "\uDC00x": 1 } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Taskbar": { "Slots": [ { "Field": "Download", "Card": "\uD800" } ] } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Taskbar": { "Slots": [ "\uD800" ] } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Usage": { "Plans": [ { "Name": "\uD800" } ] } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Alerts": { "Choices": { "\uD800": { } } } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Connection": { "CustomTarget": "\uD800" } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Usage": { "Plans": [ { "Name": "p", "OffPeak": null } ] } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Usage": { "Plans": [ { "Name": "p", "Networks": null } ] } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Usage": { "Plans": [ { "Name": "p", "Networks": [ null ] } ] } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Usage": { "Plans": [ { "Name": "p", "Networks": [ { "Key": null, "Name": null } ] } ] } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Usage": { "Plans": [ { "Name": "p", "AlertLevels": null } ] } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Usage": { "Plans": [ { "Name": "p", "Id": null } ] } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Usage": { "Plans": [ null ] } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Usage": { "Plans": { } } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Usage": 5 } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Taskbar": "x" } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Alerts": { "Choices": [ 1 ] } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Alerts": { "Choices": { "x": null } } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Alerts": { "Quiet": { "Days": [ null ] } } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Taskbar": { "Slots": [ { "Field": "Feed", "Feed": "x" } ] } } }""")]
    [InlineData("""{ "Format": "Windsock settings", "Version": 1, "Settings": { "Notifications": null, "Taskbar": { "Shortcuts": null } } }""")]
    public void ACraftedFile_IsReadOrRefused_NeverThrown(string text)
    {
        WindsockSettings mine = Changed();
        string before = Json(mine);

        SettingsImport read = SettingsTransfer.Read(text, mine);

        Assert.Equal(before, Json(mine));

        if (!read.IsValid)
        {
            Assert.NotEmpty(read.Problem);
            return;
        }

        WindsockSettings taken = new();
        taken.CopyFrom(read.Settings!);
        _ = JsonSerializer.Serialize(taken, Written);

        foreach (DataPlan plan in taken.Usage.Plans)
        {
            Assert.False(string.IsNullOrWhiteSpace(plan.Id));
            _ = plan.Problems();
            _ = plan.Levels;
            _ = plan.OffPeak.Length;
            _ = plan.Covers("anything");
        }
    }

    [Fact]
    public void AFileNestedFarTooDeep_IsRefused()
    {
        SettingsImport read = SettingsTransfer.Read(
            Envelope(string.Concat("""{ "Taskbar": { "Unknown": """, new string('[', 200), new string(']', 200), " } }")),
            new WindsockSettings());

        Assert.False(read.IsValid);
    }

    /// <summary>
    /// A plan's id is what the levels already told are kept under and what the
    /// Data plan page finds it by: a blank or repeated one is given its own.
    /// </summary>
    [Fact]
    public void ImportedPlans_EachHaveAnIdOfTheirOwn()
    {
        SettingsImport read = SettingsTransfer.Read(
            Envelope("""{ "Usage": { "Plans": [ { "Name": "a", "Id": "" }, { "Name": "b", "Id": "same" }, { "Name": "c", "Id": "same" }, { "Name": "d", "Id": "  " } ] } }"""),
            new WindsockSettings());

        Assert.True(read.IsValid, read.Problem);
        string[] ids = [.. read.Settings!.Usage.Plans.Select(plan => plan.Id)];

        Assert.Equal(4, ids.Length);
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("same", ids[1]);
    }

    /// <summary>
    /// The same, for a settings.json edited by hand, with the same ids at every
    /// start until the plans are saved, so a level is not told twice.
    /// </summary>
    [Fact]
    public void ASettingsFileEditedByHand_GivesEveryPlanAnIdOfItsOwn_TheSameAtEveryStart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "windsock-s2-plans-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(folder, "settings.json");

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, """{ "Usage": { "Plans": [ { "Name": "a", "Id": "" }, { "Name": "b", "Id": "x" }, { "Name": "c", "Id": "x" }, null ] } }""");

            WindsockSettings first = new SettingsStore(file).Load();
            WindsockSettings second = new SettingsStore(file).Load();

            string[] ids = [.. first.Usage.Plans.Select(plan => plan.Id)];

            Assert.Equal(3, ids.Length);
            Assert.Equal(3, ids.Distinct(StringComparer.Ordinal).Count());
            Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
            Assert.Equal(ids, second.Usage.Plans.Select(plan => plan.Id));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Nothing the settings work out from what was chosen is written to
    /// settings.json or an export: read back it would be ignored, and in a
    /// change list it would read as a choice that changed.
    /// </summary>
    [Fact]
    public void NothingWorkedOut_IsWritten()
    {
        List<string> written = [];
        HashSet<Type> seen = [];

        void Visit(Type type)
        {
            Type plain = Nullable.GetUnderlyingType(type) ?? type;

            if (plain.IsGenericType && plain.GetGenericArguments() is { Length: > 0 } arguments && typeof(System.Collections.IEnumerable).IsAssignableFrom(plain))
            {
                foreach (Type argument in arguments)
                {
                    Visit(argument);
                }

                return;
            }

            if (!Ours(plain) || !seen.Add(plain))
            {
                return;
            }

            foreach (PropertyInfo property in plain.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.IsDefined(typeof(JsonIgnoreAttribute)) || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                if (property.SetMethod?.IsPublic != true)
                {
                    written.Add(string.Concat(plain.Name, ".", property.Name));
                    continue;
                }

                Visit(property.PropertyType);
            }
        }

        Visit(typeof(WindsockSettings));

        Assert.Empty(written);
    }

    /// <summary>A plan's computed figures are worked out, never written to settings.json or a file.</summary>
    [Fact]
    public void APlansComputedFigures_AreNotWritten()
    {
        WindsockSettings settings = new();
        settings.Usage.Plans = [new DataPlan { Name = "p", OffPeak = new OffPeakWindow { Enabled = true } }];

        JsonObject plan = (JsonObject)Whole(settings)["Usage"]!["Plans"]![0]!;
        JsonObject offPeak = (JsonObject)plan["OffPeak"]!;

        Assert.False(plan.ContainsKey("CoversEveryNetwork"));
        Assert.False(plan.ContainsKey("Levels"));
        Assert.True(plan.ContainsKey("AlertLevels"));
        Assert.False(offPeak.ContainsKey("Start"));
        Assert.False(offPeak.ContainsKey("End"));
        Assert.False(offPeak.ContainsKey("Length"));
        Assert.True(offPeak.ContainsKey("StartMinute"));
    }

    /// <summary>
    /// A settings file is a few kilobytes. More data plans than are kept is
    /// refused; lists that only grow by hand are cut to what they can hold.
    /// </summary>
    [Fact]
    public void ListsGrownByHand_AreHeldToWhatTheyCanHold()
    {
        string plans = string.Join(", ", Enumerable.Range(0, SettingsTransfer.MostPlans + 1)
            .Select(each => string.Create(CultureInfo.InvariantCulture, $$"""{ "Name": "p{{each}}" }""")));

        SettingsImport tooMany = SettingsTransfer.Read(Envelope(string.Concat("""{ "Usage": { "Plans": [ """, plans, " ] } }")), new WindsockSettings());

        Assert.False(tooMany.IsValid);
        Assert.Contains("data plans", tooMany.Problem, StringComparison.Ordinal);

        string levels = string.Join(", ", Enumerable.Range(1, 50));
        SettingsImport read = SettingsTransfer.Read(
            Envelope(string.Concat(
                """{ "Usage": { "Plans": [ { "Name": "p", "AlertLevels": [ """, levels, """ ] } ] },""",
                """ "Alerts": { "Quiet": { "Days": [ "Monday", "Monday", "Friday", "Monday" ] },""",
                """ "Choices": { "no-such-alert": { "Sound": "Mail" }, "drive-full": { "Sound": "Mail" }, "ping-high": null } } }""")),
            new WindsockSettings());

        Assert.True(read.IsValid, read.Problem);
        WindsockSettings settings = read.Settings!;
        Assert.Equal(DataPlan.MostLevels, settings.Usage.Plans[0].AlertLevels.Count);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], settings.Alerts.Quiet.Days);
        Assert.Equal(["drive-full"], settings.Alerts.Choices.Keys);
    }

    /// <summary>
    /// A file at the size limit, every byte of it meter places, is read in
    /// moments: the places past the meter's rows and columns go before anything
    /// else looks at them.
    /// </summary>
    [Fact]
    public void AFileAtTheSizeLimit_IsReadInMoments()
    {
        System.Text.StringBuilder slots = new("""{ "Taskbar": { "Rows": 1, "Columns": 3, "Slots": [ """);

        while (slots.Length < SettingsTransfer.LargestFile - 400)
        {
            slots.Append("""{ "Field": "Download" }, """);
        }

        slots.Append("""{ "Field": "Upload" } ] } }""");

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        SettingsImport read = SettingsTransfer.Read(Envelope(slots.ToString()), new WindsockSettings());
        watch.Stop();

        Assert.True(read.IsValid, read.Problem);
        Assert.Equal(3, read.Settings!.Taskbar.Slots.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }

    /// <summary>
    /// Reading names rather than numbers is meant to rule out a choice that
    /// does not exist, but the reader takes "Light, Dark" apart at its comma
    /// and adds the two. Every value read is held to the values its choice has.
    /// </summary>
    [Theory]
    [InlineData("""{ "Theme": "Light, Dark" }""")]
    [InlineData("""{ "Taskbar": { "Click": "HideMeter, Mini" } }""")]
    [InlineData("""{ "Taskbar": { "Slots": [ { "Field": "NpuLoad, Download" } ] } }""")]
    [InlineData("""{ "Taskbar": { "Slots": [ { "Field": "Download", "Rotate": [ "NpuLoad, Download" ] } ] } }""")]
    [InlineData("""{ "Usage": { "Plans": [ { "Name": "p", "Rollover": "NextCycleOnly, Accumulate" } ] } }""")]
    [InlineData("""{ "Usage": { "Digest": { "WeekStarts": "Friday, Saturday" } } }""")]
    [InlineData("""{ "Alerts": { "Quiet": { "Days": [ "Friday, Saturday" ] } } }""")]
    [InlineData("""{ "OnClose": "Quit, KeepRunning" }""")]
    [InlineData("""{ "Usage": { "Plans": [ { "Name": "p", "ResetWeekday": "Monday, Friday" } ] } }""")]
    [InlineData("""{ "Alerts": { "Choices": { "drive-full": { "Sound": "Mail, Mail, Mail" } } } }""")]
    public void AChoiceMadeOfTwoNames_RefusesTheWholeFile(string body)
    {
        WindsockSettings mine = Changed();
        string before = Json(mine);

        SettingsImport read = SettingsTransfer.Read(Envelope(body), mine);

        Assert.False(read.IsValid);
        Assert.Contains("choice Windsock does not know", read.Problem, StringComparison.Ordinal);
        Assert.Equal(before, Json(mine));
    }

    /// <summary>
    /// The reader's own path starts again inside a meter place, so the trouble
    /// is found by reading the parts one at a time: the place is named, not
    /// just "$.Feed".
    /// </summary>
    [Fact]
    public void ThePartThatWouldNotRead_IsNamedInFull()
    {
        SettingsImport read = SettingsTransfer.Read(
            Envelope("""{ "Taskbar": { "Rows": 1, "Columns": 2, "Slots": [ { "Field": "Download" }, { "Field": "Feed", "Feed": { "Seconds": "often" } } ] } }"""),
            new WindsockSettings());

        Assert.False(read.IsValid);
        Assert.Contains("Taskbar › Slots[1] › Feed", read.Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// A graphics card is named as this PC names it: a place takes this PC's
    /// card for the same place, or follows the busiest.
    /// </summary>
    [Fact]
    public void AGraphicsCard_IsThisPcsOwn()
    {
        WindsockSettings there = new();
        there.Taskbar.Rows = 1;
        there.Taskbar.Columns = 2;
        there.Taskbar.Slots =
        [
            new MeterSlot { Field = MeterField.GraphicsLoad, Card = "There RTX" },
            new MeterSlot { Field = MeterField.GraphicsLoad, Card = "There Arc" },
        ];

        WindsockSettings here = new();
        here.Taskbar.Rows = 1;
        here.Taskbar.Columns = 1;
        here.Taskbar.Slots = [new MeterSlot { Field = MeterField.GraphicsLoad, Card = "Here Radeon" }];

        string file = SettingsTransfer.Export(there, Now);

        Assert.DoesNotContain("There", file, StringComparison.Ordinal);

        SettingsImport read = SettingsTransfer.Read(file, here);

        Assert.True(read.IsValid, read.Problem);
        Assert.Equal("Here Radeon", read.Settings!.Taskbar.Slots[0].Card);
        Assert.Null(read.Settings.Taskbar.Slots[1].Card);
    }

    /// <summary>
    /// A data plan's network is written with its kind where this PC's history
    /// knows it, and found on the other PC only among networks of that kind:
    /// a cable named "Home" is not the Wi-Fi network named "Home".
    /// </summary>
    [Fact]
    public void APlansNetwork_IsFoundByName_OnlyAmongItsOwnKind()
    {
        WindsockSettings there = new();
        there.Usage.Plans = [new DataPlan { Name = "Home plan", Networks = [new PlanNetwork { Key = "there-home", Name = "Home" }] }];

        string file = SettingsTransfer.Export(there, Now, [new NetworkTag("there-home", "Home", NetworkKind.WiFi)]);

        Assert.Contains("\"WiFi\"", file, StringComparison.Ordinal);

        SettingsImport cable = SettingsTransfer.Read(file, new WindsockSettings(), [new NetworkTag("here-cable", "Home", NetworkKind.Ethernet)]);

        Assert.True(cable.IsValid, cable.Problem);
        Assert.Empty(cable.Settings!.Usage.Plans);
        Assert.Single(cable.Notes);

        SettingsImport wireless = SettingsTransfer.Read(
            file,
            new WindsockSettings(),
            [new NetworkTag("here-cable", "Home", NetworkKind.Ethernet), new NetworkTag("here-wifi", "Home", NetworkKind.WiFi)]);

        Assert.True(wireless.IsValid, wireless.Problem);
        PlanNetwork network = Assert.Single(Assert.Single(wireless.Settings!.Usage.Plans).Networks);
        Assert.Equal("here-wifi", network.Key);
        Assert.Equal(NetworkKind.WiFi, network.Kind);
    }

    /// <summary>Settings with every leaf of every group turned some other way, lists given an item each.</summary>
    private static WindsockSettings Flipped()
    {
        WindsockSettings settings = new();
        Flip(settings, depth: 0);

        return settings;
    }

    private static void Flip(object target, int depth)
    {
        Assert.True(depth < 10, "The settings nest deeper than the test expects");

        foreach (PropertyInfo property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead
                || property.SetMethod?.IsPublic != true
                || property.GetIndexParameters().Length != 0
                || property.IsDefined(typeof(JsonIgnoreAttribute)))
            {
                continue;
            }

            Type plain = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? value = property.GetValue(target);

            if (Other(plain, value) is { } other)
            {
                property.SetValue(target, other);
                continue;
            }

            if (value is null && Ours(plain) && plain.GetConstructor(Type.EmptyTypes) is not null)
            {
                value = Activator.CreateInstance(plain)!;
                property.SetValue(target, value);
            }

            switch (value)
            {
                case IDictionary map when plain.GetGenericArguments() is [{ } key, { } item] && key == typeof(string) && Ours(item):
                    object entry = Activator.CreateInstance(item)!;
                    Flip(entry, depth + 1);
                    map["flipped"] = entry;
                    break;

                case IList list when plain.IsGenericType && plain.GetGenericArguments() is [{ } item]:
                    if (Ours(item))
                    {
                        if (list.Count == 0)
                        {
                            list.Add(Activator.CreateInstance(item));
                        }

                        foreach (object? each in list)
                        {
                            if (each is not null)
                            {
                                Flip(each, depth + 1);
                            }
                        }
                    }
                    else if (Other(Nullable.GetUnderlyingType(item) ?? item, null) is { } added)
                    {
                        list.Add(added);
                    }

                    break;

                case not null when Ours(plain):
                    Flip(value, depth + 1);
                    break;
            }
        }
    }

    /// <summary>Another value of a plain type, or null for a type that is not plain.</summary>
    private static object? Other(Type type, object? value)
    {
        if (type == typeof(bool))
        {
            return value is not true;
        }

        if (type == typeof(string))
        {
            return string.Concat(value as string, "flipped");
        }

        if (type.IsEnum)
        {
            foreach (object each in Enum.GetValues(type))
            {
                if (!each.Equals(value))
                {
                    return each;
                }
            }

            return null;
        }

        if (type == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
        }

        if (type == typeof(double))
        {
            return (value is double number ? number : 0) + 1.5;
        }

        if (type.IsPrimitive)
        {
            long number = value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return Convert.ChangeType(number + 3, type, CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static bool Ours(Type type) =>
        type.IsClass && type.Namespace?.StartsWith("Windsock", StringComparison.Ordinal) == true;

    private static WindsockSettings Clone(WindsockSettings settings) =>
        JsonSerializer.Deserialize<WindsockSettings>(JsonSerializer.Serialize(settings, Written), Written)!;

    private static JsonObject Whole(WindsockSettings settings) =>
        (JsonObject)JsonSerializer.SerializeToNode(settings, Written)!;

    /// <summary>Every leaf of a tree by its dotted path, lists by position.</summary>
    private static Dictionary<string, string> Leaves(JsonNode? node)
    {
        Dictionary<string, string> leaves = new(StringComparer.Ordinal);

        void Walk(JsonNode? each, string path)
        {
            switch (each)
            {
                case JsonObject branch:
                    foreach ((string key, JsonNode? child) in branch)
                    {
                        Walk(child, path.Length == 0 ? key : string.Concat(path, ".", key));
                    }

                    break;

                case JsonArray list:
                    leaves[path + ".Count"] = list.Count.ToString(CultureInfo.InvariantCulture);

                    for (int index = 0; index < list.Count; index++)
                    {
                        Walk(list[index], string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]"));
                    }

                    break;

                default:
                    leaves[path] = each?.ToJsonString() ?? "null";
                    break;
            }
        }

        Walk(node, string.Empty);

        return leaves;
    }

    /// <summary>A leaf's path with list positions as [*], and every path above it.</summary>
    private static IEnumerable<string> Prefixes(string path)
    {
        string pattern = Pattern(path);

        for (int index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '.')
            {
                yield return pattern[..index];
            }
        }

        yield return pattern;
    }

    private static string Pattern(string path) =>
        System.Text.RegularExpressions.Regex.Replace(path, @"\[\d+\]", "[*]");

    /// <summary>Whether a leaf is one of, or inside one of, the settings that stay on this PC.</summary>
    private static bool Stays(string path)
    {
        string pattern = Pattern(path);

        return SettingsTransfer.StaysOnThisPc.Any(each => Under(pattern, each));
    }

    private static bool Under(string path, string parent) =>
        path == parent
        || path.StartsWith(parent + ".", StringComparison.Ordinal)
        || path.StartsWith(parent + "[", StringComparison.Ordinal);
}
