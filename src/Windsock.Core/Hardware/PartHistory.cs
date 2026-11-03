using Windsock.Core.History;

namespace Windsock.Core.Hardware;

/// <summary>A part of the machine with a page of its own.</summary>
public enum MachinePart
{
    Processor,

    Graphics,

    Memory,

    Storage,
}

/// <summary>How far back a part's history goes on its page.</summary>
public enum PartSpan
{
    /// <summary>The last 24 hours, in five-minute steps.</summary>
    Day,

    /// <summary>The last seven days, an hour a step.</summary>
    Week,
}

/// <summary>
/// One step of a part's history.
/// </summary>
/// <param name="Start">When the step starts, local time.</param>
/// <param name="Minutes">How many of its minutes were recorded.</param>
/// <param name="Average">
/// Load in percent for the processor and graphics, bytes in use for memory,
/// bytes a second read and written for storage, over the recorded minutes.
/// </param>
/// <param name="Peak">
/// The highest reading in the step, in the same unit; for storage the
/// busiest minute, which an hour's step does not keep, so nothing there.
/// </param>
/// <param name="HeatAverage">Average temperature, °C, where the part reports one.</param>
/// <param name="HeatPeak">Hottest reading, °C. Graphics keeps only this, not an average.</param>
public readonly record struct PartPoint(
    DateTimeOffset Start,
    int Minutes,
    double Average,
    double? Peak,
    double? HeatAverage,
    double? HeatPeak);

/// <summary>A part's history over a span, and the span summed up.</summary>
/// <param name="Part">Which part.</param>
/// <param name="Span">Day or week.</param>
/// <param name="From">Where the span starts.</param>
/// <param name="Until">Where it ends: now.</param>
/// <param name="Step">How long each point stands for.</param>
/// <param name="Points">
/// Only the steps something was recorded in: a gap is time Windsock was not
/// running or not recording, and is left a gap rather than drawn as zero.
/// </param>
/// <param name="Average">The span's average, weighting each recorded minute alike; nothing when none was.</param>
/// <param name="Peak">The span's highest reading.</param>
/// <param name="HeatAverage">The span's average temperature.</param>
/// <param name="HeatPeak">The span's hottest reading.</param>
/// <param name="Capacity">For memory, the memory fitted; otherwise nothing.</param>
public sealed record PartSeries(
    MachinePart Part,
    PartSpan Span,
    DateTimeOffset From,
    DateTimeOffset Until,
    TimeSpan Step,
    IReadOnlyList<PartPoint> Points,
    double? Average,
    double? Peak,
    double? HeatAverage,
    double? HeatPeak,
    long? Capacity)
{
    /// <summary>Whether anything was recorded in the span.</summary>
    public bool HasData => Points.Count > 0;
}

/// <summary>
/// Reads a part's last day or week from the machine minutes History already
/// keeps, for the part's own page.
/// </summary>
/// <remarks>
/// Nothing new is recorded for this: the minutes are written for History's
/// lanes whenever history is on. What they hold decides what a page can draw:
/// the processor's load and temperature (average and peak), the busiest
/// graphics adapter's load and its hottest reading (no average temperature is
/// kept), memory in use, and bytes read and written across every drive. No
/// drive temperature and nothing per core or per adapter is kept.
/// </remarks>
public static class PartHistory
{
    /// <summary>The step of a day's history.</summary>
    public static readonly TimeSpan DayStep = TimeSpan.FromMinutes(5);

    /// <summary>The step of a week's history.</summary>
    public static readonly TimeSpan WeekStep = TimeSpan.FromHours(1);

    /// <summary>
    /// Reads a part's history up to <paramref name="now"/>. Touches the
    /// history file: call it off the UI thread.
    /// </summary>
    public static PartSeries Read(IMachineHistoryStore store, MachinePart part, PartSpan span, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(store);

        (DateTimeOffset from, TimeSpan step, UsageGranularity granularity) = Window(span, now);
        IReadOnlyList<SystemBucket> buckets = store.ReadSystem(from, now, granularity);

        return Shape(buckets, part, span, from, now, step);
    }

    /// <summary>Where a span starts, how long its steps are, and how finely to read it.</summary>
    public static (DateTimeOffset From, TimeSpan Step, UsageGranularity Granularity) Window(PartSpan span, DateTimeOffset now)
    {
        if (span == PartSpan.Week)
        {
            // Whole local hours, so the steps are the store's own hour buckets.
            DateTimeOffset hour = new(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);
            return (hour.AddHours(1).AddDays(-7), WeekStep, UsageGranularity.Hour);
        }

        long steps = now.ToUnixTimeMilliseconds() / (long)DayStep.TotalMilliseconds;
        DateTimeOffset edge = DateTimeOffset.FromUnixTimeMilliseconds((steps + 1) * (long)DayStep.TotalMilliseconds).ToOffset(now.Offset);
        return (edge.AddDays(-1), DayStep, UsageGranularity.Minute);
    }

    /// <summary>Gathers buckets read from the store into a part's steps.</summary>
    public static PartSeries Shape(
        IReadOnlyList<SystemBucket> buckets,
        MachinePart part,
        PartSpan span,
        DateTimeOffset from,
        DateTimeOffset until,
        TimeSpan step)
    {
        ArgumentNullException.ThrowIfNull(buckets);

        SortedDictionary<long, Step> steps = [];
        Step whole = new();
        long capacity = 0;

        foreach (SystemBucket bucket in buckets)
        {
            if (bucket.Minutes <= 0 || bucket.Start < from || bucket.Start >= until)
            {
                continue;
            }

            long index = (long)Math.Floor((bucket.Start - from) / step);

            if (!steps.TryGetValue(index, out Step? into))
            {
                into = new Step();
                steps[index] = into;
            }

            (double average, double? peak, double? heatAverage, double? heatPeak) = Figures(bucket, part);
            into.Add(bucket.Minutes, average, peak, heatAverage, heatPeak);
            whole.Add(bucket.Minutes, average, peak, heatAverage, heatPeak);
            capacity = Math.Max(capacity, bucket.MemoryTotal);
        }

        List<PartPoint> points = new(steps.Count);

        foreach ((long index, Step gathered) in steps)
        {
            points.Add(new PartPoint(
                from + (step * index),
                gathered.Minutes,
                gathered.Average,
                gathered.Peak,
                gathered.HeatAverage,
                gathered.HeatPeak));
        }

        return new PartSeries(
            part,
            span,
            from,
            until,
            step,
            points,
            whole.Minutes > 0 ? whole.Average : null,
            whole.Minutes > 0 ? whole.Peak : null,
            whole.HeatAverage,
            whole.HeatPeak,
            part == MachinePart.Memory && capacity > 0 ? capacity : null);
    }

    private static (double Average, double? Peak, double? HeatAverage, double? HeatPeak) Figures(SystemBucket bucket, MachinePart part) => part switch
    {
        MachinePart.Processor => (bucket.ProcessorAverage, bucket.ProcessorPeak, bucket.ProcessorHeatAverage, bucket.ProcessorHeatPeak),
        MachinePart.Graphics => (bucket.GraphicsAverage, bucket.GraphicsPeak, null, bucket.GraphicsHeatPeak),
        MachinePart.Memory => (bucket.MemoryAverage, bucket.MemoryPeak, null, null),
        _ => Storage(bucket),
    };

    private static (double Average, double? Peak, double? HeatAverage, double? HeatPeak) Storage(SystemBucket bucket)
    {
        double rate = (bucket.DiskRead + bucket.DiskWrite) / (bucket.Minutes * 60.0);

        // A one-minute bucket's rate is its own peak; an hour's busiest minute is not kept.
        return (rate, bucket.Minutes == 1 ? rate : null, null, null);
    }

    /// <summary>Several buckets taken together, each recorded minute weighing the same.</summary>
    private sealed class Step
    {
        private double _sum;
        private double _heatSum;
        private int _heatMinutes;
        private bool _peakMissing;

        public int Minutes { get; private set; }

        public double Average => Minutes > 0 ? _sum / Minutes : 0;

        public double? Peak { get; private set; }

        public double? HeatAverage => _heatMinutes > 0 ? _heatSum / _heatMinutes : null;

        public double? HeatPeak { get; private set; }

        public void Add(int minutes, double average, double? peak, double? heatAverage, double? heatPeak)
        {
            Minutes += minutes;
            _sum += average * minutes;

            if (peak is { } high && !_peakMissing)
            {
                Peak = Math.Max(Peak ?? double.MinValue, high);
            }
            else
            {
                // One step without a peak means the step's peak is not known.
                _peakMissing = true;
                Peak = null;
            }

            if (heatAverage is { } warm)
            {
                _heatSum += warm * minutes;
                _heatMinutes += minutes;
            }

            if (heatPeak is { } hot)
            {
                HeatPeak = Math.Max(HeatPeak ?? double.MinValue, hot);
            }
        }
    }
}
