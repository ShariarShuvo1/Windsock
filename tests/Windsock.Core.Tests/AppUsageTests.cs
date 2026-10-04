using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Windsock.Core.Apps;
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Processes;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class AppUsageTests : IDisposable
{
    private static readonly TimeZoneInfo Dhaka =
        TimeZoneInfo.CreateCustomTimeZone("Test+6", TimeSpan.FromHours(6), "Test+6", "Test+6");

    private static readonly TimeZoneInfo Kolkata =
        TimeZoneInfo.CreateCustomTimeZone("Test+5:30", new TimeSpan(5, 30, 0), "Test+5:30", "Test+5:30");

    // 2026-09-15 03:17 UTC, a Tuesday.
    private static readonly long Now = EpochMinute.From(new DateTimeOffset(2026, 9, 15, 3, 17, 0, TimeSpan.Zero));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Windsock-tests", Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_directory, "Windsock.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
            // A temp directory left behind is not worth failing a test over.
        }
    }

    [Theory]
    [InlineData(@"\device\harddiskvolume3\program files\google\chrome\application\chrome.exe", AppKind.Program)]
    [InlineData(@"C:\Program Files\app.exe", AppKind.Program)]
    [InlineData("Microsoft.WindowsStore_8wekyb3d8bbwe", AppKind.Package)]
    [InlineData("Claude_pzs8sxrjxfjjc", AppKind.Package)]
    [InlineData("System", AppKind.System)]
    [InlineData(@"System\IPv6 Control Message", AppKind.System)]
    [InlineData("", AppKind.Unattributed)]
    [InlineData("   ", AppKind.Unattributed)]
    [InlineData(null, AppKind.Unattributed)]
    [InlineData("SomeWindowsBucket", AppKind.System)]
    public void Identifiers_AreSortedByWhatTheyName(string? id, AppKind kind) =>
        Assert.Equal(kind, AppAttribution.Classify(id).Kind);

    [Fact]
    public void ASystemBucket_IsNamedByWhatFollowsSystem()
    {
        Assert.Null(AppAttribution.Classify("System").SystemBucket);
        Assert.Equal("IPv6 Control Message", AppAttribution.Classify(@"System\IPv6 Control Message").SystemBucket);
        Assert.Null(AppAttribution.Classify("Claude_pzs8sxrjxfjjc").SystemBucket);
    }

    [Fact]
    public void UnattributedTraffic_IsStoredUnderOneKey()
    {
        Assert.Equal(AppAttribution.UnattributedId, AppAttribution.Classify(null).Id);
        Assert.Equal(AppAttribution.UnattributedId, AppAttribution.Classify("  ").Id);
    }

    private static readonly IReadOnlyList<DevicePaths.DriveDevice> Drives =
    [
        new("C:", @"\Device\HarddiskVolume3"),
        new("D:", @"\Device\HarddiskVolume1"),
        new("E:", @"\Device\HarddiskVolume10"),
    ];

    [Theory]
    [InlineData(@"\device\harddiskvolume3\program files\qbittorrent\qbittorrent.exe", @"C:\program files\qbittorrent\qbittorrent.exe")]
    [InlineData(@"\device\harddiskvolume1\games\a.exe", @"D:\games\a.exe")]
    [InlineData(@"\device\harddiskvolume10\tools\b.exe", @"E:\tools\b.exe")]
    [InlineData(@"C:\already\dos.exe", @"C:\already\dos.exe")]
    [InlineData(@"\??\C:\Windows\x.exe", @"C:\Windows\x.exe")]
    [InlineData(@"\device\harddiskvolume7\unmounted.exe", null)]
    [InlineData(@"\device\harddiskvolume3", null)]
    public void DevicePaths_BecomeDriveLetters(string device, string? expected) =>
        Assert.Equal(expected, DevicePaths.Translate(device, Drives));

    [Fact]
    public void DevicePaths_OnThisMachine_TranslateTheSystemDrive()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        DevicePaths paths = new();

        // A drive-letter path passes straight through; a device path has to be
        // looked up, which is what this machine's own drives are for.
        Assert.Equal(windows, paths.ToDosPath(windows), StringComparer.OrdinalIgnoreCase);

        string? device = paths.ToDosPath(@"\device\harddiskvolume999\nothing.exe");
        Assert.Null(device);

        string explorer = Path.Combine(windows, "explorer.exe");
        string? volume = VolumeDeviceOf(windows);
        Assert.NotNull(volume);
        Assert.Equal(explorer, paths.ToDosPath(volume + explorer[2..]), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ms-resource:appDisplayName", "@{App_1.0.0.0_x64__abc?ms-resource://App/resources/appDisplayName}")]
    [InlineData("ms-resource:StartMenuExperienceHost/PkgDisplayName", "@{App_1.0.0.0_x64__abc?ms-resource://App/StartMenuExperienceHost/PkgDisplayName}")]
    [InlineData("ms-resource://Other/Core/Resources/AppName", "@{App_1.0.0.0_x64__abc?ms-resource://Other/Core/Resources/AppName}")]
    [InlineData("ms-resource:/Resources/Name", "@{App_1.0.0.0_x64__abc?ms-resource://App/Resources/Name}")]
    [InlineData("Microsoft Store", null)]
    [InlineData("ms-resource:bad}name", null)]
    public void PackageNames_ResolveThroughThePackagesOwnResources(string value, string? expected) =>
        Assert.Equal(expected, AppNames.IndirectSource("App_1.0.0.0_x64__abc", value));

    [Fact]
    public void APackageLogo_IsTheScaledCopyWhenThePlainOneIsMissing_AndNeverOutsideThePackage()
    {
        string package = Path.Combine(_directory, "package");
        string assets = Path.Combine(package, "Assets");
        Directory.CreateDirectory(assets);
        System.IO.File.WriteAllBytes(Path.Combine(assets, "StoreLogo.scale-200.png"), [1]);
        System.IO.File.WriteAllBytes(Path.Combine(assets, "StoreLogo.scale-100.png"), [1]);
        System.IO.File.WriteAllBytes(Path.Combine(_directory, "outside.png"), [1]);

        Assert.Equal(Path.Combine(assets, "StoreLogo.scale-100.png"), AppNames.Logo(package, @"Assets\StoreLogo.png"));
        Assert.Null(AppNames.Logo(package, @"..\outside.png"));
        Assert.Null(AppNames.Logo(package, @"Assets\Missing.png"));
    }

    [Fact]
    public void AProgramsName_IsItsFileNameWhenItSaysNothingBetter()
    {
        Assert.Equal("qbittorrent", AppNames.FileName(@"C:\program files\qbittorrent\qbittorrent.exe"));
        Assert.Equal("System", AppNames.FileName("System"));
    }

    [Fact]
    public void AFirstCollection_ReachesBackTwoWeeks_ToTheHour()
    {
        long start = AppUsagePlan.Start(null, Now, Dhaka);

        Assert.InRange(Now - start, AppUsagePlan.Backfill, AppUsagePlan.Backfill + 59);
        Assert.Equal(0, LocalMinuteOfHour(start, Dhaka));
    }

    [Fact]
    public void ACursorOlderThanWindowsKeeps_StartsAgainTwoWeeksBack()
    {
        long stale = Now - (30 * 24 * 60);

        Assert.Equal(AppUsagePlan.Start(null, Now, Dhaka), AppUsagePlan.Start(stale, Now, Dhaka));
    }

    [Fact]
    public void ACursorInTheFuture_ComesBackToNow()
    {
        Assert.Equal(AppUsagePlan.SafeEnd(Now), AppUsagePlan.Start(Now + 90, Now, Dhaka));
    }

    [Fact]
    public void Windows_AreLaidEndToEnd_HoursFarBack_MinutesRecently_AndNeverReachUnsettledMinutes()
    {
        long from = Now - (10 * 60) - 17;
        List<UsageWindow> all = [];
        long cursor = from;

        while (true)
        {
            IReadOnlyList<UsageWindow> next = AppUsagePlan.Next(cursor, Now, Dhaka, limit: 50);

            if (next.Count == 0)
            {
                break;
            }

            all.AddRange(next);
            cursor = next[^1].EndMinute;
        }

        Assert.Equal(from, all[0].StartMinute);

        for (int i = 1; i < all.Count; i++)
        {
            Assert.Equal(all[i - 1].EndMinute, all[i].StartMinute);
        }

        Assert.Equal(AppUsagePlan.SafeEnd(Now), all[^1].EndMinute);
        Assert.All(all, window => Assert.InRange(window.Minutes, 1, 60));

        foreach (UsageWindow window in all)
        {
            if (window.StartMinute >= Now - AppUsagePlan.MinuteDetail)
            {
                Assert.Equal(1, window.Minutes);
            }
            else if (window.Minutes > 1 && window.StartMinute != from)
            {
                // A wide window starts on the hour of the local clock, unless
                // it was cut short where the minute detail begins.
                Assert.True(
                    LocalMinuteOfHour(window.StartMinute, Dhaka) == 0,
                    $"a wide window started at local minute {LocalMinuteOfHour(window.StartMinute, Dhaka)}");
            }
        }
    }

    [Fact]
    public void HourWindows_FollowTheLocalHour_WhereTheZoneIsOffTheHour()
    {
        long minute = Now - (6 * 60);
        long next = AppUsagePlan.NextHour(minute, Kolkata);

        Assert.Equal(0, LocalMinuteOfHour(next, Kolkata));
        Assert.InRange(next - minute, 1, 60);
    }

    [Fact]
    public void HourWindows_StayWithinAnHour_AcrossAChangeOfClocks()
    {
        TimeZoneInfo london = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

        // 2026-10-25 01:00 UTC, when British clocks go back.
        long change = EpochMinute.From(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero));

        for (long minute = change - 120; minute < change + 120; minute += 7)
        {
            long next = AppUsagePlan.NextHour(minute, london);
            Assert.InRange(next - minute, 1, 60);
        }
    }

    [Fact]
    public void RecentMinutes_AreReadAgain_ButNeverBeforeTheMinuteRunBegan()
    {
        long cursor = Now - 1;

        Assert.Equal(AppUsagePlan.Revisit, AppUsagePlan.Recent(cursor, cursor - 50, Now).Count);
        Assert.Equal(2, AppUsagePlan.Recent(cursor, cursor - 2, Now).Count);
        Assert.Empty(AppUsagePlan.Recent(cursor, cursor, Now));
        Assert.All(AppUsagePlan.Recent(cursor, 0, Now), window => Assert.Equal(1, window.Minutes));
    }

    [Fact]
    public void Store_AWindowReadAgain_ReplacesWhatWasKeptForIt()
    {
        using SqliteUsageHistoryStore store = new(File);
        long minute = Now - 10;
        UsageWindow window = new(minute, 1);

        store.WriteAppUsage([window], [new AppUsageSlice(minute, 1, "Claude_pzs8sxrjxfjjc", 500, 50), new AppUsageSlice(minute, 1, "System", 9, 9)], new AppUsagePosition(minute + 1, minute));
        store.WriteAppUsage([window], [new AppUsageSlice(minute, 1, "Claude_pzs8sxrjxfjjc", 200, 40)], new AppUsagePosition(minute + 1, minute));

        // Windows moved part of the record out of the minute, and System's with
        // it: what is kept is what Windows says is there now.
        AppUsageSlice slice = Assert.Single(store.ReadAppSlices(minute, minute + 1));
        Assert.Equal(200, slice.BytesReceived);
        Assert.Equal(40, slice.BytesSent);

        store.WriteAppUsage([window], [], new AppUsagePosition(minute + 1, minute));
        Assert.Empty(store.ReadAppSlices(minute, minute + 1));
    }

    [Fact]
    public void Store_RemembersHowFarCollectionGot()
    {
        using (SqliteUsageHistoryStore store = new(File))
        {
            Assert.Null(store.ReadAppUsagePosition().Cursor);
            store.WriteAppUsage([], [], new AppUsagePosition(Now - 3, Now - 40));
        }

        using SqliteUsageHistoryStore reopened = new(File);
        AppUsagePosition position = reopened.ReadAppUsagePosition();

        Assert.Equal(Now - 3, position.Cursor);
        Assert.Equal(Now - 40, position.MinuteRunStart);
    }

    [Fact]
    public void Store_TotalsEachAppAcrossAPeriod_AndKeepsUnattributedApart()
    {
        using SqliteUsageHistoryStore store = new(File);
        long minute = Now - 30;

        store.WriteAppUsage(
        [new UsageWindow(minute, 1), new UsageWindow(minute + 1, 1), new UsageWindow(minute + 2, 1)],
        [
            new AppUsageSlice(minute, 1, "System", 10, 1),
            new AppUsageSlice(minute, 1, string.Empty, 5, 5),
            new AppUsageSlice(minute + 1, 1, "System", 20, 2),
            new AppUsageSlice(minute + 2, 1, "System", 0, 0),
        ],
        new AppUsagePosition(minute + 3, minute));

        IReadOnlyList<AppUsageTotal> totals = store.ReadAppTotals(EpochMinute.ToUtc(minute), EpochMinute.ToUtc(minute + 3));

        Assert.Equal(2, totals.Count);
        AppUsageTotal system = Assert.Single(totals, total => total.AttributionId == "System");
        Assert.Equal(30, system.BytesReceived);
        Assert.Equal(3, system.BytesSent);
        Assert.Contains(totals, total => total.AttributionId.Length == 0 && total.BytesTotal == 10);
    }

    [Fact]
    public void MissedSpans_AreOnlyWindowsWithNoRecordedMinute()
    {
        using SqliteUsageHistoryStore store = new(File);
        long hour = Now - 600;

        // Windsock recorded one minute inside the second hour.
        store.Write([new UsageMinute(hour + 75, 1, 1, 1, 1)]);

        store.WriteAppUsage(
        [new UsageWindow(hour, 60), new UsageWindow(hour + 60, 60), new UsageWindow(hour + 120, 1)],
        [
            new AppUsageSlice(hour, 60, "System", 100, 10),
            new AppUsageSlice(hour, 60, "Claude_pzs8sxrjxfjjc", 50, 5),
            new AppUsageSlice(hour + 60, 60, "System", 999, 999),
            new AppUsageSlice(hour + 120, 1, "System", 7, 7),
        ],
        new AppUsagePosition(hour + 121, hour + 120));

        IReadOnlyList<UsageSpan> missed = store.ReadMissedSpans(EpochMinute.ToUtc(hour), EpochMinute.ToUtc(hour + 121));

        Assert.Equal(2, missed.Count);
        Assert.Equal(new UsageSpan(hour, 60, 150, 15), missed[0]);
        Assert.Equal(new UsageSpan(hour + 120, 1, 7, 7), missed[1]);
    }

    [Fact]
    public void ErasingAPeriod_TakesWindowsFiguresForItToo_AndErasingEverything_KeepsThePosition()
    {
        using SqliteUsageHistoryStore store = new(File);
        long minute = Now - 30;

        store.WriteAppUsage(
        [new UsageWindow(minute, 1), new UsageWindow(minute + 5, 1)],
        [
            new AppUsageSlice(minute, 1, "System", 10, 1),
            new AppUsageSlice(minute + 5, 1, "System", 10, 1),
        ],
        new AppUsagePosition(minute + 6, minute));

        store.Delete(EpochMinute.ToUtc(minute), EpochMinute.ToUtc(minute + 1));
        Assert.Single(store.ReadAppSlices(minute, minute + 6));

        store.RemoveEverything();
        Assert.Empty(store.ReadAppSlices(minute, minute + 6));
        Assert.Equal(minute + 6, store.ReadAppUsagePosition().Cursor);
    }

    [Fact]
    public void AVersionOneFile_GainsTheAppTables_AndKeepsItsHistory()
    {
        Directory.CreateDirectory(_directory);

        using (SqliteConnection old = new($"Data Source={File};Pooling=False"))
        {
            old.Open();
            using SqliteCommand create = old.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE usage_minute (minute INTEGER PRIMARY KEY, down INTEGER NOT NULL, up INTEGER NOT NULL, peak_down INTEGER NOT NULL, peak_up INTEGER NOT NULL) WITHOUT ROWID;
                CREATE TABLE coverage (start_minute INTEGER PRIMARY KEY, end_minute INTEGER NOT NULL) WITHOUT ROWID;
                INSERT INTO usage_minute VALUES (1000, 5, 6, 7, 8);
                PRAGMA user_version=1;
                """;
            create.ExecuteNonQuery();
        }

        using SqliteUsageHistoryStore store = new(File);

        Assert.Equal(5, store.Summarise(EpochMinute.ToUtc(1000), EpochMinute.ToUtc(1001)).BytesDown);
        store.WriteAppUsage([new UsageWindow(Now - 10, 1)], [new AppUsageSlice(Now - 10, 1, "System", 1, 1)], new AppUsagePosition(Now - 9, Now - 10));
        Assert.Single(store.ReadAppSlices(Now - 10, Now - 9));
    }

    [Fact]
    public async Task Collection_CountsEveryRecordOnce_EvenWhenWindowsMovesOneAfterItWasKept()
    {
        ManualClock clock = new(EpochMinute.ToUtc(Now).AddSeconds(20));
        WindowsLike windows = new();
        Random random = new(15);

        // Records like Windows keeps: one a minute for two hours, and rolled
        // into a record stamped part-way through each hour before that.
        long hours = Now - (5 * 60);
        for (long at = hours; at < Now - 130; at += 60)
        {
            windows.Add(at + random.Next(20, 59), "System", random.Next(1, 1_000_000), random.Next(1, 1000));
        }

        for (long at = Now - 130; at < Now - 1; at++)
        {
            windows.Add(at, "Claude_pzs8sxrjxfjjc", random.Next(1, 1_000_000), random.Next(1, 1000));
        }

        using SqliteUsageHistoryStore store = new(File);
        store.WriteAppUsage([], [], new AppUsagePosition(hours, hours));

        AppUsageCollector collector = Collector(windows, store, clock, recording: true);

        while (await collector.CollectAsync(TestContext.Current.CancellationToken))
        {
        }

        // Nothing unsettled was kept: the newest minutes are left for later.
        Assert.Equal(EpochMinute.ToLocal(AppUsagePlan.SafeEnd(Now)), collector.CollectedUntil);
        Assert.Empty(store.ReadAppSlices(AppUsagePlan.SafeEnd(Now), Now + 1));

        // Then Windows moves most of a record already kept into the minute after
        // it, also kept, the way it was seen to on 2026-09-15.
        windows.Move(Now - 8, "Claude_pzs8sxrjxfjjc", Now - 7, share: 0.9);
        clock.Advance(TimeSpan.FromMinutes(10));
        await collector.CollectAsync(TestContext.Current.CancellationToken);

        // From the top of the hour: a start that far back is read from its hour.
        IReadOnlyList<AppUsageTotal> totals = store.ReadAppTotals(EpochMinute.ToUtc(AppUsagePlan.HourStart(hours, Dhaka)), EpochMinute.ToUtc(Now + 20));

        foreach (string app in windows.Apps)
        {
            AppUsageTotal total = Assert.Single(totals, each => each.AttributionId == app);
            Assert.Equal(windows.Received(app), total.BytesReceived);
            Assert.Equal(windows.Sent(app), total.BytesSent);
        }

        Assert.Equal(AppUsageState.Collecting, collector.State);
    }

    [Fact]
    public async Task TheRecentFigure_IsOneReadOfTheLastFewMinutes_KeptNowhere()
    {
        ManualClock clock = new(EpochMinute.ToUtc(Now).AddSeconds(20));
        WindowsLike windows = new();
        windows.Add(Now - 4, "Claude_pzs8sxrjxfjjc", 3000, 300);
        windows.Add(Now - 1, "Claude_pzs8sxrjxfjjc", 1000, 100);
        windows.Add(Now - 1, "System", 60, 6);
        windows.Add(Now - 40, "System", 5, 5);

        using SqliteUsageHistoryStore store = new(File);
        store.WriteAppUsage([], [], new AppUsagePosition(Now - 50, Now - 50));

        AppUsageCollector collector = Collector(windows, store, clock, recording: true);
        await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EpochMinute.ToLocal(Now - AppUsagePlan.RecentSpan), collector.RecentStart);
        Assert.Equal(new AppUsageRecord("Claude_pzs8sxrjxfjjc", 4000, 400), collector.Recent["Claude_pzs8sxrjxfjjc"]);
        Assert.Equal(66, collector.Recent["System"].BytesTotal);
        Assert.Contains(AppUsagePlan.RecentWindow(Now), windows.Asked);

        // None of it is in history until it has settled.
        Assert.Empty(store.ReadAppSlices(Now - 5, Now));
    }

    [Fact]
    public async Task AnEmptyRecentRead_KeepsTheLastReading_OnlyWhileItIsStillRecent()
    {
        ManualClock clock = new(EpochMinute.ToUtc(Now).AddSeconds(20));
        WindowsLike windows = new();
        windows.Add(Now - 2, "System", 600, 60);

        using SqliteUsageHistoryStore store = new(File);
        store.WriteAppUsage([], [], new AppUsagePosition(Now - 20, Now - 20));

        AppUsageCollector collector = Collector(windows, store, clock, recording: true);
        await collector.CollectAsync(TestContext.Current.CancellationToken);
        DateTimeOffset? first = collector.RecentStart;
        Assert.NotNull(first);

        // Windows in the middle of rewriting: the next read finds nothing.
        windows.Clear();
        clock.Advance(TimeSpan.FromMinutes(1));
        await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(first, collector.RecentStart);
        Assert.Equal(660, collector.Recent["System"].BytesTotal);

        // Long enough later, an old reading is no longer a recent one.
        clock.Advance(TimeSpan.FromMinutes(12));
        await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Null(collector.RecentStart);
        Assert.Empty(collector.Recent);
    }

    [Fact]
    public async Task TheRecentFigure_IsUnknown_WhenNothingIsInIt()
    {
        ManualClock clock = new(EpochMinute.ToUtc(Now).AddSeconds(20));
        WindowsLike windows = new();

        // Something long ago, nothing lately.
        windows.Add(Now - 90, "System", 100, 10);

        using SqliteUsageHistoryStore store = new(File);
        store.WriteAppUsage([], [], new AppUsagePosition(Now - 95, Now - 95));

        AppUsageCollector collector = Collector(windows, store, clock, recording: true);
        await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AppUsageState.Collecting, collector.State);
        Assert.Null(collector.RecentStart);
        Assert.Empty(collector.Recent);
    }

    [Fact]
    public async Task Collection_WhileRecordingIsOff_KeepsNothing_AndDoesNotFetchItLater()
    {
        ManualClock clock = new(EpochMinute.ToUtc(Now).AddSeconds(20));
        WindowsLike windows = new();

        for (long at = Now - 60; at < Now - 1; at++)
        {
            windows.Add(at, "System", 100, 10);
        }

        using SqliteUsageHistoryStore store = new(File);
        store.WriteAppUsage([], [], new AppUsagePosition(Now - 60, Now - 60));

        AppUsageCollector paused = Collector(windows, store, clock, recording: false);
        await paused.CollectAsync(TestContext.Current.CancellationToken);

        // Only the live figure was read, which is kept nowhere.
        Assert.Equal(AppUsageState.Paused, paused.State);
        Assert.All(windows.Asked, window => Assert.Equal(AppUsagePlan.RecentWindow(Now), window));
        Assert.NotEmpty(paused.Recent);

        AppUsageCollector resumed = Collector(windows, store, clock, recording: true);
        await resumed.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Empty(store.ReadAppSlices(Now - 60, Now));
    }

    [Fact]
    public async Task Collection_WhenWindowsWillNotAnswer_SaysSo_AndLosesNothing()
    {
        ManualClock clock = new(EpochMinute.ToUtc(Now).AddSeconds(20));
        WindowsLike windows = new() { Refuses = true };

        using SqliteUsageHistoryStore store = new(File);
        store.WriteAppUsage([], [], new AppUsagePosition(Now - 30, Now - 30));

        AppUsageCollector collector = Collector(windows, store, clock, recording: true);
        await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AppUsageState.Unavailable, collector.State);
        Assert.False(string.IsNullOrWhiteSpace(collector.Detail));
        Assert.Equal(Now - 30, store.ReadAppUsagePosition().Cursor);
        Assert.Null(collector.RecentStart);
    }

    [Fact]
    public void Start_AfterAGapLongerThanMinuteDetail_GoesBackToTheStartOfTheHour()
    {
        long hour = AppUsagePlan.HourStart(Now - 300, Dhaka);
        long partWay = hour + 32;

        Assert.Equal(hour, AppUsagePlan.Start(partWay, partWay + 500, Dhaka));

        // Within minute detail, nothing has been rolled up: carry on where it stopped.
        Assert.Equal(partWay, AppUsagePlan.Start(partWay, partWay + 50, Dhaka));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(40)]
    public async Task AnHourLeftPartWay_IsCountedOnce_AfterWindowsRollsItUp(int stampedAt)
    {
        long hour = AppUsagePlan.HourStart(Now - 30, Dhaka);
        ManualClock clock = new(EpochMinute.ToUtc(hour + 37).AddSeconds(20));
        WindowsLike windows = new();

        for (long at = hour; at < hour + 60; at++)
        {
            windows.Add(at, "System", 1000 + at % 7, 10);
        }

        long received = windows.Received("System");
        long sent = windows.Sent("System");

        using SqliteUsageHistoryStore store = new(File, clock);
        store.WriteAppUsage([], [], new AppUsagePosition(hour, hour));

        AppUsageCollector collector = Collector(windows, store, clock, recording: true);
        await collector.CollectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EpochMinute.ToLocal(hour + 32), collector.CollectedUntil);

        // Windsock closes. Hours later Windows has made the hour one record,
        // stamped before or after where collection stopped.
        windows.RollUp(hour, hour + stampedAt);
        clock.Advance(TimeSpan.FromHours(5));

        while (await collector.CollectAsync(TestContext.Current.CancellationToken))
        {
        }

        AppUsageTotal total = Assert.Single(store.ReadAppTotals(EpochMinute.ToUtc(hour - 60), EpochMinute.ToUtc(hour + 600)));
        Assert.Equal(received, total.BytesReceived);
        Assert.Equal(sent, total.BytesSent);
    }

    [Fact]
    public async Task ErasingEverything_IsNotUndone_ByMinutesReadAgain()
    {
        ManualClock clock = new(EpochMinute.ToUtc(Now).AddSeconds(20));
        WindowsLike windows = new();

        for (long at = Now - 40; at < Now - 1; at++)
        {
            windows.Add(at, "System", 100, 10);
        }

        using SqliteUsageHistoryStore store = new(File, clock);
        store.WriteAppUsage([], [], new AppUsagePosition(Now - 40, Now - 40));

        AppUsageCollector collector = Collector(windows, store, clock, recording: true);
        await collector.CollectAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(store.ReadAppSlices(Now - 40, Now));

        store.RemoveEverything();

        // The next passes read the last settled minutes again, as they always do.
        clock.Advance(TimeSpan.FromMinutes(1));
        await collector.CollectAsync(TestContext.Current.CancellationToken);
        Assert.Empty(store.ReadAppSlices(Now - 60, Now + 1));

        // What happens after the erase is kept as usual.
        windows.Add(Now + 3, "System", 500, 50);
        clock.Advance(TimeSpan.FromMinutes(10));
        await collector.CollectAsync(TestContext.Current.CancellationToken);

        AppUsageSlice after = Assert.Single(store.ReadAppSlices(Now - 60, Now + 20));
        Assert.Equal(Now + 3, after.StartMinute);
    }

    [Fact]
    public async Task AnEraseWhileWindowsIsBeingRead_IsNotWrittenBack()
    {
        ManualClock clock = new(EpochMinute.ToUtc(Now).AddSeconds(20));
        WindowsLike windows = new();

        for (long at = Now - 40; at < Now - 1; at++)
        {
            windows.Add(at, "System", 100, 10);
        }

        using SqliteUsageHistoryStore store = new(File, clock);
        store.WriteAppUsage([], [], new AppUsagePosition(Now - 40, Now - 40));

        windows.DuringRead = () => store.Delete(EpochMinute.ToUtc(Now - 30), EpochMinute.ToUtc(Now - 20));

        AppUsageCollector collector = Collector(windows, store, clock, recording: true);
        await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Empty(store.ReadAppSlices(Now - 30, Now - 20));
        Assert.Equal(10, store.ReadAppSlices(Now - 40, Now - 30).Count);
        Assert.Equal(15, store.ReadAppSlices(Now - 20, Now).Count);
    }

    [Fact]
    public void ErasingPartOfAnHour_TakesTheHoursWholeRecord()
    {
        using SqliteUsageHistoryStore store = new(File);
        long hour = Now - 600;

        store.WriteAppUsage([new UsageWindow(hour, 60)], [new AppUsageSlice(hour, 60, "System", 100, 10)], new AppUsagePosition(hour + 60, hour + 60));
        store.Delete(EpochMinute.ToUtc(hour + 30), EpochMinute.ToUtc(hour + 120));

        Assert.Empty(store.ReadAppSlices(hour - 60, hour + 120));
    }

    [Fact]
    public void MissedSpans_LeaveOutAWindowThePeriodOnlyPartlyCovers()
    {
        using SqliteUsageHistoryStore store = new(File);
        long hour = Now - 600;

        store.WriteAppUsage([new UsageWindow(hour, 60)], [new AppUsageSlice(hour, 60, "System", 100, 10)], new AppUsagePosition(hour + 60, hour + 60));

        Assert.Empty(store.ReadMissedSpans(EpochMinute.ToUtc(hour), EpochMinute.ToUtc(hour + 30)));
        Assert.Single(store.ReadMissedSpans(EpochMinute.ToUtc(hour), EpochMinute.ToUtc(hour + 60)));
    }

    [Fact]
    public async Task Changes_SayWhatWasKept()
    {
        ManualClock clock = new(EpochMinute.ToUtc(Now).AddSeconds(20));
        WindowsLike windows = new();
        windows.Add(Now - 12, "System", 100, 10);

        using SqliteUsageHistoryStore store = new(File, clock);
        store.WriteAppUsage([], [], new AppUsagePosition(Now - 15, Now - 15));

        AppUsageCollector collector = Collector(windows, store, clock, recording: true);
        List<AppUsageChangedEventArgs> heard = [];
        collector.Changed += (_, change) => heard.Add(change);

        await collector.CollectAsync(TestContext.Current.CancellationToken);

        AppUsageChangedEventArgs first = Assert.Single(heard);
        Assert.True(first.Changes.HasFlag(AppUsageChanges.Kept));
        Assert.True(first.KeptWithin(EpochMinute.ToUtc(Now - 13), EpochMinute.ToUtc(Now - 11)));
        Assert.False(first.KeptWithin(EpochMinute.ToUtc(Now - 60), EpochMinute.ToUtc(Now - 15)));
        Assert.Equal(EpochMinute.ToLocal(AppUsagePlan.SafeEnd(Now)), first.KeptUntil);
    }

    [Fact]
    public async Task ANetworkWindowsRefuses_IsLeftOut_ThenFilledIn_WhenItAnswers()
    {
        ManualClock clock = new(EpochMinute.ToUtc(Now).AddSeconds(20));
        WindowsLike windows = new();

        for (long at = Now - 40; at < Now - 10; at++)
        {
            windows.Add(at, "System", 100, 10, "Ethernet");
            windows.Add(at, "Claude_pzs8sxrjxfjjc", 700, 70, "Old hotspot");
        }

        windows.Refusing.Add("Old hotspot");

        using SqliteUsageHistoryStore store = new(File, clock);
        store.WriteAppUsage([], [], new AppUsagePosition(Now - 40, Now - 40));

        AppUsageCollector collector = Collector(windows, store, clock, recording: true);
        await collector.CollectAsync(TestContext.Current.CancellationToken);

        // Collection carries on without it, and says so.
        Assert.Equal(AppUsageState.Collecting, collector.State);
        Assert.Contains("Old hotspot", collector.Detail, StringComparison.Ordinal);
        Assert.Equal(Now - 40, store.ReadAppUsagePosition().MissingSince);
        Assert.DoesNotContain(store.ReadAppTotals(EpochMinute.ToUtc(Now - 60), EpochMinute.ToUtc(Now)), total => total.AttributionId.StartsWith("Claude", StringComparison.Ordinal));

        // It answers again: what it missed is read again, and nothing twice.
        windows.Refusing.Clear();
        clock.Advance(TimeSpan.FromMinutes(1));

        while (await collector.CollectAsync(TestContext.Current.CancellationToken))
        {
        }

        IReadOnlyList<AppUsageTotal> totals = store.ReadAppTotals(EpochMinute.ToUtc(Now - 60), EpochMinute.ToUtc(Now));
        Assert.Equal(30 * 110, Assert.Single(totals, total => total.AttributionId == "System").BytesTotal);
        Assert.Equal(30 * 770, Assert.Single(totals, total => total.AttributionId == "Claude_pzs8sxrjxfjjc").BytesTotal);
        Assert.Null(store.ReadAppUsagePosition().MissingSince);
        Assert.Null(collector.Detail);
    }

    [Fact]
    public async Task ReadingAgainForAMissingNetwork_LeavesPausedTimeOut()
    {
        ManualClock clock = new(EpochMinute.ToUtc(Now).AddSeconds(20));
        WindowsLike windows = new();

        for (long at = Now - 40; at < Now + 8; at++)
        {
            windows.Add(at, "System", 100, 10, "Ethernet");
            windows.Add(at, "Claude_pzs8sxrjxfjjc", 700, 70, "Old hotspot");
        }

        windows.Refusing.Add("Old hotspot");

        using SqliteUsageHistoryStore store = new(File, clock);
        store.WriteAppUsage([], [], new AppUsagePosition(Now - 40, Now - 40));

        await Collector(windows, store, clock, recording: true).CollectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Now - 5, store.ReadAppUsagePosition().Cursor);

        // Recording is switched off for ten minutes.
        clock.Advance(TimeSpan.FromMinutes(10));
        await Collector(windows, store, clock, recording: false).CollectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Now - 40, store.ReadAppUsagePosition().MissingSince);

        // Back on, and the network answers: its missing stretch is read again,
        // but not the ten minutes nobody wanted kept.
        windows.Refusing.Clear();
        clock.Advance(TimeSpan.FromMinutes(1));
        AppUsageCollector resumed = Collector(windows, store, clock, recording: true);

        while (await resumed.CollectAsync(TestContext.Current.CancellationToken))
        {
        }

        Assert.Empty(store.ReadAppSlices(Now - 5, Now + 5));
        Assert.Equal(35 * 770, Assert.Single(store.ReadAppTotals(EpochMinute.ToUtc(Now - 40), EpochMinute.ToUtc(Now - 5)), total => total.AttributionId == "Claude_pzs8sxrjxfjjc").BytesTotal);
    }

    [Fact]
    public void Refusals_AreAskedAgainLessOften_AndForgottenWhenAnswered()
    {
        ManualClock clock = new(EpochMinute.ToUtc(Now));
        NetworkProfileRefusals refusals = new(clock);

        Assert.True(refusals.ShouldAsk("Wi-Fi"));
        refusals.Refused("Wi-Fi", Now - 30);
        Assert.False(refusals.ShouldAsk("Wi-Fi"));
        Assert.True(refusals.ShouldAsk("Ethernet"));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(refusals.ShouldAsk("Wi-Fi"));
        refusals.Refused("Wi-Fi", Now - 10);

        // Two minutes this time, and the earliest minute missed is kept.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(refusals.ShouldAsk("Wi-Fi"));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(refusals.ShouldAsk("Wi-Fi"));
        Assert.Equal(new LeftOutNetwork("Wi-Fi", Now - 30), Assert.Single(refusals.LeftOut));

        for (int time = 0; time < 10; time++)
        {
            refusals.Refused("Wi-Fi", Now);
        }

        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.True(refusals.ShouldAsk("Wi-Fi"));

        refusals.Answered("Wi-Fi");
        Assert.Empty(refusals.LeftOut);

        refusals.Refused("Old VPN", Now);
        refusals.KeepOnly(["Wi-Fi"]);
        Assert.Empty(refusals.LeftOut);
    }

    [Fact]
    public void Store_RemembersWhatIsMissing_AndPassingOverIsKeptOut()
    {
        ManualClock clock = new(EpochMinute.ToUtc(Now).AddSeconds(20));

        using (SqliteUsageHistoryStore store = new(File, clock))
        {
            store.WriteAppUsage([], [], new AppUsagePosition(Now - 3, Now - 40, Now - 90));
        }

        using SqliteUsageHistoryStore reopened = new(File, clock);
        Assert.Equal(Now - 90, reopened.ReadAppUsagePosition().MissingSince);

        reopened.PassOver(Now - 3, new AppUsagePosition(Now - 2, Now - 2, Now - 90));
        reopened.PassOver(Now - 2, new AppUsagePosition(Now - 1, Now - 1));
        Assert.Null(reopened.ReadAppUsagePosition().MissingSince);

        // Passed-over minutes are not kept, whatever is written for them later.
        reopened.WriteAppUsage([new UsageWindow(Now - 3, 1), new UsageWindow(Now - 2, 1)], [new AppUsageSlice(Now - 3, 1, "System", 5, 5), new AppUsageSlice(Now - 2, 1, "System", 5, 5)], new AppUsagePosition(Now - 1, Now - 1));
        Assert.Empty(reopened.ReadAppSlices(Now - 3, Now - 1));
    }

    [Fact]
    public async Task RecentFigures_GiveAnAppItsCount_ZeroOnlyWhenWindowsCountsIt_AndNothingOtherwise()
    {
        string windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string? volume = VolumeDeviceOf(windowsFolder);
        Assert.NotNull(volume);

        string busy = Path.Combine(windowsFolder, "busy-test.exe");
        string idle = Path.Combine(windowsFolder, "idle-test.exe");
        string never = Path.Combine(windowsFolder, "never-test.exe");

        ManualClock clock = new(EpochMinute.ToUtc(Now).AddSeconds(20));
        WindowsLike windows = new();
        windows.Add(Now - 2, (volume + busy[2..]).ToLowerInvariant(), 3000, 300);

        using SqliteUsageHistoryStore store = new(File, clock);
        store.WriteAppUsage([], [], new AppUsagePosition(Now - 3, Now - 3));

        // Windows counted the idle program three days ago, and never the third.
        long earlier = Now - (3 * 24 * 60);
        store.WriteAppUsage(
            [new UsageWindow(earlier, 60)],
            [new AppUsageSlice(earlier, 60, (volume + idle[2..]).ToLowerInvariant(), 10, 10)],
            new AppUsagePosition(Now - 3, Now - 3));

        AppUsageCollector collector = Collector(windows, store, clock, recording: true);
        using RecentAppFigures figures = new(collector, store, new AppNames(new DevicePaths()), clock, NullLogger<RecentAppFigures>.Instance);

        TaskCompletionSource known = new(TaskCreationOptions.RunContinuationsAsynchronously);
        figures.Changed += (_, _) =>
        {
            if (figures.IsKnown)
            {
                known.TrySetResult();
            }
        };

        await collector.CollectAsync(TestContext.Current.CancellationToken);
        await known.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        AppUsageRecord? counted = figures.For(new ProcessIdentity("busy-test", null, busy, null));
        Assert.NotNull(counted);
        Assert.Equal(3300, counted.Value.BytesTotal);

        AppUsageRecord? quiet = figures.For(new ProcessIdentity("idle-test", null, idle, null));
        Assert.NotNull(quiet);
        Assert.Equal(0, quiet.Value.BytesTotal);

        Assert.Null(figures.For(new ProcessIdentity("never-test", null, never, null)));
        Assert.Null(figures.For(new ProcessIdentity("svchost", null, null, null)));
    }

    private static AppUsageCollector Collector(IAppUsageReader reader, IAppUsageStore store, TimeProvider clock, bool recording)
    {
        UsageRecorder recorder = new(
            new ThroughputMonitor(new StillTotals(), Options.Create(new ThroughputMonitorOptions()), NullLogger<ThroughputMonitor>.Instance),
            new NoHistory(),
            new WindsockSettings { RecordHistory = recording },
            NullLogger<UsageRecorder>.Instance);

        return new AppUsageCollector(reader, store, recorder, clock, Dhaka, NullLogger<AppUsageCollector>.Instance);
    }

    private static string? VolumeDeviceOf(string path) =>
        DevicePaths.ReadDrives()
            .Where(drive => string.Equals(drive.Drive, path[..2], StringComparison.OrdinalIgnoreCase))
            .Select(drive => drive.Device)
            .FirstOrDefault();

    private static int LocalMinuteOfHour(long minute, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(EpochMinute.ToUtc(minute), zone).Minute;

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Keeps stamped records the way Windows' data usage does, and answers the same way.</summary>
    private sealed class WindowsLike : IAppUsageReader
    {
        private readonly List<(long Stamp, string App, long Received, long Sent, string Network)> _records = [];
        private readonly Dictionary<string, long> _leftOut = new(StringComparer.Ordinal);

        public bool Refuses { get; init; }

        /// <summary>Run once, while the first read is under way.</summary>
        public Action? DuringRead { get; set; }

        public List<UsageWindow> Asked { get; } = [];

        public IEnumerable<string> Apps => _records.Select(record => record.App).Distinct();

        /// <summary>Networks Windows refuses, while others answer.</summary>
        public HashSet<string> Refusing { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<LeftOutNetwork> LeftOut => [.. _leftOut.Select(pair => new LeftOutNetwork(pair.Key, pair.Value))];

        public void Add(long stamp, string app, long received, long sent, string network = "Ethernet") =>
            _records.Add((stamp, app, received, sent, network));

        public void Clear() => _records.Clear();

        /// <summary>Makes an hour's records one record per app, stamped part-way through, as Windows does after two hours.</summary>
        public void RollUp(long hourStart, long stamp)
        {
            List<(long Stamp, string App, long Received, long Sent, string Network)> inHour = [.. _records.Where(record => record.Stamp >= hourStart && record.Stamp < hourStart + 60)];
            _records.RemoveAll(record => record.Stamp >= hourStart && record.Stamp < hourStart + 60);

            foreach (IGrouping<(string App, string Network), (long Stamp, string App, long Received, long Sent, string Network)> app in inHour.GroupBy(record => (record.App, record.Network)))
            {
                _records.Add((stamp, app.Key.App, app.Sum(record => record.Received), app.Sum(record => record.Sent), app.Key.Network));
            }
        }

        /// <summary>Moves part of a record to a later stamp, as Windows does with a provisional one.</summary>
        public void Move(long stamp, string app, long to, double share)
        {
            int index = _records.FindIndex(record => record.Stamp == stamp && record.App == app);
            (long s, string a, long r, long t, string n) = _records[index];
            long movedIn = (long)(r * share);
            long movedOut = (long)(t * share);
            _records[index] = (s, a, r - movedIn, t - movedOut, n);
            _records.Add((to, a, movedIn, movedOut, n));
        }

        public long Received(string app) => _records.Where(record => record.App == app).Sum(record => record.Received);

        public long Sent(string app) => _records.Where(record => record.App == app).Sum(record => record.Sent);

        public Task<IReadOnlyList<AppUsageReading>> ReadAsync(IReadOnlyList<UsageWindow> windows, CancellationToken token)
        {
            if (Refuses)
            {
                throw new AppUsageUnavailableException("Windows did not answer.");
            }

            Asked.AddRange(windows);
            List<AppUsageReading> readings = [];

            if (windows.Count > 0)
            {
                long first = windows.Min(window => window.StartMinute);

                foreach (string network in _records.Select(record => record.Network).Distinct())
                {
                    if (Refusing.Contains(network))
                    {
                        _leftOut[network] = _leftOut.TryGetValue(network, out long since) ? Math.Min(since, first) : first;
                    }
                    else
                    {
                        _leftOut.Remove(network);
                    }
                }
            }

            foreach (UsageWindow window in windows)
            {
                List<AppUsageRecord> records = [.. _records
                    .Where(record => record.Stamp >= window.StartMinute && record.Stamp < window.EndMinute && !Refusing.Contains(record.Network))
                    .GroupBy(record => record.App)
                    .Select(group => new AppUsageRecord(group.Key, group.Sum(each => each.Received), group.Sum(each => each.Sent)))];

                readings.Add(new AppUsageReading(window, records));
            }

            if (DuringRead is { } during)
            {
                DuringRead = null;
                during();
            }

            return Task.FromResult<IReadOnlyList<AppUsageReading>>(readings);
        }
    }

    private sealed class StillTotals : INetworkTotalsSource
    {
        public NetworkTotals ReadTotals() => default;
    }

    private sealed class NoHistory : IUsageHistoryStore
    {
        public event EventHandler? Erased
        {
            add { }
            remove { }
        }

        public void Write(IReadOnlyList<UsageMinute> minutes)
        {
        }

        public void WriteCoverage(long startMinute, long endMinute)
        {
        }

        public IReadOnlyList<UsageBucket> Read(DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity) => [];

        public UsageSummary Summarise(DateTimeOffset from, DateTimeOffset until) => UsageSummary.Empty;

        public IReadOnlyList<CoverageWindow> ReadCoverage(DateTimeOffset from, DateTimeOffset until) => [];

        public UsageExtent Extent() => default;

        public int Delete(DateTimeOffset from, DateTimeOffset until) => 0;

        public int RemoveEverything() => 0;

        public int Merge(IReadOnlyList<UsageMinute> minutes) => 0;

        public int Replace(IReadOnlyList<UsageMinute> minutes) => 0;

        public int CountExisting(IReadOnlyList<UsageMinute> minutes) => 0;
    }
}
