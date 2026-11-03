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

    [Fact]
    public void TheSecondOne_HandsOverThePageItWasAskedFor()
    {
        string name = Name();

        using ManualResetEventSlim asked = new();
        string? heard = "nothing yet";
        using SingleInstance first = new(name);

        first.WakeRequested += (_, wake) =>
        {
            heard = wake.Link;
            asked.Set();
        };
        first.Listen();

        using (SingleInstance second = new(name))
        {
            second.Wake("outages");
        }

        Assert.True(asked.Wait(Patience, TestContext.Current.CancellationToken), "The first instance was never asked.");
        Assert.Equal("outages", heard);
    }

    [Fact]
    public void ASecondOneWithNoPage_AsksForTheWindowAsItWas()
    {
        string name = Name();

        using ManualResetEventSlim asked = new();
        string? heard = "nothing yet";
        using SingleInstance first = new(name);

        first.WakeRequested += (_, wake) =>
        {
            heard = wake.Link;
            asked.Set();
        };
        first.Listen();

        using (SingleInstance second = new(name))
        {
            second.Wake();
        }

        Assert.True(asked.Wait(Patience, TestContext.Current.CancellationToken), "The first instance was never asked.");
        Assert.Null(heard);
    }

    [Fact]
    public void ACallerThatSaysNothing_DoesNotStopTheNextOne()
    {
        string name = Name();

        using ManualResetEventSlim asked = new();
        string? heard = "nothing yet";
        using SingleInstance first = new(name);

        first.WakeRequested += (_, wake) =>
        {
            heard = wake.Link;
            asked.Set();
        };
        first.Listen();

        // Connects and never says a word, and stays connected.
        using System.IO.Pipes.NamedPipeClientStream stalled = new(".", first.PipeName, System.IO.Pipes.PipeDirection.InOut);
        stalled.Connect(5000);

        using (SingleInstance second = new(name))
        {
            second.Wake("history");
        }

        Assert.True(asked.Wait(Patience, TestContext.Current.CancellationToken), "The second caller was never heard.");
        Assert.Equal("history", heard);
    }

    [Fact]
    public void ANameSomebodyElseHolds_IsReportedOnce_AndAWakeStillArrives()
    {
        string name = Name();

        // Another program of this user got to the name first, and answers nobody.
        using System.IO.Pipes.NamedPipeServerStream squatter = new(
            SingleInstance.PipeNameFor(name),
            System.IO.Pipes.PipeDirection.InOut,
            1,
            System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous);

        using ManualResetEventSlim asked = new();
        string? heard = "nothing yet";
        int troubles = 0;
        using SingleInstance first = new(name);

        first.HandOffTrouble += (_, _) => Interlocked.Increment(ref troubles);
        first.WakeRequested += (_, wake) =>
        {
            heard = wake.Link;
            asked.Set();
        };
        first.Listen();

        using (SingleInstance second = new(name))
        {
            second.Wake("outages");
        }

        // The squatter never answers "ok", so the caller falls back to the event:
        // the window is shown as it was, and the app is none the worse.
        Assert.True(asked.Wait(Patience, TestContext.Current.CancellationToken), "No wake arrived past the squatter.");
        Assert.Null(heard);
        Assert.Equal(1, Volatile.Read(ref troubles));

        // Waits that double from a quarter second: a few tries in the seconds
        // above, never a spin.
        Assert.InRange(first.PipeAttempts, 2, 14);
    }

    [Fact]
    public void APipeMadeFirstBySomethingElse_IsNotJoined_AndIsTakenOnceItGoes()
    {
        string name = Name();

        // A program of this user made the name first, taking as many instances
        // as Windsock does, so a listener that did not insist on being first
        // would have joined it and let it answer callers.
        System.IO.Pipes.NamedPipeServerStream squatter = new(
            SingleInstance.PipeNameFor(name),
            System.IO.Pipes.PipeDirection.InOut,
            2,
            System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous);

        using ManualResetEventSlim asked = new();
        string? heard = "nothing yet";
        int troubles = 0;
        using SingleInstance first = new(name);

        first.HandOffTrouble += (_, _) => Interlocked.Increment(ref troubles);
        first.WakeRequested += (_, wake) =>
        {
            heard = wake.Link;
            asked.Set();
        };

        try
        {
            first.Listen();

            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref troubles) == 1, Patience), "The pipe made first by somebody else was not reported.");
            Assert.False(first.HoldsThePipe, "Windsock joined a pipe somebody else made.");
        }
        finally
        {
            squatter.Dispose();
        }

        Assert.True(SpinWait.SpinUntil(() => first.HoldsThePipe, Patience), "The name was never taken back once it was free.");

        using (SingleInstance second = new(name))
        {
            second.Wake("history");
        }

        Assert.True(asked.Wait(Patience, TestContext.Current.CancellationToken), "The first instance was never asked.");
        Assert.Equal("history", heard);
        Assert.Equal(1, Volatile.Read(ref troubles));
    }

    [Fact]
    public async Task ASlowAnswer_IsWaitedForOnce_NotOnceToConnectAndAgainToHear()
    {
        string name = Name();

        using CancellationTokenSource done = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Something makes the pipe late in the sender's wait, takes its line
        // and never answers.
        Task late = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1600), done.Token);

            await using System.IO.Pipes.NamedPipeServerStream server = new(
                SingleInstance.PipeNameFor(name),
                System.IO.Pipes.PipeDirection.InOut,
                1,
                System.IO.Pipes.PipeTransmissionMode.Byte,
                System.IO.Pipes.PipeOptions.Asynchronous);

            await server.WaitForConnectionAsync(done.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, done.Token);
        }, done.Token);

        TimeSpan took;

        // Made and let go before any await: the mutex is released on the
        // thread that took it, as the app does on its own thread.
        using (SingleInstance only = new(name))
        {
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            only.Wake("history");
            took = clock.Elapsed;
        }

        await done.CancelAsync();

        try
        {
            await late;
        }
        catch (OperationCanceledException)
        {
            // Cancelled, as it was told to be.
        }

        // 1.6 s to connect and 2 s more to hear nothing was 3.6 s.
        Assert.True(took < TimeSpan.FromSeconds(3), $"The sender waited {took.TotalSeconds:0.0} s, more than its one deadline.");
    }

    [Fact]
    public void WithNobodyListening_AWakeGivesUpRatherThanHanging()
    {
        string name = Name();

        using SingleInstance first = new(name);
        using SingleInstance second = new(name);

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        second.Wake("history");

        // One deadline of two seconds for the whole exchange.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), "Waking a Windsock that never listens hung.");
    }

    [Theory]
    [InlineData("open history", true, "history")]
    [InlineData("open", true, null)]
    [InlineData("open History", true, null)]
    [InlineData("open ../../x", true, null)]
    [InlineData("close history", false, null)]
    [InlineData("open history now", false, null)]
    [InlineData("", false, null)]
    public void WhatArrivesOnThePipe_IsReadStrictly(string message, bool isRequest, string? link)
    {
        Assert.Equal(isRequest, SingleInstance.TryRead(message, out string? read));
        Assert.Equal(link, read);
    }

    [Fact]
    public void AMessageTooLongToBeOne_IsIgnored() =>
        Assert.False(SingleInstance.TryRead("open " + new string('a', 200), out _));

    /// <summary>
    /// A Windsock started just as another was killed found the lock still held,
    /// handed off to nobody and left, so none ran. One whose hand-off goes
    /// unanswered now waits a moment for the lock, and runs once it is let go.
    /// </summary>
    [Fact]
    public void AHolderThatLetsGo_IsTakenOverFrom()
    {
        string name = Name();
        using Holder holder = new(name);

        using SingleInstance second = new(name);
        Assert.False(second.IsOnly);

        holder.LetGo();

        Assert.True(second.TakeOver(Patience));
        Assert.True(second.IsOnly);
        Assert.True(second.OwnsLock);
        Assert.False(second.Refused);
    }

    /// <summary>A holder whose process was killed leaves the lock abandoned, which counts as let go.</summary>
    [Fact]
    public void AHolderThatDiedHoldingIt_IsTakenOverFrom()
    {
        string name = Name();
        using Holder holder = new(name);

        using SingleInstance second = new(name);
        holder.Die();

        Assert.True(second.TakeOver(Patience));
        Assert.True(second.IsOnly);
    }

    [Fact]
    public void AHolderThatKeepsIt_IsNotTakenOverFrom()
    {
        string name = Name();
        using Holder holder = new(name);

        using SingleInstance second = new(name);

        Assert.False(second.TakeOver(TimeSpan.FromMilliseconds(200)));
        Assert.False(second.IsOnly);
        Assert.False(second.OwnsLock);
    }

    [Fact]
    public void TheFirstOne_OwnsTheLock_AndWasNotRefused()
    {
        using SingleInstance only = new(Name());

        Assert.True(only.OwnsLock);
        Assert.False(only.Refused);
        Assert.True(only.TakeOver(TimeSpan.Zero));
    }

    private static string Name() => "Windsock-tests-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// A SingleInstance held on a thread of its own, as another process would
    /// hold it: a mutex belongs to a thread, so one made on the test's own
    /// thread would let the test's second instance straight in.
    /// </summary>
    private sealed class Holder : IDisposable
    {
        private readonly ManualResetEventSlim _taken = new();
        private readonly ManualResetEventSlim _letGo = new();
        private readonly Thread _thread;
        private volatile bool _die;

        public Holder(string name)
        {
            _thread = new Thread(() =>
            {
                SingleInstance held = new(name);
                _taken.Set();
                _letGo.Wait();

                if (!_die)
                {
                    held.Dispose();
                }
            })
            {
                IsBackground = true,
            };

            _thread.Start();
            Assert.True(_taken.Wait(Patience));
        }

        /// <summary>Releases the lock, as a Windsock closing does.</summary>
        public void LetGo()
        {
            _letGo.Set();
            _thread.Join(Patience);
        }

        /// <summary>Ends the holding thread without releasing it, as a killed process does.</summary>
        public void Die()
        {
            _die = true;
            LetGo();
        }

        public void Dispose()
        {
            LetGo();
            _taken.Dispose();
            _letGo.Dispose();
        }
    }
}
