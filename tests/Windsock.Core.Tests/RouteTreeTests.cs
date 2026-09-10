using Windsock.Core.Networking;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class RouteTreeTests
{
    [Fact]
    public void PeersSharingTheirFirstHops_ShareOneBranch()
    {
        RouteNode root = RouteTree.Build(
        [
            new RouteLeaf("one", [Hop(1, "192.168.1.1"), Hop(2, "10.0.0.1"), Hop(3, "1.1.1.1", End)]),
            new RouteLeaf("two", [Hop(1, "192.168.1.1"), Hop(2, "10.0.0.1"), Hop(3, "9.9.9.9", End)]),
        ]);

        RouteNode gateway = Assert.Single(root.Children);
        RouteNode border = Assert.Single(gateway.Children);

        Assert.Equal("192.168.1.1", gateway.Address);
        Assert.Equal("10.0.0.1", border.Address);
        Assert.Equal(2, border.Children.Count);
        Assert.Equal(["one", "two"], border.Children.Select(child => child.LeafKey));
    }

    [Fact]
    public void APeerWithNoRoute_HangsOffTheProcess()
    {
        RouteNode root = RouteTree.Build([new RouteLeaf("quiet", [])]);

        RouteNode peer = Assert.Single(root.Children);

        Assert.Equal("quiet", peer.LeafKey);
        Assert.Null(peer.Address);
        Assert.Equal(1, peer.Distance);
    }

    [Fact]
    public void TheFarEnd_IsThePeersOwnNode()
    {
        RouteNode root = RouteTree.Build(
            [new RouteLeaf("far", [Hop(1, "192.168.1.1"), Hop(2, "1.1.1.1", End)])]);

        RouteNode gateway = Assert.Single(root.Children);
        RouteNode peer = Assert.Single(gateway.Children);

        Assert.Equal("far", peer.LeafKey);
        Assert.Equal("1.1.1.1", peer.Address);
        Assert.Empty(peer.Children);
    }

    [Fact]
    public void AStretchOfSilence_IsOneNode()
    {
        RouteNode root = RouteTree.Build(
            [new RouteLeaf("far", [Hop(1, "192.168.1.1"), Quiet(2), Quiet(3), Hop(4, "1.1.1.1", End)])]);

        RouteNode gateway = Assert.Single(root.Children);
        RouteNode quiet = Assert.Single(gateway.Children);
        RouteNode peer = Assert.Single(quiet.Children);

        Assert.True(quiet.IsSilent);
        Assert.Equal(2, quiet.Silences);
        Assert.Equal(3, quiet.Distance);
        Assert.Equal(4, peer.Distance);
    }

    [Fact]
    public void SilenceAtTheEndOfAWalkThatNeverArrived_IsNotDrawn()
    {
        RouteNode root = RouteTree.Build(
            [new RouteLeaf("far", [Hop(1, "192.168.1.1"), Quiet(2), Quiet(3)])]);

        RouteNode gateway = Assert.Single(root.Children);
        RouteNode peer = Assert.Single(gateway.Children);

        Assert.Equal("far", peer.LeafKey);
        Assert.False(peer.IsSilent);
    }

    [Fact]
    public void SilenceOnTheWayToAFarEndThatAnswered_IsKept()
    {
        RouteNode root = RouteTree.Build(
            [new RouteLeaf("far", [Quiet(1), Hop(2, "1.1.1.1", End)])]);

        RouteNode quiet = Assert.Single(root.Children);

        Assert.True(quiet.IsSilent);
        Assert.Equal("far", Assert.Single(quiet.Children).LeafKey);
    }

    [Fact]
    public void SilencesUnderDifferentParents_DoNotMerge()
    {
        RouteNode root = RouteTree.Build(
        [
            new RouteLeaf("one", [Hop(1, "10.0.0.1"), Quiet(2), Hop(3, "1.1.1.1", End)]),
            new RouteLeaf("two", [Hop(1, "10.0.0.2"), Quiet(2), Hop(3, "9.9.9.9", End)]),
        ]);

        Assert.Equal(2, root.Children.Count);
        Assert.All(root.Children, child => Assert.Single(child.Children));
    }

    [Fact]
    public void SilencesOfDifferentLengths_DoNotMerge()
    {
        // One unknown hop and three of them are not the same stretch of road.
        RouteNode root = RouteTree.Build(
        [
            new RouteLeaf("one", [Hop(1, "10.0.0.1"), Quiet(2), Hop(3, "1.1.1.1", End)]),
            new RouteLeaf("two", [Hop(1, "10.0.0.1"), Quiet(2), Quiet(3), Hop(4, "9.9.9.9", End)]),
        ]);

        RouteNode gateway = Assert.Single(root.Children);

        Assert.Equal(2, gateway.Children.Count);
        Assert.Equal([1, 2], gateway.Children.Select(child => child.Silences));
    }

    [Fact]
    public void ARouteThatLoops_StopsAtTheRepeat()
    {
        RouteNode root = RouteTree.Build(
            [new RouteLeaf("far", [Hop(1, "10.0.0.1"), Hop(2, "10.0.0.2"), Hop(3, "10.0.0.1"), Hop(4, "10.0.0.2")])]);

        RouteNode first = Assert.Single(root.Children);
        RouteNode second = Assert.Single(first.Children);
        RouteNode peer = Assert.Single(second.Children);

        Assert.Equal("10.0.0.1", first.Address);
        Assert.Equal("10.0.0.2", second.Address);
        Assert.Equal("far", peer.LeafKey);
        Assert.Empty(peer.Children);
    }

    [Fact]
    public void AGatewayThatIsAlsoAPeer_IsOneNode()
    {
        RouteNode root = RouteTree.Build(
        [
            new RouteLeaf("router", [Hop(1, "192.168.1.1", End)]),
            new RouteLeaf("far", [Hop(1, "192.168.1.1"), Hop(2, "1.1.1.1", End)]),
        ]);

        RouteNode gateway = Assert.Single(root.Children);

        Assert.Equal("router", gateway.LeafKey);
        Assert.Equal("192.168.1.1", gateway.Address);
        Assert.Equal("far", Assert.Single(gateway.Children).LeafKey);
    }

    [Fact]
    public void AGatewayMetAsAHopFirst_IsStillOneNode()
    {
        RouteNode root = RouteTree.Build(
        [
            new RouteLeaf("far", [Hop(1, "192.168.1.1"), Hop(2, "1.1.1.1", End)]),
            new RouteLeaf("router", [Hop(1, "192.168.1.1", End)]),
        ]);

        RouteNode gateway = Assert.Single(root.Children);

        Assert.Equal("router", gateway.LeafKey);
        Assert.Equal("far", Assert.Single(gateway.Children).LeafKey);
    }

    [Fact]
    public void TwoPeersAtTheSameAddress_AreBothDrawn()
    {
        RouteNode root = RouteTree.Build(
        [
            new RouteLeaf("one.example", [Hop(1, "10.0.0.1"), Hop(2, "1.1.1.1", End)]),
            new RouteLeaf("two.example", [Hop(1, "10.0.0.1"), Hop(2, "1.1.1.1", End)]),
        ]);

        RouteNode gateway = Assert.Single(root.Children);

        Assert.Equal(2, gateway.Children.Count);
        Assert.All(gateway.Children, child => Assert.Equal("1.1.1.1", child.Address));
    }

    [Fact]
    public void Reach_ListsEveryPeerBeyondANode()
    {
        RouteNode root = RouteTree.Build(
        [
            new RouteLeaf("one", [Hop(1, "10.0.0.1"), Hop(2, "1.1.1.1", End)]),
            new RouteLeaf("two", [Hop(1, "10.0.0.1"), Hop(2, "9.9.9.9", End)]),
            new RouteLeaf("three", []),
        ]);

        Assert.Equal(["one", "two", "three"], root.Reach);
        Assert.Equal(["one", "two"], root.Children[0].Reach);
        Assert.Equal(["three"], root.Children[1].Reach);
    }

    [Fact]
    public void ANodeThatIsAPeerAndAWay_ReachesItselfAndWhatIsBeyond()
    {
        RouteNode root = RouteTree.Build(
        [
            new RouteLeaf("router", [Hop(1, "192.168.1.1", End)]),
            new RouteLeaf("far", [Hop(1, "192.168.1.1"), Hop(2, "1.1.1.1", End)]),
        ]);

        Assert.Equal(["router", "far"], Assert.Single(root.Children).Reach);
    }

    [Fact]
    public void TheSameRouterOnTwoRoutes_KeepsItsQuickestAnswer()
    {
        RouteNode root = RouteTree.Build(
        [
            new RouteLeaf(
                "one",
                [new PathHop(1, "10.0.0.1", TimeSpan.FromMilliseconds(30), false), Hop(2, "1.1.1.1", End)]),
            new RouteLeaf(
                "two",
                [new PathHop(1, "10.0.0.1", TimeSpan.FromMilliseconds(8), false), Hop(2, "9.9.9.9", End)]),
        ]);

        Assert.Equal(TimeSpan.FromMilliseconds(8), Assert.Single(root.Children).RoundTrip);
    }

    [Fact]
    public void OnePeerNamedTwice_IsPlacedOnce()
    {
        RouteNode root = RouteTree.Build(
        [
            new RouteLeaf("one", [Hop(1, "1.1.1.1", End)]),
            new RouteLeaf("one", [Hop(1, "9.9.9.9", End)]),
        ]);

        Assert.Equal("1.1.1.1", Assert.Single(root.Children).Address);
    }

    [Fact]
    public void TheOrderPeersAreGivenIn_IsTheOrderTheyAreDrawnIn()
    {
        RouteNode root = RouteTree.Build(
            [new RouteLeaf("c", []), new RouteLeaf("a", []), new RouteLeaf("b", [])]);

        Assert.Equal(["c", "a", "b"], root.Children.Select(child => child.LeafKey));
    }

    [Fact]
    public void ARouterKeepsItsIdentity_WhileAPeerKeepsItsOwn()
    {
        RouteNode root = RouteTree.Build(
            [new RouteLeaf("far", [Hop(1, "10.0.0.1"), Hop(2, "1.1.1.1", End)])]);

        RouteNode gateway = Assert.Single(root.Children);

        Assert.Equal("/10.0.0.1", gateway.Id);
        Assert.Equal("peer:far", Assert.Single(gateway.Children).Id);
    }

    [Fact]
    public void NothingAtAll_IsJustTheProcess()
    {
        RouteNode root = RouteTree.Build([]);

        Assert.Empty(root.Children);
        Assert.Empty(root.Reach);
        Assert.Equal(0, root.Distance);
    }

    [Fact]
    public void Shared_OfOneRoute_IsThatRoute()
    {
        IReadOnlyList<PathHop> route = [Hop(1, "10.0.0.1"), Hop(2, "1.1.1.1", End)];

        Assert.Same(route, RouteTree.Shared([route]));
    }

    [Fact]
    public void Shared_StopsWhereTheRoutesPartCompany()
    {
        IReadOnlyList<PathHop> shared = RouteTree.Shared(
        [
            [Hop(1, "10.0.0.1"), Hop(2, "10.0.0.2"), Hop(3, "1.1.1.1", End)],
            [Hop(1, "10.0.0.1"), Hop(2, "10.0.0.9"), Hop(3, "9.9.9.9", End)],
        ]);

        Assert.Equal(["10.0.0.1"], shared.Select(hop => hop.Address));
    }

    [Fact]
    public void Shared_MarksTheEndOnlyWhereEveryRouteEndsThere()
    {
        // One route arriving at a router another passes through is a router.
        IReadOnlyList<PathHop> shared = RouteTree.Shared(
        [
            [Hop(1, "10.0.0.1"), Hop(2, "1.1.1.1", End)],
            [Hop(1, "10.0.0.1"), Hop(2, "1.1.1.1"), Hop(3, "1.0.0.1", End)],
        ]);

        Assert.Equal(2, shared.Count);
        Assert.False(shared[1].IsDestination);
    }

    [Fact]
    public void Shared_OfRoutesThatAgreeThroughout_KeepsTheEnd()
    {
        IReadOnlyList<PathHop> shared = RouteTree.Shared(
        [
            [Hop(1, "10.0.0.1"), Hop(2, "1.1.1.1", End)],
            [Hop(1, "10.0.0.1"), Hop(2, "1.1.1.1", End)],
        ]);

        Assert.Equal(2, shared.Count);
        Assert.True(shared[1].IsDestination);
    }

    [Fact]
    public void Shared_OfRoutesThatNeverAgree_IsNothing()
    {
        Assert.Empty(RouteTree.Shared(
        [
            [Hop(1, "10.0.0.1", End)],
            [Hop(1, "10.0.0.2", End)],
        ]));
    }

    [Fact]
    public void Shared_TreatsSilenceAsAgreeing()
    {
        IReadOnlyList<PathHop> shared = RouteTree.Shared(
        [
            [Quiet(1), Hop(2, "10.0.0.1"), Hop(3, "1.1.1.1", End)],
            [Quiet(1), Hop(2, "10.0.0.1"), Hop(3, "9.9.9.9", End)],
        ]);

        Assert.Equal(2, shared.Count);
        Assert.Null(shared[0].Address);
    }

    [Fact]
    public void Shared_OfNothing_IsNothing() => Assert.Empty(RouteTree.Shared([]));

    [Fact]
    public void APeerPlacedAtAFork_HangsOffTheLastSharedHop()
    {
        IReadOnlyList<PathHop> shared = RouteTree.Shared(
        [
            [Hop(1, "10.0.0.1"), Hop(2, "1.1.1.1", End)],
            [Hop(1, "10.0.0.1"), Hop(2, "9.9.9.9", End)],
        ]);

        RouteNode root = RouteTree.Build([new RouteLeaf("split.example", shared)]);
        RouteNode gateway = Assert.Single(root.Children);

        Assert.Equal("10.0.0.1", gateway.Address);
        Assert.Equal("split.example", Assert.Single(gateway.Children).LeafKey);
    }

    private const bool End = true;

    private static PathHop Hop(int distance, string address, bool destination = false) =>
        new(distance, address, TimeSpan.FromMilliseconds(distance), destination);

    private static PathHop Quiet(int distance) => new(distance, null, null, IsDestination: false);
}
