using Windsock.Core.Startup;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class SingleInstanceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public void TheFirstOne_GetsToRun()
    {
        using SingleInstance only = new(Name());

        Assert.True(only.IsOnly);
    }

    [Fact]
    public void TheSecondOne_DoesNot()
    {
        string name = Name();

        using SingleInstance first = new(name);
        using SingleInstance second = new(name);

        Assert.True(first.IsOnly);
        Assert.False(second.IsOnly);
    }

    [Fact]
    public void TwoDifferentNames_DoNotCollide()
    {
        using SingleInstance one = new(Name());
        using SingleInstance other = new(Name());

        Assert.True(one.IsOnly);
        Assert.True(other.IsOnly);
    }

    [Fact]
    public void OnceTheFirstHasGone_TheNameIsFreeAgain()
    {
        // Which is what makes quitting and starting again work at all.
        string name = Name();

        using (SingleInstance first = new(name))
        {
            Assert.True(first.IsOnly);
        }

        using SingleInstance next = new(name);

        Assert.True(next.IsOnly);
    }

    [Fact]
    public void TheSecondOne_AsksTheFirstToShowItself()
    {
        string name = Name();

        using ManualResetEventSlim asked = new();
        using SingleInstance first = new(name);

        first.WakeRequested += (_, _) => asked.Set();
        first.Listen();

        using (SingleInstance second = new(name))
        {
            Assert.False(second.IsOnly);
            second.Wake();
        }

        Assert.True(asked.Wait(Patience, TestContext.Current.CancellationToken), "The first instance was never asked to show itself.");
    }

    [Fact]
    public void AskedAgainLater_IsToldAgain()
    {
        string name = Name();

        using ManualResetEventSlim asked = new();
        using SingleInstance first = new(name);

        first.WakeRequested += (_, _) => asked.Set();
        first.Listen();

        first.Wake();
        Assert.True(asked.Wait(Patience, TestContext.Current.CancellationToken), "The first request was lost.");

        asked.Reset();

        first.Wake();
        Assert.True(asked.Wait(Patience, TestContext.Current.CancellationToken), "The listener answered once and stopped.");
    }

    [Fact]
    public void AfterASecondOneHasBeenAndGone_TheFirstIsStillListening()
    {
        string name = Name();

        using ManualResetEventSlim asked = new();
        using SingleInstance first = new(name);

        first.WakeRequested += (_, _) => asked.Set();
        first.Listen();

        using (SingleInstance second = new(name))
        {
            second.Wake();
        }

        Assert.True(asked.Wait(Patience, TestContext.Current.CancellationToken), "The first request was lost.");
        asked.Reset();
        using (SingleInstance third = new(name))
        {
            third.Wake();
        }

        Assert.True(
            asked.Wait(Patience, TestContext.Current.CancellationToken),
            "The listener stopped when the second instance disposed itself.");
    }

    [Fact]
    public void ListeningThenDisposed_LetsGoWithoutHanging()
    {
        string name = Name();

        SingleInstance first = new(name);

        first.Listen();
        first.Dispose();

        using SingleInstance next = new(name);

        Assert.True(next.IsOnly);
    }

    [Fact]
    public void DisposedTwice_IsHarmless()
    {
        SingleInstance only = new(Name());

        only.Dispose();
        only.Dispose();
    }

    [Fact]
    public void AReplacement_WaitsForTheOneItIsReplacing()
    {
        string name = Name();

        using ManualResetEventSlim holding = new();
        using ManualResetEventSlim leave = new();
        Thread holder = new(() =>
        {
            using SingleInstance first = new(name);
            holding.Set();
            leave.Wait();
        });

        holder.Start();
        Assert.True(holding.Wait(Patience, TestContext.Current.CancellationToken));

        // Let go a moment from now, the way the Windsock being replaced does.
        using Timer going = new(_ => leave.Set(), null, TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);

        using SingleInstance replacement = new(name, TimeSpan.FromSeconds(10));

        Assert.True(replacement.IsOnly, "The replacement stood aside instead of waiting its turn.");
        holder.Join(Patience);
    }

    [Fact]
    public void AReplacement_RunsEvenIfTheOldOneWillNotGo()
    {
        string name = Name();

        using SingleInstance stuck = new(name);
        using SingleInstance replacement = new(name, TimeSpan.FromMilliseconds(200));

        Assert.True(replacement.IsOnly);
    }

    [Fact]
    public void AReplacementThatWaitedInVain_LetsGoWithoutThrowing()
    {
        string name = Name();

        using SingleInstance stuck = new(name);

        SingleInstance replacement = new(name, TimeSpan.FromMilliseconds(200));
        replacement.Dispose();
    }

    [Fact]
    public void AnOrdinarySecondOne_StillStandsAside()
    {
        string name = Name();

        using SingleInstance first = new(name);
        using SingleInstance second = new(name);

        Assert.False(second.IsOnly);
    }

    [Theory]
    [InlineData("--replacing")]
    [InlineData("--REPLACING")]
    public void AReplacementSaysSoOnItsCommandLine_AndIsHeard(string argument) =>
        Assert.True(SingleInstance.IsReplacing([argument]));

    [Fact]
    public void AnythingElse_IsNotAReplacement()
    {
        Assert.False(SingleInstance.IsReplacing([]));
        Assert.False(SingleInstance.IsReplacing(null));
        Assert.False(SingleInstance.IsReplacing(["--background"]));
    }

    private static string Name() => "Windsock-tests-" + Guid.NewGuid().ToString("N");
}
