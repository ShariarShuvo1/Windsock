using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.App.Controls;
using Windsock.Core.Formatting;
using Windsock.Core.Hardware;

namespace Windsock.App.ViewModels;

/// <summary>One entry in the history span picker.</summary>
public sealed record PartSpanOption(PartSpan Span, string Label)
{
    /// <summary>Returns the label, so automation announces it by name.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// A part of the machine over its last day or week, for the band at the foot
/// of its page.
/// </summary>
/// <remarks>
/// Drawn from the minutes History keeps for its machine lanes; nothing is
/// recorded for this alone. The scales are put into words here, so the chart
/// knows nothing of percent, bytes or degrees, and a temperature follows the
/// scale Settings › Appearance has chosen.
/// </remarks>
public sealed partial class PartHistoryViewModel : ObservableObject
{
    private PartSeries? _series;
    private SystemPart _part = SystemPart.Processor;

    /// <summary>Raised when the reader chooses another span, to read it.</summary>
    public event EventHandler? SpanChanged;

    /// <summary>The spans on offer.</summary>
    public IReadOnlyList<PartSpanOption> Spans { get; } =
    [
        new(PartSpan.Day, "Last 24 hours"),
        new(PartSpan.Week, "Last 7 days"),
    ];

    /// <summary>The span showing.</summary>
    [ObservableProperty]
    public partial PartSpan SelectedSpan { get; set; } = PartSpan.Day;

    /// <summary>Which part the chart is about, for its colour.</summary>
    [ObservableProperty]
    public partial SystemPart Part { get; private set; } = SystemPart.Processor;

    /// <summary>The steps, oldest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HistoryStep> Steps { get; private set; } = [];

    /// <summary>The top of the scale, in words.</summary>
    [ObservableProperty]
    public partial string TopLabel { get; private set; } = string.Empty;

    /// <summary>The middle of the scale, in words.</summary>
    [ObservableProperty]
    public partial string MiddleLabel { get; private set; } = string.Empty;

    /// <summary>The top of the temperature scale, or nothing where there is no temperature.</summary>
    [ObservableProperty]
    public partial string HeatTopLabel { get; private set; } = string.Empty;

    /// <summary>The bottom of the temperature scale.</summary>
    [ObservableProperty]
    public partial string HeatBottomLabel { get; private set; } = string.Empty;

    /// <summary>The span summed up in a line.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>What the bars and the line are, in a line under the title.</summary>
    [ObservableProperty]
    public partial string Note { get; private set; } = string.Empty;

    /// <summary>Whether there is anything to draw.</summary>
    [ObservableProperty]
    public partial bool HasData { get; private set; }

    /// <summary>What the empty chart says.</summary>
    [ObservableProperty]
    public partial string EmptyTitle { get; private set; } = "Reading the history";

    /// <summary>Why it is empty, and what to do.</summary>
    [ObservableProperty]
    public partial string EmptyNote { get; private set; } = string.Empty;

    partial void OnSelectedSpanChanged(PartSpan value) => SpanChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Clears the chart for a part, before its history is read.</summary>
    public void Reset(SystemPart part)
    {
        _part = part;
        _series = null;
        Part = part;
        Steps = [];
        HasData = false;
        Summary = string.Empty;
        TopLabel = MiddleLabel = HeatTopLabel = HeatBottomLabel = string.Empty;
        Note = Describe(part);
        EmptyTitle = "Reading the history";
        EmptyNote = string.Empty;
    }

    /// <summary>Says the history could not be read.</summary>
    public void Fail()
    {
        _series = null;
        Steps = [];
        HasData = false;
        Summary = string.Empty;
        EmptyTitle = "The history could not be read";
        EmptyNote = "The history file is busy or damaged. The live figures above are unaffected.";
    }

    /// <summary>Shows what was read.</summary>
    /// <param name="series">The part's history.</param>
    /// <param name="recording">Whether history is being recorded now, for what the empty chart says.</param>
    public void Show(PartSeries series, bool recording)
    {
        ArgumentNullException.ThrowIfNull(series);

        _series = series;
        Draw(recording);
    }

    /// <summary>Writes the same history again, as a change of temperature scale asks.</summary>
    public void Restate(bool recording)
    {
        if (_series is not null)
        {
            Draw(recording);
        }
    }

    private void Draw(bool recording)
    {
        PartSeries series = _series!;
        (double top, Func<double, string> words) = Scale(series);
        (double heatLow, double heatHigh) = HeatRange(series);
        bool hasHeat = heatHigh > heatLow;

        int count = Math.Max(0, (int)Math.Round((series.Until - series.From) / series.Step));
        Dictionary<DateTimeOffset, PartPoint> byStart = [];

        foreach (PartPoint point in series.Points)
        {
            byStart[point.Start] = point;
        }

        List<HistoryStep> steps = new(count);

        for (int index = 0; index < count; index++)
        {
            DateTimeOffset start = series.From + (series.Step * index);
            string label = Label(start, series.Span);

            if (!byStart.TryGetValue(start, out PartPoint point))
            {
                steps.Add(new HistoryStep(label, string.Concat(When(start, series), ": nothing recorded"), null, null, null));
                continue;
            }

            double? heat = point.HeatAverage ?? point.HeatPeak;

            steps.Add(new HistoryStep(
                label,
                Sentence(point, start, series, words),
                top > 0 ? point.Average / top : 0,
                point.Peak is { } peak && top > 0 ? peak / top : null,
                hasHeat && heat is { } warm ? (warm - heatLow) / (heatHigh - heatLow) : null));
        }

        Steps = steps;
        HasData = series.HasData;
        TopLabel = top > 0 ? words(top) : string.Empty;
        MiddleLabel = top > 0 ? words(top / 2) : string.Empty;
        HeatTopLabel = hasHeat ? TemperatureScale.Format(heatHigh) : string.Empty;
        HeatBottomLabel = hasHeat ? TemperatureScale.Format(heatLow) : string.Empty;
        Summary = series.HasData ? Summarise(series, words) : string.Empty;

        if (!series.HasData)
        {
            EmptyTitle = series.Span == PartSpan.Day ? "Nothing recorded in the last 24 hours" : "Nothing recorded in the last 7 days";
            EmptyNote = recording
                ? "The machine's readings are kept a minute at a time while Windsock runs with history on, so this fills in as it runs."
                : "History is paused, so nothing new is kept. Turn it back on in Settings › Data and privacy, or on the History page.";
        }
    }

    /// <summary>The top of the bars' scale, and how a figure on it is written.</summary>
    private (double Top, Func<double, string> Words) Scale(PartSeries series)
    {
        switch (_part)
        {
            case SystemPart.Memory:
                double fitted = series.Capacity ?? Highest(series);
                return (fitted, static bytes => SizeFormatter.Format((long)bytes));

            case SystemPart.Storage:
                double busiest = Highest(series);
                return (busiest > 0 ? busiest : 0, static rate => RateFormatter.Format(rate, RateFamily.Bytes, RateScale.Auto));

            default:
                return (100, static percent => string.Create(CultureInfo.InvariantCulture, $"{percent:F0}%"));
        }
    }

    private static double Highest(PartSeries series)
    {
        double highest = 0;

        foreach (PartPoint point in series.Points)
        {
            highest = Math.Max(highest, Math.Max(point.Average, point.Peak ?? 0));
        }

        return highest;
    }

    /// <summary>
    /// The temperature scale: the readings' own range, opened out to whole
    /// fives and at least twenty degrees, so a steady part draws a steady line
    /// rather than noise blown up to the full height.
    /// </summary>
    private static (double Low, double High) HeatRange(PartSeries series)
    {
        double low = double.MaxValue;
        double high = double.MinValue;

        foreach (PartPoint point in series.Points)
        {
            foreach (double? value in (ReadOnlySpan<double?>)[point.HeatAverage, point.HeatPeak])
            {
                if (value is { } heat)
                {
                    low = Math.Min(low, heat);
                    high = Math.Max(high, heat);
                }
            }
        }

        if (low > high)
        {
            return (0, 0);
        }

        low = Math.Floor(low / 5) * 5;
        high = Math.Ceiling(high / 5) * 5;

        if (high - low < 20)
        {
            double middle = (high + low) / 2;
            low = Math.Floor((middle - 10) / 5) * 5;
            high = low + 20;
        }

        return (low, high);
    }

    private static string Label(DateTimeOffset start, PartSpan span)
    {
        DateTime local = start.LocalDateTime;
        ClockPatterns clock = ClockFormat.Current;

        if (span == PartSpan.Week)
        {
            return local.Hour == 0 ? local.ToString("ddd d", clock.Format) : string.Empty;
        }

        return local.Minute == 0 && local.Hour % 3 == 0 ? clock.Hour(local) : string.Empty;
    }

    private static string When(DateTimeOffset start, PartSeries series)
    {
        DateTime from = start.LocalDateTime;
        DateTime until = (start + series.Step).LocalDateTime;
        ClockPatterns clock = ClockFormat.Current;

        return series.Span == PartSpan.Week
            ? string.Concat(clock.Day(from), " to ", clock.Time(until))
            : string.Concat(clock.Time(from), " to ", clock.Time(until));
    }

    private string Sentence(PartPoint point, DateTimeOffset start, PartSeries series, Func<double, string> words)
    {
        List<string> parts = [string.Concat(words(point.Average), " on average")];

        if (point.Peak is { } peak)
        {
            parts.Add(string.Concat(_part == SystemPart.Storage ? "busiest minute " : "peak ", words(peak)));
        }

        if (point.HeatAverage is { } warm)
        {
            parts.Add(TemperatureScale.Format(warm));
        }

        if (point.HeatPeak is { } hot)
        {
            parts.Add(string.Concat("hottest ", TemperatureScale.Format(hot)));
        }

        if (point.Minutes < series.Step.TotalMinutes)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{point.Minutes} of {series.Step.TotalMinutes:F0} minutes recorded"));
        }

        return string.Concat(When(start, series), ": ", string.Join(", ", parts));
    }

    private string Summarise(PartSeries series, Func<double, string> words)
    {
        List<string> parts = [];

        if (series.Average is { } average)
        {
            parts.Add(_part == SystemPart.Memory && series.Capacity is { } fitted
                ? string.Concat("Average ", words(average), " of ", words(fitted))
                : string.Concat("Average ", words(average)));
        }

        if (series.Peak is { } peak)
        {
            parts.Add(string.Concat(_part switch
            {
                SystemPart.Memory => "most ",
                SystemPart.Storage => "busiest minute ",
                _ => "peak ",
            }, words(peak)));
        }

        if (series.HeatAverage is { } warm)
        {
            parts.Add(string.Concat(TemperatureScale.Format(warm), " on average"));
        }

        if (series.HeatPeak is { } hot)
        {
            parts.Add(string.Concat("hottest ", TemperatureScale.Format(hot)));
        }

        return string.Join(" · ", parts);
    }

    private static string Describe(SystemPart part) => part switch
    {
        SystemPart.Graphics => "The busiest adapter's load each step: its average, with the peak fainter behind it. The line is the hottest reading.",
        SystemPart.Memory => "Memory in use each step: its average, with the most at any reading fainter behind it.",
        SystemPart.Storage => "Bytes read and written across every drive, as a rate over each step.",
        _ => "Load each step: its average, with the peak fainter behind it. The line is the temperature, where it is read.",
    };
}
