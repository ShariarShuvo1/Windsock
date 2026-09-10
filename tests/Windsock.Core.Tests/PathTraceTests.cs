using System.Net.NetworkInformation;
using Windsock.Core.Networking;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class PathTraceTests
{
    [Fact]
    public void AnExpiredHopLimit_MeansCarryOn() =>
        Assert.Equal(HopOutcome.Onward, PathTrace.Read(IPStatus.TtlExpired));

    [Fact]
    public void AReply_MeansTheFarEndAnswered() =>
        Assert.Equal(HopOutcome.Arrived, PathTrace.Read(IPStatus.Success));

    [Fact]
    public void NoAnswer_IsSilenceRatherThanAnEnding() =>
        Assert.Equal(HopOutcome.Silent, PathTrace.Read(IPStatus.TimedOut));

    [Theory]
    [InlineData(IPStatus.DestinationHostUnreachable)]
    [InlineData(IPStatus.DestinationNetworkUnreachable)]
    [InlineData(IPStatus.DestinationProhibited)]
    [InlineData(IPStatus.BadRoute)]
    public void AnAnswerThatRefuses_EndsTheRoute(IPStatus status) =>
        Assert.Equal(HopOutcome.Blocked, PathTrace.Read(status));

    [Fact]
    public void TrailingSilences_AreDropped()
    {
        PathHop[] walked =
        [
            new PathHop(1, "192.168.1.1", TimeSpan.FromMilliseconds(1), IsDestination: false),
            new PathHop(2, "10.0.0.1", TimeSpan.FromMilliseconds(9), IsDestination: false),
            new PathHop(3, null, null, IsDestination: false),
            new PathHop(4, null, null, IsDestination: false),
        ];

        IReadOnlyList<PathHop> settled = PathTrace.Trim(walked);

        Assert.Equal(2, settled.Count);
        Assert.Equal("10.0.0.1", settled[^1].Address);
    }

    [Fact]
    public void ASilenceInTheMiddle_Stays()
    {
        PathHop[] walked =
        [
            new PathHop(1, "192.168.1.1", TimeSpan.FromMilliseconds(1), IsDestination: false),
            new PathHop(2, null, null, IsDestination: false),
            new PathHop(3, "1.1.1.1", TimeSpan.FromMilliseconds(14), IsDestination: true),
        ];

        Assert.Equal(3, PathTrace.Trim(walked).Count);
    }

    [Fact]
    public void AWalkThatArrived_IsLeftAlone()
    {
        PathHop[] walked =
        [
            new PathHop(1, "192.168.1.1", TimeSpan.FromMilliseconds(1), IsDestination: false),
            new PathHop(2, "1.1.1.1", TimeSpan.FromMilliseconds(14), IsDestination: true),
        ];

        Assert.Same(walked, PathTrace.Trim(walked));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.10.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("fe80::1")]
    public void SomewhereThereIsAWayTo_IsWorthWalking(string address) =>
        Assert.True(PathTrace.Walkable(address));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.251")]
    [InlineData("239.255.255.250")]
    [InlineData("ff02::fb")]
    public void SomewhereWithNoRouteToWalk_IsNot(string address) =>
        Assert.False(PathTrace.Walkable(address));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not an address")]
    public void SomethingThatIsNotAnAddress_IsNot(string? address) =>
        Assert.False(PathTrace.Walkable(address));

    [Fact]
    public void AWalkThatFoundNothing_IsEmpty()
    {
        PathHop[] walked =
        [
            new PathHop(1, null, null, IsDestination: false),
            new PathHop(2, null, null, IsDestination: false),
        ];

        Assert.Empty(PathTrace.Trim(walked));
    }
}
