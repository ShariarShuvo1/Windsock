using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using Windsock.Core.Processes;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class ProcessSourceTests
{
    [Fact]
    public void ConnectionSource_ReadsTheSocketTablesWithoutPrivileges()
    {
        var source = new IpHelperConnectionSource();
        Dictionary<int, SocketCounts> counts = [];

        source.CopyTo(counts);
        Assert.All(counts, entry =>
        {
            Assert.True(entry.Key > 0, $"process id {entry.Key} is not valid");
            Assert.True(entry.Value.Total > 0, $"process {entry.Key} was listed with no sockets");
            Assert.True(entry.Value.Tcp >= 0 && entry.Value.Udp >= 0, "socket counts cannot be negative");
        });
    }

    [Fact]
    public void ConnectionSource_ReplacesRatherThanAccumulatesBetweenReads()
    {
        var source = new IpHelperConnectionSource();
        Dictionary<int, SocketCounts> counts = [];
        counts[-1] = new SocketCounts(9, 9);

        source.CopyTo(counts);

        Assert.DoesNotContain(-1, counts.Keys);
    }

    [Fact]
    public void ConnectionDetails_ReadAll_ReportsMoreThanOneProcess()
    {
        var source = new IpHelperConnectionDetails();

        IReadOnlyList<ProcessConnection> all = source.ReadAll();
        Assert.NotEmpty(all);
        Assert.True(
            all.Select(row => row.ProcessId).Distinct().Count() > 1,
            "the whole table came back owned by a single process");
    }

    [Fact]
    public void ConnectionDetails_ReadAll_FilesASocketUnderTheProcessThatOwnsIt()
    {
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        var source = new IpHelperConnectionDetails();

        IReadOnlyList<ProcessConnection> all = source.ReadAll();
        Assert.Contains(
            all,
            row => row.ProcessId == Environment.ProcessId
                && row.LocalPort == port
                && row.Protocol == TransportProtocol.Tcp);
    }

    [Fact]
    public void ConnectionDetails_ReadingOneProcess_StillReturnsOnlyThatProcess()
    {
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        var source = new IpHelperConnectionDetails();

        IReadOnlyList<ProcessConnection> mine = source.Read(Environment.ProcessId);

        Assert.NotEmpty(mine);
        Assert.All(mine, row => Assert.Equal(Environment.ProcessId, row.ProcessId));
    }

    [Fact]
    public void TrafficSource_KeepsWatchingWhileAnyoneStillWants()
    {
        using var source = new EtwProcessTrafficSource();
        int process = Environment.ProcessId;
        source.Watch(process);
        source.Watch(process);

        Assert.True(source.IsWatching(process));

        // One of them closes. The other is still looking at it.
        source.Unwatch(process);
        Assert.True(source.IsWatching(process));

        source.Unwatch(process);
        Assert.False(source.IsWatching(process));
    }

    [Fact]
    public void TrafficSource_UnwatchingSomethingNobodyWatches_DoesNothing()
    {
        using var source = new EtwProcessTrafficSource();

        source.Unwatch(Environment.ProcessId);
        Assert.False(source.IsWatching(Environment.ProcessId));
        source.Watch(Environment.ProcessId);
        source.Unwatch(Environment.ProcessId);

        Assert.False(source.IsWatching(Environment.ProcessId));
    }

    [Fact]
    public void TrafficSource_WithoutElevation_SaysSoRatherThanThrowing()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            return;
        }

        using var source = new EtwProcessTrafficSource();

        source.Start();
        Assert.Equal(TrafficSourceState.RequiresElevation, source.State);
        Assert.False(string.IsNullOrWhiteSpace(source.Detail));

        Dictionary<int, ProcessTraffic> totals = [];
        source.CopyTo(totals);
        Assert.Empty(totals);
    }
}
