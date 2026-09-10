using Windsock.Core.Processes;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class ProcessAttributionHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void KeepsTheBusiestFew_InOrder()
    {
        var history = new ProcessAttributionHistory(capacity: 4, width: 3);

        history.Add(Start, Rows((1, 100), (2, 900), (3, 50), (4, 400), (5, 700)));

        ProcessShare[] shares = new ProcessShare[3];
        int count = history.Find(Start, TimeSpan.FromSeconds(1), shares);

        Assert.Equal(3, count);
        Assert.Equal([2, 5, 4], shares.Take(count).Select(share => share.ProcessId));
    }

    [Fact]
    public void IgnoresProcessesMovingNothing()
    {
        var history = new ProcessAttributionHistory(capacity: 4, width: 3);

        history.Add(Start, Rows((1, 0), (2, 0), (3, 25)));

        ProcessShare[] shares = new ProcessShare[3];
        Assert.Equal(1, history.Find(Start, TimeSpan.FromSeconds(1), shares));
        Assert.Equal(3, shares[0].ProcessId);
    }

    [Fact]
    public void FindsTheSnapshotNearestTheMomentAsked()
    {
        var history = new ProcessAttributionHistory(capacity: 8, width: 2);

        for (int second = 0; second < 5; second++)
        {
            history.Add(Start.AddSeconds(second), Rows((second + 1, 100)));
        }

        ProcessShare[] shares = new ProcessShare[2];
        history.Find(Start.AddSeconds(2.4), TimeSpan.FromSeconds(1), shares);

        Assert.Equal(3, shares[0].ProcessId);
    }

    [Fact]
    public void RefusesToGuess_WhenNothingIsNearEnough()
    {
        var history = new ProcessAttributionHistory(capacity: 8, width: 2);
        history.Add(Start, Rows((1, 100)));

        ProcessShare[] shares = new ProcessShare[2];
        Assert.Equal(0, history.Find(Start.AddSeconds(30), TimeSpan.FromSeconds(2), shares));
    }

    [Fact]
    public void EmptyHistory_ReturnsNothing()
    {
        var history = new ProcessAttributionHistory(capacity: 4, width: 2);

        ProcessShare[] shares = new ProcessShare[2];
        Assert.Equal(0, history.Find(Start, TimeSpan.FromSeconds(1), shares));
    }

    [Fact]
    public void OldestSnapshotsAreOverwritten_AndTheRestStillFound()
    {
        var history = new ProcessAttributionHistory(capacity: 3, width: 1);

        for (int second = 0; second < 5; second++)
        {
            history.Add(Start.AddSeconds(second), Rows((second + 1, 100)));
        }

        ProcessShare[] shares = new ProcessShare[1];

        // The first two were pushed out; the last three are intact.
        Assert.Equal(0, history.Find(Start, TimeSpan.FromSeconds(0.4), shares));
        Assert.Equal(1, history.Find(Start.AddSeconds(4), TimeSpan.FromSeconds(0.4), shares));
        Assert.Equal(5, shares[0].ProcessId);
    }

    [Fact]
    public void NamesAreKept_SoAProcessThatHasExitedCanStillBeNamed()
    {
        var history = new ProcessAttributionHistory(capacity: 2, width: 1);
        history.Add(Start, [Usage(42, "installer", 500)]);

        ProcessShare[] shares = new ProcessShare[1];
        history.Find(Start, TimeSpan.FromSeconds(1), shares);

        Assert.Equal("installer", shares[0].Name);
    }

    private static ProcessUsage[] Rows(params (int ProcessId, double Rate)[] rows) =>
        [.. rows.Select(row => Usage(row.ProcessId, $"process-{row.ProcessId}", row.Rate))];

    private static ProcessUsage Usage(int processId, string name, double rate) =>
        new(processId, new ProcessIdentity(name, null, null, null), rate, 0, 0, 0, 0, default);
}
