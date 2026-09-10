using System.Diagnostics;
using System.Runtime.CompilerServices;
using Windsock.Core.Networking;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class NetworkRouteCacheTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(20);

    [Fact]
    public async Task AWantedAddress_IsWalkedAndRemembered()
    {
        Probe probe = new();
        probe.Routes["1.1.1.1"] = [Hop(1, "10.0.0.1"), Hop(2, "1.1.1.1", End)];

        using NetworkRouteCache cache = Make(probe);
        cache.Want(this, ["1.1.1.1"]);

        await Settled(cache, "1.1.1.1", RouteState.Found);

        NetworkRoute route = cache.Route("1.1.1.1")!;

        Assert.Equal(2, route.Hops.Count);
        Assert.Equal("1.1.1.1", route.Hops[^1].Address);
    }

    [Fact]
    public async Task ARouteAlreadyKnown_IsNotWalkedAgainWhileItIsFresh()
    {
        Probe probe = new();
        probe.Routes["1.1.1.1"] = [Hop(1, "1.1.1.1", End)];

        using NetworkRouteCache cache = Make(probe);
        cache.Want(this, ["1.1.1.1"]);

        await Settled(cache, "1.1.1.1", RouteState.Found);

        cache.Want(this, ["1.1.1.1"]);
        cache.Want(this, ["1.1.1.1"]);

        Assert.Equal(1, probe.Walks);
    }

    [Fact]
    public async Task ARouteOldEnoughToDoubt_IsWalkedAgain()
    {
        Probe probe = new();
        probe.Routes["1.1.1.1"] = [Hop(1, "1.1.1.1", End)];

        Clock clock = new();
        using NetworkRouteCache cache = Make(probe, clock);
        cache.Want(this, ["1.1.1.1"]);

        await Settled(cache, "1.1.1.1", RouteState.Found);

        clock.Now += Freshness;
        cache.Want(this, ["1.1.1.1"]);

        await Until(() => probe.Walks == 2, "the route to be walked again");
    }

    [Fact]
    public async Task AStaleRoute_IsStillHandedOutWhileItIsWalkedAgain()
    {
        Probe probe = new();
        probe.Routes["1.1.1.1"] = [Hop(1, "10.0.0.1"), Hop(2, "1.1.1.1", End)];

        Clock clock = new();
        using NetworkRouteCache cache = Make(probe, clock);
        cache.Want(this, ["1.1.1.1"]);

        await Settled(cache, "1.1.1.1", RouteState.Found);

        probe.Hold = new TaskCompletionSource();
        clock.Now += Freshness;
        cache.Want(this, ["1.1.1.1"]);

        await Until(() => cache.Walking == 1, "the second walk to start");

        NetworkRoute route = cache.Route("1.1.1.1")!;

        Assert.Equal(RouteState.Found, route.State);
        Assert.Equal(2, route.Hops.Count);

        probe.Hold.SetResult();
    }

    [Fact]
    public async Task AnAddressThatAnswersNothing_IsUnreachableAndLeftAlone()
    {
        Probe probe = new();

        using NetworkRouteCache cache = Make(probe);
        cache.Want(this, ["1.1.1.1"]);

        await Settled(cache, "1.1.1.1", RouteState.Unreachable);

        cache.Want(this, ["1.1.1.1"]);

        Assert.Equal(1, probe.Walks);
    }

    [Fact]
    public async Task AnAddressThatAnsweredNothing_IsTriedAgainAfterItsPatience()
    {
        Probe probe = new();

        Clock clock = new();
        using NetworkRouteCache cache = Make(probe, clock);
        cache.Want(this, ["1.1.1.1"]);

        await Settled(cache, "1.1.1.1", RouteState.Unreachable);

        clock.Now += Patience;
        cache.Want(this, ["1.1.1.1"]);

        await Until(() => probe.Walks == 2, "the address to be tried again");
    }

    [Fact]
    public async Task EachSilenceInARow_DoublesTheWait()
    {
        Probe probe = new();

        Clock clock = new();
        using NetworkRouteCache cache = Make(probe, clock);
        cache.Want(this, ["1.1.1.1"]);

        await Settled(cache, "1.1.1.1", RouteState.Unreachable);

        clock.Now += Patience;
        cache.Want(this, ["1.1.1.1"]);

        await Until(() => probe.Walks == 2, "the second attempt");
        await Settled(cache, "1.1.1.1", RouteState.Unreachable);

        clock.Now += Patience;
        cache.Want(this, ["1.1.1.1"]);

        Assert.Equal(2, probe.Walks);

        clock.Now += Patience;
        cache.Want(this, ["1.1.1.1"]);

        await Until(() => probe.Walks == 3, "the third attempt");
    }

    [Fact]
    public async Task NoMoreThanTheBudget_IsWalkedAtOnce()
    {
        Probe probe = new() { Hold = new TaskCompletionSource() };

        using NetworkRouteCache cache = new(probe, new Clock()) { Budget = 2 };
        cache.Want(this, ["1.1.1.1", "2.2.2.2", "3.3.3.3", "4.4.4.4"]);

        await Until(() => probe.Walks == 2, "two walks to start");

        Assert.Equal(2, cache.Walking);

        probe.Hold.SetResult();

        await Until(() => probe.Walks == 4, "the rest to follow");
    }

    [Fact]
    public async Task ThePriorityOrder_DecidesWhatIsWalkedFirst()
    {
        Probe probe = new() { Hold = new TaskCompletionSource() };

        using NetworkRouteCache cache = new(probe, new Clock()) { Budget = 1 };
        cache.Want(this, ["9.9.9.9", "1.1.1.1"]);

        await Until(() => probe.Walks == 1, "the first walk");

        Assert.Equal(["9.9.9.9"], probe.Walked);

        probe.Hold.SetResult();
    }

    [Fact]
    public async Task AnAddressBothAskersWant_IsWalkedOnce()
    {
        Probe probe = new();
        probe.Routes["1.1.1.1"] = [Hop(1, "1.1.1.1", End)];

        object other = new();
        using NetworkRouteCache cache = Make(probe);

        cache.Want(this, ["1.1.1.1"]);
        cache.Want(other, ["1.1.1.1"]);

        await Settled(cache, "1.1.1.1", RouteState.Found);

        Assert.Equal(1, probe.Walks);
    }

    [Fact]
    public async Task AnAddressNothingWantsAnyMore_IsAbandoned()
    {
        Probe probe = new() { Hold = new TaskCompletionSource() };

        using NetworkRouteCache cache = Make(probe);
        cache.Want(this, ["1.1.1.1", "9.9.9.9"]);

        await Until(() => cache.Walking == 2, "both walks to start");

        cache.Want(this, ["9.9.9.9"]);

        await Until(() => cache.Walking == 1, "the abandoned walk to stop");
        Assert.Equal(RouteState.Unknown, cache.Route("1.1.1.1")?.State);

        probe.Hold.SetResult();
    }

    [Fact]
    public async Task OneAskerLeaving_DoesNotTakeWhatAnotherStillWants()
    {
        Probe probe = new() { Hold = new TaskCompletionSource() };

        object other = new();
        using NetworkRouteCache cache = Make(probe);

        cache.Want(this, ["1.1.1.1"]);
        cache.Want(other, ["1.1.1.1"]);

        await Until(() => cache.Walking == 1, "the walk to start");

        cache.Release(this);

        Assert.Equal(1, cache.Walking);

        probe.Hold.SetResult();
    }

    [Fact]
    public async Task TheLastAskerLeaving_AbandonsTheWalk()
    {
        Probe probe = new() { Hold = new TaskCompletionSource() };

        using NetworkRouteCache cache = Make(probe);
        cache.Want(this, ["1.1.1.1"]);

        await Until(() => cache.Walking == 1, "the walk to start");

        cache.Release(this);

        await Until(() => cache.Walking == 0, "the walk to stop");

        probe.Hold.SetResult();
    }

    [Fact]
    public async Task AFirstWalk_ReportsEachHopAsItArrives()
    {
        Probe probe = new();
        probe.Routes["1.1.1.1"] = [Hop(1, "10.0.0.1"), Hop(2, "10.0.0.2"), Hop(3, "1.1.1.1", End)];

        using NetworkRouteCache cache = Make(probe);

        List<NetworkRoute> told = [];
        cache.RouteChanged += (_, route) =>
        {
            lock (told)
            {
                told.Add(route);
            }
        };

        cache.Want(this, ["1.1.1.1"]);

        await Settled(cache, "1.1.1.1", RouteState.Found);

        lock (told)
        {
            Assert.Equal([1, 2, 3, 3], told.Select(route => route.Hops.Count));
            Assert.Equal(
                [RouteState.Walking, RouteState.Walking, RouteState.Walking, RouteState.Found],
                told.Select(route => route.State));
        }
    }

    [Fact]
    public async Task AWalkToCheckAKnownRoute_ReportsOnlyWhatItFound()
    {
        Probe probe = new();
        probe.Routes["1.1.1.1"] = [Hop(1, "10.0.0.1"), Hop(2, "10.0.0.2"), Hop(3, "1.1.1.1", End)];

        Clock clock = new();
        using NetworkRouteCache cache = Make(probe, clock);
        cache.Want(this, ["1.1.1.1"]);

        await Settled(cache, "1.1.1.1", RouteState.Found);

        int said = 0;
        cache.RouteChanged += (_, _) => Interlocked.Increment(ref said);

        clock.Now += Freshness;
        cache.Want(this, ["1.1.1.1"]);

        await Until(() => probe.Walks == 2, "the second walk");
        await Until(() => Volatile.Read(ref said) == 1, "the one report");

        Assert.Equal(1, Volatile.Read(ref said));
    }

    [Fact]
    public async Task TurningTheWalkingOff_AbandonsItAndStartsNothingMore()
    {
        // It sends real packets, so there has to be a way to stop it.
        Probe probe = new() { Hold = new TaskCompletionSource() };

        using NetworkRouteCache cache = Make(probe);
        cache.Want(this, ["1.1.1.1"]);

        await Until(() => cache.Walking == 1, "the walk to start");

        cache.IsEnabled = false;

        await Until(() => cache.Walking == 0, "the walk to stop");

        cache.Want(this, ["1.1.1.1"]);

        Assert.Equal(1, probe.Walks);

        probe.Hold.SetResult();
        cache.IsEnabled = true;

        await Until(() => probe.Walks == 2, "walking to start again");
    }

    [Fact]
    public void BlankAddresses_AreIgnored()
    {
        Probe probe = new();

        using NetworkRouteCache cache = Make(probe);
        cache.Want(this, ["", "   "]);

        Assert.Equal(0, probe.Walks);
        Assert.Null(cache.Route(" "));
    }

    [Fact]
    public void SomewhereWithNoRouteToWalk_IsNeverProbed()
    {
        Probe probe = new();

        using NetworkRouteCache cache = Make(probe);
        cache.Want(this, ["127.0.0.1", "239.255.255.250", "ff02::fb"]);

        Assert.Equal(0, probe.Walks);
        Assert.Null(cache.Route("127.0.0.1"));
    }

    [Fact]
    public async Task OneAddressNamedTwice_IsWalkedOnce()
    {
        Probe probe = new();
        probe.Routes["1.1.1.1"] = [Hop(1, "1.1.1.1", End)];

        using NetworkRouteCache cache = Make(probe);
        cache.Want(this, ["1.1.1.1", "1.1.1.1"]);

        await Settled(cache, "1.1.1.1", RouteState.Found);

        Assert.Equal(1, probe.Walks);
    }

    [Fact]
    public async Task DisposingTheCache_AbandonsWhatItWasWalking()
    {
        Probe probe = new() { Hold = new TaskCompletionSource() };

        NetworkRouteCache cache = Make(probe);
        cache.Want(this, ["1.1.1.1"]);

        await Until(() => cache.Walking == 1, "the walk to start");

        cache.Dispose();

        await Until(() => cache.Walking == 0, "the walk to stop");

        Assert.Null(cache.Route("1.1.1.1"));

        probe.Hold.SetResult();
    }

    [Fact]
    public void WantingAfterDisposal_DoesNothing()
    {
        Probe probe = new();

        NetworkRouteCache cache = Make(probe);
        cache.Dispose();
        cache.Want(this, ["1.1.1.1"]);

        Assert.Equal(0, probe.Walks);
    }

    private const bool End = true;

    private static NetworkRouteCache Make(Probe probe, TimeProvider? clock = null) =>
        new(probe, clock ?? new Clock()) { Patience = Patience, Freshness = Freshness };

    private static PathHop Hop(int distance, string address, bool destination = false) =>
        new(distance, address, TimeSpan.FromMilliseconds(distance), destination);

    private static Task Settled(NetworkRouteCache cache, string address, RouteState state) =>
        Until(
            () => cache.Route(address)?.State == state && cache.Walking == 0,
            $"the route to {address} to be {state}");

    private static async Task Until(Func<bool> ready, string what)
    {
        long deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 10);

        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (ready())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail("Waited for " + what + " and it never happened.");
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Probe : INetworkPathProbe
    {
        private readonly Lock _gate = new();
        private readonly List<string> _walked = [];

        public Dictionary<string, PathHop[]> Routes { get; } = new(StringComparer.Ordinal);

        /// <summary>Held here until released, so a walk can be caught mid-flight.</summary>
        public TaskCompletionSource? Hold { get; set; }

        public IReadOnlyList<string> Walked
        {
            get
            {
                lock (_gate)
                {
                    return [.. _walked];
                }
            }
        }

        public int Walks
        {
            get
            {
                lock (_gate)
                {
                    return _walked.Count;
                }
            }
        }

        public async IAsyncEnumerable<PathHop> TraceAsync(
            string address,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _walked.Add(address);
            }

            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }

            foreach (PathHop hop in Routes.TryGetValue(address, out PathHop[]? route) ? route : [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return hop;
            }
        }

        public Task<TimeSpan?> PingAsync(string address, CancellationToken cancellationToken) =>
            Task.FromResult<TimeSpan?>(null);
    }
}
