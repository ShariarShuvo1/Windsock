using Windsock.Core.Apps;
using Windsock.Core.History;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class HistoryFillTests
{
    private static DateTimeOffset Noon { get; } = new(new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Local));

    private static long MinuteOf(DateTimeOffset moment) => EpochMinute.From(moment);

    [Fact]
    public void NothingMissed_LeavesTheRecordedBucketsAlone()
    {
        IReadOnlyList<UsageBucket> recorded = [new UsageBucket(Noon, 1, 2, 3, 4, 60)];

        Assert.Same(recorded, HistoryFill.Merge(recorded, [], UsageGranularity.Hour));
    }

    [Fact]
    public void WindowsFigures_JoinTheHourTheyStartIn_ButStayApartFromWhatWasRecorded()
    {
        IReadOnlyList<UsageBucket> recorded = [new UsageBucket(Noon, 1000, 100, 50, 5, 20)];
        IReadOnlyList<UsageSpan> missed =
        [
            new UsageSpan(MinuteOf(Noon.AddMinutes(30)), 1, 700, 70),
            new UsageSpan(MinuteOf(Noon.AddMinutes(31)), 1, 300, 30),
            new UsageSpan(MinuteOf(Noon.AddHours(2)), 60, 5000, 500),
        ];

        IReadOnlyList<UsageBucket> merged = HistoryFill.Merge(recorded, missed, UsageGranularity.Hour);

        Assert.Equal(2, merged.Count);

        UsageBucket noon = merged[0];
        Assert.Equal(Noon, noon.Start);
        Assert.Equal(1000, noon.BytesDown);
        Assert.Equal(1000, noon.EstimatedDown);
        Assert.Equal(2000, noon.AllDown);
        Assert.Equal(20, noon.Minutes);
        Assert.Equal(2, noon.EstimatedMinutes);
        Assert.Equal(50, noon.PeakDown);

        UsageBucket later = merged[1];
        Assert.Equal(Noon.AddHours(2), later.Start);
        Assert.Equal(0, later.BytesDown);
        Assert.Equal(0, later.Minutes);
        Assert.Equal(5000, later.EstimatedDown);
        Assert.True(later.HasEstimate);
    }

    [Fact]
    public void ByMinute_AWideWindow_IsABucketOfItsOwn_AtItsStart()
    {
        IReadOnlyList<UsageSpan> missed = [new UsageSpan(MinuteOf(Noon), 60, 6000, 600)];

        UsageBucket only = Assert.Single(HistoryFill.Merge([], missed, UsageGranularity.Minute));

        Assert.Equal(Noon, only.Start);
        Assert.Equal(60, only.EstimatedMinutes);
    }

    [Fact]
    public void ByDay_WindowsGoToTheirLocalDay()
    {
        DateTimeOffset lateEvening = new(new DateTime(2026, 3, 14, 23, 0, 0, DateTimeKind.Local));

        UsageBucket day = Assert.Single(HistoryFill.Merge([], [new UsageSpan(MinuteOf(lateEvening), 60, 1, 1)], UsageGranularity.Day));

        Assert.Equal(new DateTimeOffset(new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Local)), day.Start);
    }

    [Fact]
    public void Totals_KeepTheEstimateSeparate()
    {
        UsageSummary summary = HistoryFill.Add(
            new UsageSummary(100, 10, 5, 1, 30),
            [new UsageSpan(1, 60, 400, 40), new UsageSpan(61, 60, 100, 10)]);

        Assert.Equal(100, summary.BytesDown);
        Assert.Equal(500, summary.EstimatedDown);
        Assert.Equal(600, summary.AllDown);
        Assert.Equal(60, summary.AllUp);
        Assert.Equal(120, summary.EstimatedMinutes);
        Assert.Equal(30, summary.Minutes);
    }

    [Theory]
    [InlineData(@"C:\Users\sam\AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude-code\2.1.270\claude.exe", @"C:\Users\sam\AppData\Roaming\Claude\claude-code\2.1.270\claude.exe")]
    [InlineData(@"c:\users\sam\appdata\local\packages\app_abc\localcache\local\tool\tool.exe", @"C:\Users\sam\AppData\Local\tool\tool.exe")]
    [InlineData(@"C:\Users\sam\AppData\Local\Packages\App_abc\LocalState\tool.exe", null)]
    [InlineData(@"C:\Users\sam\AppData\Local\Packages\App_abc\LocalState\LocalCache\Roaming\tool.exe", null)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", null)]
    public void AProgramInAPackagesRedirectedFolder_IsAlsoKnownByThePathItRunsUnder(string real, string? seen) =>
        Assert.Equal(seen, ProgramUsageIndex.AsSeenByThePackage(real, @"C:\Users\sam\AppData\Roaming", @"C:\Users\sam\AppData\Local"));

    [Fact]
    public void ProgramUsage_IsFoundByFile_OrByThePackageFolderItRunsFrom()
    {
        AppUsageRecord chrome = new(@"\device\harddiskvolume3\chrome.exe", 600, 60);
        AppUsageRecord store = new("Microsoft.WindowsStore_8wekyb3d8bbwe", 120, 12);

        ProgramUsageIndex index = new(
            [(@"C:\Program Files\Google\Chrome\Application\chrome.exe", chrome)],
            [(@"C:\Program Files\WindowsApps\Microsoft.WindowsStore_22508.1401.5.0_x64__8wekyb3d8bbwe", store)]);

        Assert.True(index.TryFind(@"c:\program files\google\chrome\application\chrome.exe", out AppUsageRecord found));
        Assert.Equal(chrome, found);

        Assert.True(index.TryFind(@"C:\Program Files\WindowsApps\Microsoft.WindowsStore_22508.1401.5.0_x64__8wekyb3d8bbwe\WinStore.App.exe", out found));
        Assert.Equal(store, found);

        // A folder whose name merely begins the same is not the package.
        Assert.False(index.TryFind(@"C:\Program Files\WindowsApps\Microsoft.WindowsStore_22508.1401.5.0_x64__8wekyb3d8bbwe2\x.exe", out _));
        Assert.False(index.TryFind(null, out _));
        Assert.True(ProgramUsageIndex.Empty.IsEmpty);
    }
}
