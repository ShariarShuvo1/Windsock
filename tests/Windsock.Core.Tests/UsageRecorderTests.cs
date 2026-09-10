using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class UsageRecorderTests
{
    private static DateTimeOffset Noon { get; } = new DateTimeOffset(
        new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Local));

    private static (UsageRecorder Recorder, FakeHistoryStore Store) Build(bool recording = true)
    {
        FakeHistoryStore store = new();

        ThroughputMonitor monitor = new(
            new StillTotals(),
            Options.Create(new ThroughputMonitorOptions()),
            NullLogger<ThroughputMonitor>.Instance);

        UsageRecorder recorder = new(
            monitor,
            store,
            new WindsockSettings { RecordHistory = recording },
            NullLogger<UsageRecorder>.Instance);

        return (recorder, store);
    }

    [Fact]
    public void SamplesWithinAMinute_BecomeOneRow()
    {
        (UsageRecorder recorder, FakeHistoryStore store) = Build();

        recorder.Observe(new ThroughputDelta(Noon, 1000, 100, 0.5));
        recorder.Observe(new ThroughputDelta(Noon.AddSeconds(1), 2000, 200, 0.5));
        recorder.Observe(new ThroughputDelta(Noon.AddSeconds(2), 3000, 300, 0.5));
        Assert.Empty(store.Written);

        recorder.FlushOnShutdown();

        UsageMinute row = Assert.Single(store.Written);

        Assert.Equal(EpochMinute.From(Noon), row.Minute);
        Assert.Equal(6000, row.BytesDown);
        Assert.Equal(600, row.BytesUp);
    }

    [Fact]
    public void ThePeak_IsTheFastestSampleNotTheAverage()
    {
        (UsageRecorder recorder, FakeHistoryStore store) = Build();

        recorder.Observe(new ThroughputDelta(Noon, 500, 50, 1.0));
        recorder.Observe(new ThroughputDelta(Noon.AddSeconds(1), 1000, 20, 0.5));
        recorder.Observe(new ThroughputDelta(Noon.AddSeconds(2), 100, 10, 1.0));
        recorder.FlushOnShutdown();

        UsageMinute row = Assert.Single(store.Written);
        Assert.Equal(2000, row.PeakDown);
        Assert.Equal(50, row.PeakUp);
    }

    [Fact]
    public void CrossingIntoTheNextMinute_ClosesThePreviousOne()
    {
        (UsageRecorder recorder, FakeHistoryStore store) = Build();

        recorder.Observe(new ThroughputDelta(Noon, 1000, 100, 0.5));
        recorder.Observe(new ThroughputDelta(Noon.AddMinutes(1), 2000, 200, 0.5));
        recorder.Observe(new ThroughputDelta(Noon.AddMinutes(2), 3000, 300, 0.5));
        recorder.FlushOnShutdown();

        Assert.Equal(3, store.Written.Count);
        Assert.Equal(EpochMinute.From(Noon), store.Written[0].Minute);
        Assert.Equal(1000, store.Written[0].BytesDown);
        Assert.Equal(2000, store.Written[1].BytesDown);
        Assert.Equal(3000, store.Written[2].BytesDown);
    }

    [Fact]
    public void ClosingWindsock_KeepsTheMinuteInProgress()
    {
        (UsageRecorder recorder, FakeHistoryStore store) = Build();

        recorder.Observe(new ThroughputDelta(Noon, 4096, 512, 0.5));
        recorder.FlushOnShutdown();
        Assert.Equal(4096, Assert.Single(store.Written).BytesDown);
    }

    [Fact]
    public void FlushingTwice_DoesNotWriteTheMinuteTwice()
    {
        (UsageRecorder recorder, FakeHistoryStore store) = Build();

        recorder.Observe(new ThroughputDelta(Noon, 4096, 512, 0.5));
        recorder.FlushOnShutdown();
        recorder.FlushOnShutdown();

        Assert.Single(store.Written);
    }

    [Fact]
    public void TheRunIsRecordedAsCovered()
    {
        (UsageRecorder recorder, FakeHistoryStore store) = Build();

        recorder.Observe(new ThroughputDelta(Noon, 1, 1, 0.5));
        recorder.Observe(new ThroughputDelta(Noon.AddMinutes(5), 1, 1, 0.5));
        recorder.FlushOnShutdown();
        Assert.Equal(EpochMinute.From(Noon), store.CoverageStart);
        Assert.Equal(EpochMinute.From(Noon.AddMinutes(5)), store.CoverageEnd);
    }

    [Fact]
    public void AQuietMinute_IsStillRecorded()
    {
        (UsageRecorder recorder, FakeHistoryStore store) = Build();

        recorder.Observe(new ThroughputDelta(Noon, 0, 0, 0.5));
        recorder.FlushOnShutdown();
        Assert.Equal(0, Assert.Single(store.Written).BytesTotal);
    }

    [Fact]
    public void RecordingOff_WritesNothing()
    {
        (UsageRecorder recorder, FakeHistoryStore store) = Build(recording: false);

        recorder.Observe(new ThroughputDelta(Noon, 4096, 512, 0.5));
        recorder.Observe(new ThroughputDelta(Noon.AddMinutes(1), 4096, 512, 0.5));
        recorder.FlushOnShutdown();

        Assert.False(recorder.IsRecording);
        Assert.Empty(store.Written);
    }

    [Fact]
    public void TurningRecordingOff_KeepsTheMinuteAlreadyMeasured()
    {
        (UsageRecorder recorder, FakeHistoryStore store) = Build();

        recorder.Observe(new ThroughputDelta(Noon, 4096, 512, 0.5));
        recorder.IsRecording = false;
        recorder.Observe(new ThroughputDelta(Noon.AddSeconds(1), 9999, 9999, 0.5));
        recorder.FlushOnShutdown();
        Assert.Equal(4096, Assert.Single(store.Written).BytesDown);
    }

    [Fact]
    public void TurningRecordingBackOn_StartsANewRun()
    {
        (UsageRecorder recorder, FakeHistoryStore store) = Build();

        recorder.Observe(new ThroughputDelta(Noon, 1, 1, 0.5));
        recorder.IsRecording = false;
        recorder.IsRecording = true;
        recorder.Observe(new ThroughputDelta(Noon.AddHours(2), 1, 1, 0.5));
        recorder.FlushOnShutdown();
        Assert.Equal(EpochMinute.From(Noon.AddHours(2)), store.CoverageStart);
        Assert.True(recorder.IsRecording);
    }

    [Fact]
    public void SwitchingToTheStateItIsAlreadyIn_ChangesNothing()
    {
        (UsageRecorder recorder, FakeHistoryStore store) = Build();

        recorder.Observe(new ThroughputDelta(Noon, 4096, 512, 0.5));
        recorder.IsRecording = true;
        recorder.FlushOnShutdown();

        // A redundant set must not close the open minute and write it twice.
        Assert.Equal(4096, Assert.Single(store.Written).BytesDown);
    }

    private sealed class StillTotals : INetworkTotalsSource
    {
        public NetworkTotals ReadTotals() => default;
    }

    private sealed class FakeHistoryStore : IUsageHistoryStore
    {
        public List<UsageMinute> Written { get; } = [];

        public long CoverageStart { get; private set; } = -1;

        public long CoverageEnd { get; private set; } = -1;

        public void Write(IReadOnlyList<UsageMinute> minutes) => Written.AddRange(minutes);

        public void WriteCoverage(long startMinute, long endMinute)
        {
            CoverageStart = startMinute;
            CoverageEnd = endMinute;
        }

        public IReadOnlyList<UsageBucket> Read(
            DateTimeOffset from,
            DateTimeOffset until,
            UsageGranularity granularity) => [];

        public UsageSummary Summarise(DateTimeOffset from, DateTimeOffset until) => UsageSummary.Empty;

        public IReadOnlyList<CoverageWindow> ReadCoverage(DateTimeOffset from, DateTimeOffset until) => [];

        public UsageExtent Extent() => default;

        public event EventHandler? Erased;

        public int Delete(DateTimeOffset from, DateTimeOffset until) => 0;

        public int RemoveEverything()
        {
            Written.Clear();
            Erased?.Invoke(this, EventArgs.Empty);

            return 0;
        }

        public int Merge(IReadOnlyList<UsageMinute> minutes) => 0;

        public int Replace(IReadOnlyList<UsageMinute> minutes) => 0;

        public int CountExisting(IReadOnlyList<UsageMinute> minutes) => 0;
    }
}
