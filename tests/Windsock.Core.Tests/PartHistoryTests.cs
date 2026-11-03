using Windsock.Core.Hardware;
using Windsock.Core.History;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>A part's last day or week, made from the machine minutes History keeps.</summary>
public sealed class PartHistoryTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(6);
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 14, 37, 20, Offset);

    private static SystemBucket Minute(DateTimeOffset at, double cpu, double? heat = null, double gpu = 0, double? gpuHeat = null, long memory = 0, long read = 0, long written = 0) =>
        new(at, 1, cpu, cpu, heat, heat, gpu, gpu, gpuHeat, memory, memory, 32L << 30, read, written);

    /// <summary>A day runs to the next five-minute edge, in five-minute steps.</summary>
    [Fact]
    public void Day_EndsAtTheNextFiveMinuteEdge()
    {
        (DateTimeOffset from, TimeSpan step, UsageGranularity granularity) = PartHistory.Window(PartSpan.Day, Now);

        Assert.Equal(new DateTimeOffset(2026, 10, 4, 14, 40, 0, Offset), from);
        Assert.Equal(TimeSpan.FromMinutes(5), step);
        Assert.Equal(UsageGranularity.Minute, granularity);
    }

    /// <summary>A week runs to the end of this hour, an hour a step.</summary>
    [Fact]
    public void Week_EndsAtTheEndOfThisHour()
    {
        (DateTimeOffset from, TimeSpan step, UsageGranularity granularity) = PartHistory.Window(PartSpan.Week, Now);

        Assert.Equal(new DateTimeOffset(2026, 9, 28, 15, 0, 0, Offset), from);
        Assert.Equal(TimeSpan.FromHours(1), step);
        Assert.Equal(UsageGranularity.Hour, granularity);
    }

    /// <summary>Minutes in one step average, each weighing the same, with the step's own peak.</summary>
    [Fact]
    public void Processor_AveragesMinutesIntoSteps()
    {
        DateTimeOffset from = new(2026, 10, 4, 14, 40, 0, Offset);

        PartSeries series = PartHistory.Shape(
            [
                Minute(from, 10, heat: 40),
                Minute(from.AddMinutes(1), 30, heat: 50),
                Minute(from.AddMinutes(2), 50),
                Minute(from.AddMinutes(7), 90, heat: 70),
            ],
            MachinePart.Processor,
            PartSpan.Day,
            from,
            Now,
            TimeSpan.FromMinutes(5));

        Assert.Equal(2, series.Points.Count);

        PartPoint first = series.Points[0];
        Assert.Equal(from, first.Start);
        Assert.Equal(3, first.Minutes);
        Assert.Equal(30, first.Average, 6);
        Assert.Equal(50, first.Peak);
        Assert.Equal(45, first.HeatAverage!.Value, 6);
        Assert.Equal(50, first.HeatPeak);

        Assert.Equal(from.AddMinutes(5), series.Points[1].Start);
        Assert.Equal(90, series.Points[1].Average, 6);

        Assert.Equal(45, series.Average!.Value, 6);
        Assert.Equal(90, series.Peak);
        Assert.Equal(160.0 / 3, series.HeatAverage!.Value, 6);
        Assert.Equal(70, series.HeatPeak);
        Assert.Null(series.Capacity);
    }

    /// <summary>Time nothing was recorded is a gap, never a zero.</summary>
    [Fact]
    public void Gaps_AreLeftOut()
    {
        DateTimeOffset from = new(2026, 10, 4, 14, 40, 0, Offset);

        PartSeries series = PartHistory.Shape(
            [Minute(from, 10), Minute(from.AddHours(3), 20)],
            MachinePart.Processor,
            PartSpan.Day,
            from,
            Now,
            TimeSpan.FromMinutes(5));

        Assert.Equal([from, from.AddHours(3)], series.Points.Select(point => point.Start));
    }

    /// <summary>Nothing recorded: no points, no figures.</summary>
    [Fact]
    public void Nothing_HasNoFigures()
    {
        PartSeries series = PartHistory.Shape([], MachinePart.Memory, PartSpan.Week, Now.AddDays(-7), Now, TimeSpan.FromHours(1));

        Assert.False(series.HasData);
        Assert.Null(series.Average);
        Assert.Null(series.Peak);
    }

    /// <summary>Graphics keeps no average temperature, only its hottest reading.</summary>
    [Fact]
    public void Graphics_HasAPeakTemperatureOnly()
    {
        DateTimeOffset from = new(2026, 10, 4, 14, 40, 0, Offset);

        PartSeries series = PartHistory.Shape(
            [Minute(from, 0, gpu: 60, gpuHeat: 66), Minute(from.AddMinutes(1), 0, gpu: 20, gpuHeat: 61)],
            MachinePart.Graphics,
            PartSpan.Day,
            from,
            Now,
            TimeSpan.FromMinutes(5));

        PartPoint point = Assert.Single(series.Points);
        Assert.Equal(40, point.Average, 6);
        Assert.Equal(60, point.Peak);
        Assert.Null(point.HeatAverage);
        Assert.Equal(66, point.HeatPeak);
    }

    /// <summary>Memory is bytes in use, with the memory fitted beside it.</summary>
    [Fact]
    public void Memory_CarriesTheMemoryFitted()
    {
        DateTimeOffset from = new(2026, 10, 4, 14, 40, 0, Offset);

        PartSeries series = PartHistory.Shape(
            [Minute(from, 0, memory: 8L << 30), Minute(from.AddMinutes(1), 0, memory: 12L << 30)],
            MachinePart.Memory,
            PartSpan.Day,
            from,
            Now,
            TimeSpan.FromMinutes(5));

        Assert.Equal(10L << 30, Assert.Single(series.Points).Average, 6);
        Assert.Equal(12L << 30, series.Peak);
        Assert.Equal(32L << 30, series.Capacity);
    }

    /// <summary>Storage is bytes a second; a minute is its own peak, an hour keeps none.</summary>
    [Fact]
    public void Storage_IsBytesASecond()
    {
        DateTimeOffset from = new(2026, 10, 4, 14, 40, 0, Offset);

        PartSeries day = PartHistory.Shape(
            [Minute(from, 0, read: 60_000, written: 60_000), Minute(from.AddMinutes(1), 0, read: 600_000)],
            MachinePart.Storage,
            PartSpan.Day,
            from,
            Now,
            TimeSpan.FromMinutes(5));

        PartPoint point = Assert.Single(day.Points);
        Assert.Equal(6_000, point.Average, 6);
        Assert.Equal(10_000, point.Peak);

        SystemBucket hour = new(from, 60, 0, 0, null, null, 0, 0, null, 0, 0, 0, 3_600_000, 0);
        PartSeries week = PartHistory.Shape([hour], MachinePart.Storage, PartSpan.Week, from, Now, TimeSpan.FromHours(1));

        Assert.Equal(1_000, Assert.Single(week.Points).Average, 6);
        Assert.Null(week.Points[0].Peak);
        Assert.Null(week.Peak);
    }

    /// <summary>Read goes to the store for the span's window and shapes what comes back.</summary>
    [Fact]
    public void Read_AsksTheStoreForTheWindow()
    {
        RecordingStore store = new();

        PartSeries series = PartHistory.Read(store, MachinePart.Processor, PartSpan.Week, Now);

        Assert.Equal(new DateTimeOffset(2026, 9, 28, 15, 0, 0, Offset), store.From);
        Assert.Equal(Now, store.Until);
        Assert.Equal(UsageGranularity.Hour, store.Granularity);
        Assert.Single(series.Points);
    }

    private sealed class RecordingStore : IMachineHistoryStore
    {
        public DateTimeOffset From { get; private set; }

        public DateTimeOffset Until { get; private set; }

        public UsageGranularity Granularity { get; private set; }

        public void WriteSystem(IReadOnlyList<SystemMinute> minutes)
        {
        }

        public void WriteConnection(IReadOnlyList<ConnectionMinute> minutes)
        {
        }

        public IReadOnlyList<SystemBucket> ReadSystem(DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity)
        {
            From = from;
            Until = until;
            Granularity = granularity;
            return [new SystemBucket(from.AddHours(2), 60, 25, 80, 45, 70, 0, 0, null, 0, 0, 0, 0, 0)];
        }

        public IReadOnlyList<ConnectionBucket> ReadConnection(DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity) => [];
    }
}
