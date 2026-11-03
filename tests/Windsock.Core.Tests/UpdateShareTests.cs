using Windsock.Core.Apps;
using Windsock.Core.Insight;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class UpdateShareTests
{
    [Theory]
    [InlineData(@"\device\harddiskvolume3\windows\uus\amd64\mousocoreworker.exe", true)]
    [InlineData(@"\device\harddiskvolume3\windows\uus\amd64\waasmedicagent.exe", true)]
    [InlineData(@"\device\harddiskvolume3\windows\system32\sihclient.exe", true)]
    [InlineData(@"\device\harddiskvolume3\Windows\System32\UsoClient.exe", true)]
    [InlineData(@"\device\harddiskvolume3\program files (x86)\microsoft\edgeupdate\microsoftedgeupdate.exe", false)]
    [InlineData(@"\device\harddiskvolume3\users\me\downloads\mousocoreworker.exe", false)]
    [InlineData("Microsoft.WindowsStore_8wekyb3d8bbwe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void UpdateProgramsAreWindowsOwnFiles(string? id, bool update) =>
        Assert.Equal(update, WindowsUpdateParts.IsUpdateProgram(id));

    [Theory]
    [InlineData("DoSvc", true)]
    [InlineData("wuauserv", true)]
    [InlineData("bits", true)]
    [InlineData("Dnscache", false)]
    [InlineData(null, false)]
    public void UpdateServicesAreKnownByName(string? name, bool update) =>
        Assert.Equal(update, WindowsUpdateParts.IsUpdateService(name));

    [Fact]
    public void DeliveryOptimizationAnswersAStandardUser()
    {
        // Settings' Activity monitor reads the same figures; on 2026-10-05 this
        // PC had downloaded 248 MB from Microsoft and 35 MB from a cache host.
        DeliveryOptimizationMonth? month = new WmiDeliveryOptimizationSource().ReadThisMonth();

        Assert.NotNull(month);
        Assert.True(month.Downloaded >= 0);
        Assert.True(month.OverInternet <= month.Downloaded + month.Uploaded);
    }

    [Fact]
    public void TheMonthPutsEverySourceTogether()
    {
        DateTimeOffset now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        DeliveryOptimizationMonth deliveryOptimization = new(248_000_000, 35_000_000, 1_000, 2_000, 3_000, 4_000);
        DateTimeOffset? askedFrom = null;

        WindowsUpdateShare share = new(
            new Fixed(deliveryOptimization),
            (from, until) =>
            {
                askedFrom = from;
                return
                [
                    new AppUsageTotal(@"\device\harddiskvolume3\windows\uus\amd64\mousocoreworker.exe", 2_000_000, 500_000),
                    new AppUsageTotal(@"\device\harddiskvolume3\program files\browser\browser.exe", 9_000_000, 1_000_000),
                ];
            },
            new Services(),
            new InsightClock(now));

        UpdateShareReport report = share.Read();

        Assert.Same(deliveryOptimization, report.DeliveryOptimization);
        Assert.Equal(2_000_000, report.ProgramsReceived);
        Assert.Equal(500_000, report.ProgramsSent);
        Assert.Equal(1, report.MonthStart.Day);
        Assert.Equal(report.MonthStart, askedFrom);

        // Only update services' hosts, a shared host counting if it runs one.
        Assert.Equal(["DoSvc", "BITS,Schedule"], report.MeasuredServices!.Select(each => each.ServiceKey));

        Assert.Equal(248_000_000 + 2_000 + 4_000, deliveryOptimization.OverInternet);
    }

    private sealed class Fixed(DeliveryOptimizationMonth month) : IDeliveryOptimizationSource
    {
        public DeliveryOptimizationMonth? ReadThisMonth() => month;
    }

    private sealed class Services : IServiceTrafficStore
    {
        public IReadOnlyList<ServiceBytes> ReadServiceTotals(DateTimeOffset from, DateTimeOffset until) =>
        [
            new ServiceBytes("DoSvc", 100, 10),
            new ServiceBytes("Dnscache", 5, 5),
            new ServiceBytes("BITS,Schedule", 50, 5),
        ];
    }
}
