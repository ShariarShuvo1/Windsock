#if !STORE
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Windsock.Core.Networking;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>The hop-by-hop table's lifetime: it never outlives whoever asked for it.</summary>
public sealed class HopWatchTests
{
    [Theory]
    [InlineData("192.0.2.1")]
    [InlineData("localhost")]
    public async Task AStartWhoseAskerHasGone_StartsNothing(string target)
    {
        using NetworkRouteCache routes = new(new SilentProbe());
        using HopWatch watch = new(new AnsweringHop(), routes, TimeProvider.System, NullLogger<HopWatch>.Instance);
        using CancellationTokenSource gone = new();
        await gone.CancelAsync();

        Assert.False(await watch.StartAsync(target, gone.Token));
        Assert.False(watch.IsRunning);
    }

    [Fact]
    public async Task TracingSwitchedOffElsewhere_EndsTheTableAndSaysSo()
    {
        using NetworkRouteCache routes = new(new SilentProbe());
        using HopWatch watch = new(new AnsweringHop(), routes, TimeProvider.System, NullLogger<HopWatch>.Instance);
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource round = new(TaskCreationOptions.RunContinuationsAsynchronously);
        watch.Ended += (_, _) => ended.TrySetResult();
        watch.Updated += (_, _) => round.TrySetResult();

        Assert.True(watch.Start("192.0.2.1"));
        await round.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        routes.IsEnabled = false;
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(watch.IsRunning);
    }

    private sealed class AnsweringHop : IHopProbe
    {
        public Task<HopReply?> ProbeAsync(IPAddress target, int distance, CancellationToken cancellationToken) =>
            Task.FromResult<HopReply?>(new HopReply(distance, HopOutcome.Arrived, target.ToString(), TimeSpan.FromMilliseconds(5)));
    }

    private sealed class SilentProbe : INetworkPathProbe
    {
        public async IAsyncEnumerable<PathHop> TraceAsync(string address, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public Task<TimeSpan?> PingAsync(string address, CancellationToken cancellationToken) => Task.FromResult<TimeSpan?>(null);
    }
}
#endif
