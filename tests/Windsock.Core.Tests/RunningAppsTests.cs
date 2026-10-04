using Windsock.Core.Apps;
using Windsock.Core.Processes;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class RunningAppsTests
{
    [Fact]
    public void AProcessRunningAPackagesOwnFile_IsThePackage()
    {
        ProcessIdentity store = new("WinStore.App", "Microsoft Store", @"C:\Program Files\WindowsApps\Microsoft.WindowsStore_22607.1401.9.0_x64__8wekyb3d8bbwe\WinStore.App.exe", null, "Microsoft.WindowsStore_8wekyb3d8bbwe");
        ProcessIdentity helper = store with { Name = "Helper", FilePath = @"C:\Program Files\WindowsApps\Microsoft.WindowsStore_22607.1401.9.0_x64__8wekyb3d8bbwe\Helper.exe" };

        Assert.Equal("package:Microsoft.WindowsStore_8wekyb3d8bbwe", RunningApps.KeyOf(store));
        Assert.Equal(RunningApps.KeyOf(store), RunningApps.KeyOf(helper));
        Assert.Equal(AppKind.Package, RunningApps.KindOf(RunningApps.KeyOf(store)));
    }

    [Fact]
    public void AProgram_IsItsFile_WhateverTheCaseOrTheProcess()
    {
        ProcessIdentity first = new("chrome", "Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", null);
        ProcessIdentity second = new("chrome", null, @"c:\program files\google\chrome\application\chrome.exe", null);
        ProcessIdentity other = new("chrome", null, @"D:\Portable\chrome.exe", null);

        Assert.True(RunningApps.KeyComparer.Equals(RunningApps.KeyOf(first), RunningApps.KeyOf(second)));
        Assert.False(RunningApps.KeyComparer.Equals(RunningApps.KeyOf(first), RunningApps.KeyOf(other)));
        Assert.Equal(AppKind.Program, RunningApps.KindOf(RunningApps.KeyOf(first)));
        Assert.True(RunningApps.IsCountable(RunningApps.KeyOf(first)));
    }

    [Fact]
    public void AProcessWhoseFileCannotBeRead_IsGatheredByName_AndNeverMatchedToWindows()
    {
        string key = RunningApps.KeyOf(new ProcessIdentity("svchost", null, null, null));

        Assert.Equal("process:svchost", key);
        Assert.Equal(AppKind.Unattributed, RunningApps.KindOf(key));
        Assert.False(RunningApps.IsCountable(key));
    }

    [Fact]
    public void AProcessWhoseFileCannotBeRead_JoinsTheOneProgramNamedLikeIt()
    {
        ProcessIdentity readable = new("svchost", "Host Process for Windows Services", @"C:\Windows\System32\svchost.exe", null);
        ProcessIdentity hidden = new("svchost", null, null, null);
        ProcessIdentity lonely = new("MsMpEng", null, null, null);
        ProcessIdentity nodeOne = new("node", null, @"C:\One\node.exe", null);
        ProcessIdentity nodeTwo = new("node", null, @"D:\Two\node.exe", null);
        ProcessIdentity nodeHidden = new("node", null, null, null);

        Dictionary<int, string> keys = RunningApps.KeysFor(
        [
            Usage(1, readable),
            Usage(2, hidden),
            Usage(3, lonely),
            Usage(4, nodeOne),
            Usage(5, nodeTwo),
            Usage(6, nodeHidden),
        ]);

        Assert.Equal(RunningApps.KeyOf(readable), keys[2]);
        Assert.Equal("process:MsMpEng", keys[3]);

        // Two programs share the name: which one it is cannot be said.
        Assert.Equal("process:node", keys[6]);
        Assert.NotEqual(keys[4], keys[5]);
    }

    private static ProcessUsage Usage(int processId, ProcessIdentity identity) =>
        new(processId, identity, 0, 0, 0, 0, 0, new SocketCounts(1, 0));

    [Theory]
    [InlineData("Microsoft.WindowsStore_22607.1401.9.0_x64__8wekyb3d8bbwe", "Microsoft.WindowsStore_8wekyb3d8bbwe")]
    [InlineData("Claude_1.52386.6.0_x64__pzs8sxrjxfjjc", "Claude_pzs8sxrjxfjjc")]
    [InlineData("App_1.0.0.0_neutral_split.scale-200_abc", "App_abc")]
    [InlineData("NotAFullName", null)]
    [InlineData("Too_many_parts_in_this_name_here", null)]
    [InlineData("_1.0_x64__abc", null)]
    public void APackageFullName_GivesItsFamily(string fullName, string? family) =>
        Assert.Equal(family, RunningApps.FamilyOf(fullName));

    [Fact]
    public void EndpointRates_AreKeptPerProcess_SoAProcessJoiningBringsNoBurst()
    {
        Clock clock = new(new DateTimeOffset(2026, 9, 15, 3, 0, 0, TimeSpan.Zero));
        Endpoints source = new();
        EndpointMeter meter = new(source, clock);
        EndpointKey far = EndpointKey.From(System.Net.IPAddress.Parse("203.0.113.9"), 443);

        source.Set(10, far, sent: 1_000, received: 5_000);
        Assert.All(meter.Measure([10]), reading => Assert.Equal(0, reading.SendBytesPerSecond));

        clock.Advance(TimeSpan.FromSeconds(2));
        source.Set(10, far, sent: 3_000, received: 9_000);

        // A second process of the same app joins, having moved a great deal already.
        source.Set(11, far, sent: 900_000_000, received: 900_000_000);

        IReadOnlyList<EndpointReading> readings = meter.Measure([10, 11]);

        EndpointReading first = Assert.Single(readings, reading => reading.ProcessId == 10);
        Assert.Equal(1_000, first.SendBytesPerSecond);
        Assert.Equal(2_000, first.ReceiveBytesPerSecond);

        EndpointReading joined = Assert.Single(readings, reading => reading.ProcessId == 11);
        Assert.Equal(0, joined.SendBytesPerSecond);
        Assert.Equal(900_000_000, joined.Total.BytesSent);

        // Gone from the set, gone from the meter: back later, it starts again.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.All(meter.Measure([10]), reading => Assert.Equal(10, reading.ProcessId));

        clock.Advance(TimeSpan.FromSeconds(1));
        source.Set(11, far, sent: 900_100_000, received: 900_000_000);
        Assert.Equal(0, Assert.Single(meter.Measure([11])).SendBytesPerSecond);
    }

    [Fact]
    public void AReadingTooSoonAfterTheLast_KeepsTheLastRates()
    {
        Clock clock = new(new DateTimeOffset(2026, 9, 15, 3, 0, 0, TimeSpan.Zero));
        Endpoints source = new();
        EndpointMeter meter = new(source, clock);
        EndpointKey far = EndpointKey.From(System.Net.IPAddress.Parse("203.0.113.9"), 443);

        source.Set(10, far, sent: 0, received: 0);
        meter.Measure([10]);

        clock.Advance(TimeSpan.FromSeconds(1));
        source.Set(10, far, sent: 5_000, received: 0);
        Assert.Equal(5_000, Assert.Single(meter.Measure([10])).SendBytesPerSecond);

        // A refresh a few milliseconds later: the rate stands, not zero.
        clock.Advance(TimeSpan.FromMilliseconds(30));
        source.Set(10, far, sent: 5_100, received: 0);
        Assert.Equal(5_000, Assert.Single(meter.Measure([10])).SendBytesPerSecond);

        // And the next proper reading measures from the last proper one.
        clock.Advance(TimeSpan.FromMilliseconds(970));
        source.Set(10, far, sent: 7_000, received: 0);
        Assert.Equal(2_000, Assert.Single(meter.Measure([10])).SendBytesPerSecond, 3);
    }

    [Fact]
    public void EndpointRates_AreNotOfferedWhereNothingAttributesThem()
    {
        EndpointMeter meter = new(new WithheldTrafficSource());

        Assert.Empty(meter.Measure([10, 11]));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Endpoints : IProcessEndpointSource
    {
        private readonly Dictionary<int, Dictionary<EndpointKey, EndpointTraffic>> _processes = [];

        public bool CanAttribute => true;

        public void Set(int processId, EndpointKey key, long sent, long received)
        {
            if (!_processes.TryGetValue(processId, out Dictionary<EndpointKey, EndpointTraffic>? endpoints))
            {
                endpoints = [];
                _processes[processId] = endpoints;
            }

            endpoints[key] = new EndpointTraffic(sent, received, 0, DateTimeOffset.UnixEpoch);
        }

        public void Watch(int processId)
        {
        }

        public void Unwatch(int processId)
        {
        }

        public void CopyEndpoints(int processId, Dictionary<EndpointKey, EndpointTraffic> destination)
        {
            destination.Clear();

            if (_processes.TryGetValue(processId, out Dictionary<EndpointKey, EndpointTraffic>? endpoints))
            {
                foreach ((EndpointKey key, EndpointTraffic traffic) in endpoints)
                {
                    destination[key] = traffic;
                }
            }
        }
    }
}
