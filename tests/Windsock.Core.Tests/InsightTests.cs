using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Windsock.Core.Insight;
using Windsock.Core.Processes;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class InsightTests
{
    [Fact]
    public void AServiceHostIsCalledByItsOneService()
    {
        ServiceHostMap map = new(
        [
            new HostedService("DoSvc", "Delivery Optimization", 1200),
            new HostedService("BFE", "Base Filtering Engine", 900),
            new HostedService("mpssvc", "Windows Defender Firewall", 900),
            new HostedService("Stopped", "Not running", 0),
        ]);

        ServiceHostLabel? alone = map.Describe(1200);
        Assert.NotNull(alone);
        Assert.Equal("Delivery Optimization", alone.Text);
        Assert.False(alone.IsShared);

        ServiceHostLabel? shared = map.Describe(900);
        Assert.NotNull(shared);
        Assert.True(shared.IsShared);
        Assert.Equal("Base Filtering Engine and Windows Defender Firewall", shared.Text);
        Assert.Equal("BFE,mpssvc", map.KeyOf(900));

        Assert.Null(map.Describe(4));
        Assert.Equal(1200, map.ProcessOf("dosvc"));
        Assert.Null(map.ProcessOf("Stopped"));
        Assert.Equal(2, map.Hosts);
    }

    [Theory]
    [InlineData(new[] { "A" }, "A")]
    [InlineData(new[] { "A", "B" }, "A and B")]
    [InlineData(new[] { "A", "B", "C" }, "A, B and C")]
    [InlineData(new[] { "A", "B", "C", "D", "E" }, "A, B and 3 more")]
    public void ASharedHostsNamesAreJoinedShortly(string[] names, string expected) =>
        Assert.Equal(expected, ServiceHostMap.Join(names));

    [Fact]
    public void TheServiceManagerAnswersAStandardUser()
    {
        IReadOnlyList<HostedService> services = new WindowsServiceSource().Read();

        // The RPC service runs on every Windows there is.
        Assert.Contains(services, each => string.Equals(each.Name, "RpcSs", StringComparison.OrdinalIgnoreCase) && each.ProcessId > 0);
        Assert.All(services, each => Assert.False(string.IsNullOrWhiteSpace(each.DisplayName)));
    }

    [Fact]
    public void AStaleReadingIsReadAgain()
    {
        CountingServices source = new();
        InsightClock clock = new(DateTimeOffset.UnixEpoch);
        ServiceHosts hosts = new(source, clock);

        _ = hosts.Current;
        _ = hosts.Current;
        Assert.Equal(1, source.Reads);

        clock.Advance(TimeSpan.FromSeconds(31));
        _ = hosts.Current;
        Assert.Equal(2, source.Reads);

        hosts.Invalidate();
        _ = hosts.Current;
        Assert.Equal(3, source.Reads);
    }

    [Fact]
    public void ASocketIsPutToTheModuleThatOpenedIt()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient client = new();
        client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using TcpClient accepted = listener.AcceptTcpClient();

        IReadOnlyDictionary<string, SocketOwner> owners = new SocketOwners().Read(Environment.ProcessId);

        string expected = Path.GetFileName(Environment.ProcessPath!);
        Assert.NotEmpty(owners);
        Assert.Contains(owners.Values, owner => string.Equals(owner.Module, expected, StringComparison.OrdinalIgnoreCase));

        // The keys are the connection table's own, so a page can join the two.
        HashSet<string> keys = [.. new IpHelperConnectionDetails().Read(Environment.ProcessId).Select(each => each.Key)];
        Assert.Contains(owners.Keys, keys.Contains);
    }

    [Fact]
    public void ProcessListEntriesAreReadAtTheirPublishedOffsets()
    {
        // Two SYSTEM_PROCESS_INFORMATION entries laid out as phnt's ntexapi.h
        // declares them on x64, image names in the same buffer.
        byte[] buffer = new byte[1024];
        GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        try
        {
            nint start = pin.AddrOfPinnedObject();

            WriteEntry(buffer, start, at: 0, next: 300, processId: 4321, parentId: 1000, created: 133_000_000_000_000_000, session: 1, name: "child.exe", nameAt: 900);
            WriteEntry(buffer, start, at: 300, next: 0, processId: 1000, parentId: 600, created: 132_000_000_000_000_000, session: 1, name: "parent.exe", nameAt: 960);

            List<KernelProcess> entries = ProcessTree.Decode(buffer, start);

            Assert.Equal(2, entries.Count);
            Assert.Equal(new KernelProcess(4321, 1000, 133_000_000_000_000_000, "child.exe", 1), entries[0]);
            Assert.Equal(new KernelProcess(1000, 600, 132_000_000_000_000_000, "parent.exe", 1), entries[1]);
        }
        finally
        {
            pin.Free();
        }
    }

    [Fact]
    public void AParentThatStartedAfterItsChildHoldsAReusedId()
    {
        ProcessTree tree = new(
        [
            new KernelProcess(10, 4, 100, "parent.exe", 1),
            new KernelProcess(20, 10, 200, "child.exe", 1),

            // 30's parent had id 10 too, but 30 is older than the 10 running now.
            new KernelProcess(30, 10, 50, "orphan.exe", 1),
            new KernelProcess(40, 99, 300, "no-parent.exe", 1),
        ]);

        ParentProcess? parent = tree.ParentOf(20);
        Assert.NotNull(parent);
        Assert.True(parent.IsRunning);
        Assert.Equal("parent.exe", parent.Entry!.Value.ImageName);

        ParentProcess? reused = tree.ParentOf(30);
        Assert.NotNull(reused);
        Assert.False(reused.IsRunning);
        Assert.Equal(10, reused.ProcessId);

        Assert.False(tree.ParentOf(40)!.IsRunning);
        Assert.Null(tree.ParentOf(4));
        Assert.Null(tree.ParentOf(12345));

        // Up to the first ancestor that is no longer running, which ends the chain.
        IReadOnlyList<ParentProcess> chain = tree.AncestorsOf(20);
        Assert.Equal([10, 4], chain.Select(each => each.ProcessId));
        Assert.False(chain[^1].IsRunning);
    }

    [Fact]
    public void TheKernelListNamesThisProcessAndItsParent()
    {
        ProcessTree tree = ProcessTree.Read();

        Assert.True(tree.Count > 10);
        KernelProcess? me = tree.Find(Environment.ProcessId);
        Assert.NotNull(me);
        Assert.Equal(Path.GetFileName(Environment.ProcessPath!), me.Value.ImageName, ignoreCase: true);

        // Process.StartTime comes from the same kernel field.
        using Process current = Process.GetCurrentProcess();
        Assert.Equal(current.StartTime.ToUniversalTime(), me.Value.StartedAt!.Value.UtcDateTime, TimeSpan.FromMilliseconds(1));

        Assert.NotNull(tree.ParentOf(Environment.ProcessId));
    }

    [Fact]
    public void WindowsAndWindsockAreNeverOffered()
    {
        Assert.Equal(EndOutcome.NotAllowed, ProcessEnder.Probe(new EndTarget(4, "System", null)).Outcome);
        Assert.Equal(EndOutcome.NotAllowed, ProcessEnder.Probe(new EndTarget(Environment.ProcessId, "testhost", null)).Outcome);

        foreach (Process csrss in Process.GetProcessesByName("csrss"))
        {
            using (csrss)
            {
                Assert.Equal(EndOutcome.NotAllowed, ProcessEnder.Probe(new EndTarget(csrss.Id, "csrss.exe", null)).Outcome);
            }
        }
    }

    [Fact]
    public void AProcessThatHasGoneIsSaidToHaveGone()
    {
        using Process quick = StartWaiting();
        int id = quick.Id;
        quick.Kill();
        quick.WaitForExit();

        // The id may be reused at once; the start time says it is not ours.
        EndResult result = ProcessEnder.Probe(new EndTarget(id, "ping.exe", DateTimeOffset.Now.AddDays(-1)));
        Assert.Equal(EndOutcome.AlreadyGone, result.Outcome);
    }

    [Fact]
    public void ThePersonsOwnProcessIsEnded()
    {
        using Process mine = StartWaiting();
        EndTarget target = new(mine.Id, "ping.exe", new DateTimeOffset(mine.StartTime));

        Assert.Equal(EndOutcome.CanEnd, ProcessEnder.Probe(target).Outcome);
        Assert.False(mine.HasExited);

        IReadOnlyList<EndResult> results = ProcessEnder.End([target], TimeSpan.FromSeconds(5));

        Assert.Equal(EndOutcome.Ended, Assert.Single(results).Outcome);
        Assert.True(mine.WaitForExit(5000));
    }

    [Fact]
    public void WithoutAStartTimeNothingIsEnded()
    {
        // A window following an id cannot be sure the id is still its process,
        // so no start time means no ending - by probe or for real.
        using Process mine = StartWaiting();
        EndTarget unconfirmed = new(mine.Id, "ping.exe", null);

        Assert.Equal(EndOutcome.NotConfirmed, ProcessEnder.Probe(unconfirmed).Outcome);
        Assert.Equal(EndOutcome.NotConfirmed, Assert.Single(ProcessEnder.End([unconfirmed], TimeSpan.FromSeconds(1))).Outcome);
        Assert.False(mine.HasExited);

        mine.Kill();
        mine.WaitForExit();
    }

    [Fact]
    public void EndingSeveralSharesOneWait()
    {
        using Process first = StartWaiting();
        using Process second = StartWaiting();

        IReadOnlyList<EndResult> results = ProcessEnder.End(
            [
                new EndTarget(first.Id, "ping.exe", new DateTimeOffset(first.StartTime)),
                new EndTarget(second.Id, "ping.exe", new DateTimeOffset(second.StartTime), @"C:\Windows\System32\PING.EXE"),
            ],
            TimeSpan.FromSeconds(5));

        Assert.All(results, result => Assert.Equal(EndOutcome.Ended, result.Outcome));
        Assert.Equal(@"C:\Windows\System32\PING.EXE", results[1].Target.Path);
    }

    [Theory]
    [InlineData("svchost", true)]
    [InlineData("svchost.exe", true)]
    [InlineData(@"C:\Windows\System32\SVCHOST.EXE", true)]
    [InlineData("Secure System", true)]
    [InlineData("csrss", true)]
    [InlineData("PID 1234", false)]
    [InlineData("Delivery Optimization", false)]
    [InlineData(@"C:\Program Files\App\app.exe", false)]
    [InlineData(null, false)]
    public void WhatWindowsCannotDoWithoutIsKnownByNameOrFile(string? nameOrPath, bool essential) =>
        Assert.Equal(essential, EssentialProcesses.IsEssential(nameOrPath));

    [Fact]
    public void TheServicesWindowsNeedsForTheNetworkAreKnown()
    {
        Assert.True(EssentialProcesses.IsNetworkService("Dnscache"));
        Assert.True(EssentialProcesses.IsNetworkService("DHCP"));
        Assert.False(EssentialProcesses.IsNetworkService("DoSvc"));
        Assert.False(EssentialProcesses.IsNetworkService(null));
    }

    [Fact]
    public void AProcessUnderAnotherAccountNeedsAnAdministrator()
    {
        if (Environment.IsPrivilegedProcess)
        {
            return;
        }

        // The spooler runs as SYSTEM. Probing opens and closes; nothing is ended.
        Process? spooler = Process.GetProcessesByName("spoolsv").FirstOrDefault();

        if (spooler is null)
        {
            return;
        }

        using (spooler)
        {
            Assert.Equal(EndOutcome.NeedsAdministrator, ProcessEnder.Probe(new EndTarget(spooler.Id, "spoolsv.exe", null)).Outcome);
        }
    }

    [Fact]
    public void AListenerIsFoundWithWhoCanReachIt()
    {
        using TcpListener local = new(IPAddress.Loopback, 0);
        local.Start();
        int port = ((IPEndPoint)local.LocalEndpoint).Port;

        IReadOnlyList<ListeningSocket> sockets = Listeners.Read();

        ListeningSocket mine = Assert.Single(sockets, each => each.ProcessId == Environment.ProcessId && each.Port == port && each.Protocol == TransportProtocol.Tcp);
        Assert.Equal(ListenReach.ThisPcOnly, mine.Reach);
        Assert.Equal("127.0.0.1", mine.LocalAddress);
    }

    [Theory]
    [InlineData("127.0.0.1", ListenReach.ThisPcOnly)]
    [InlineData("::1", ListenReach.ThisPcOnly)]
    [InlineData("0.0.0.0", ListenReach.AllNetworks)]
    [InlineData("::", ListenReach.AllNetworks)]
    [InlineData("192.168.1.20", ListenReach.OneAddress)]
    public void ReachFollowsTheBoundAddress(string address, ListenReach reach) =>
        Assert.Equal(reach, Listeners.ReachOf(address));

    [Fact]
    public void AnOutgoingUdpPortIsMarkedAsAClientEnd()
    {
        Assert.True(new ListeningSocket(1, TransportProtocol.Udp, false, "0.0.0.0", 53000).IsLikelyClientEnd);
        Assert.False(new ListeningSocket(1, TransportProtocol.Udp, false, "0.0.0.0", 5353).IsLikelyClientEnd);
        Assert.False(new ListeningSocket(1, TransportProtocol.Tcp, false, "0.0.0.0", 53000).IsLikelyClientEnd);
    }

    [Theory]
    [InlineData(0x0050u, 20480)]
    [InlineData(0x5000u, 80)]
    [InlineData(0xBB01u, 443)]
    public void PortsAreReadInNetworkOrder(uint stored, int port) =>
        Assert.Equal(port, Listeners.Port(stored));

    [Fact]
    public void TheOutsideContactsAndTheTimelineStartOff()
    {
        Settings.WindsockSettings fresh = new();

        Assert.False(fresh.Insight.SpamhausDrop);
        Assert.False(fresh.Insight.FeodoTracker);
        Assert.False(fresh.Insight.Timeline);
        Assert.False(fresh.Insight.NewAppNotices);
        Assert.Equal(30, fresh.Insight.KeptDays);

        fresh.Insight.TimelineDays = 40;
        Assert.Equal(30, fresh.Insight.KeptDays);
        fresh.Insight.TimelineDays = 10_000;
        Assert.Equal(365, fresh.Insight.KeptDays);

        string folder = Path.Combine(Path.GetTempPath(), "windsock-insight-settings-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(folder, "settings.json");

        try
        {
            fresh.Insight.Timeline = true;
            fresh.Insight.TimelineDays = 7;
            new Settings.SettingsStore(file).Save(fresh);

            Assert.DoesNotContain("KeptDays", File.ReadAllText(file), StringComparison.Ordinal);

            Settings.WindsockSettings read = new Settings.SettingsStore(file).Load();
            Assert.True(read.Insight.Timeline);
            Assert.Equal(7, read.Insight.KeptDays);

            // The timeline is this PC's record, and stays with it on an
            // import; the new-app alert is a choice, and travels.
            read.Insight.NewAppNotices = true;
            Settings.WindsockSettings other = new();
            other.CopyFrom(read);
            Assert.True(other.Insight.NewAppNotices);
            Assert.False(other.Insight.Timeline);
            Assert.NotSame(read.Insight, other.Insight);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static Process StartWaiting() =>
        Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;

    private static void WriteEntry(byte[] buffer, nint start, int at, int next, int processId, int parentId, long created, int session, string name, int nameAt)
    {
        Span<byte> entry = buffer.AsSpan(at);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[ProcessTree.NextEntryOffsetAt..], (uint)next);
        BinaryPrimitives.WriteInt64LittleEndian(entry[ProcessTree.CreateTimeAt..], created);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[ProcessTree.ImageNameLengthAt..], (ushort)(name.Length * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(entry[(ProcessTree.ImageNameLengthAt + 2)..], (ushort)((name.Length + 1) * 2));
        BinaryPrimitives.WriteInt64LittleEndian(entry[ProcessTree.ImageNameBufferAt..], start + nameAt);
        BinaryPrimitives.WriteInt64LittleEndian(entry[ProcessTree.ProcessIdAt..], processId);
        BinaryPrimitives.WriteInt64LittleEndian(entry[ProcessTree.ParentIdAt..], parentId);
        BinaryPrimitives.WriteInt32LittleEndian(entry[ProcessTree.SessionIdAt..], session);
        MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(buffer.AsSpan(nameAt));
    }

    private sealed class CountingServices : IServiceHostSource
    {
        public int Reads { get; private set; }

        public IReadOnlyList<HostedService> Read()
        {
            Reads++;
            return [];
        }
    }
}
