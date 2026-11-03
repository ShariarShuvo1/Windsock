using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Formatting;
using Windsock.Core.History;

namespace Windsock.App.ViewModels;

/// <summary>One row of the history table: a minute, an hour or a day.</summary>
public sealed partial class HistoryRowViewModel : ObservableObject
{
    /// <summary>A time to the minute on Windows' own clock, 12 or 24-hour, read afresh each time.</summary>
    private static string ClockPattern => ClockFormat.Current.ShortTime;

    public HistoryRowViewModel(UsageBucket bucket, UsageGranularity granularity)
    {
        Start = bucket.Start;
        End = EndOf(bucket, granularity);
        BytesDown = bucket.AllDown;
        BytesUp = bucket.AllUp;
        BytesTotal = bucket.AllTotal;
        PeakDownBytes = bucket.PeakDown;
        PeakUpBytes = bucket.PeakUp;
        MinutesRecorded = bucket.Minutes;
        IsCountedByWindows = bucket.HasEstimate;
        IsOnlyWindows = bucket.HasEstimate && bucket.Minutes == 0;
        Source = !bucket.HasEstimate
            ? string.Empty
            : bucket.Minutes == 0
                ? "Counted by Windows while Windsock was closed"
                : string.Format(
                    CultureInfo.CurrentCulture,
                    "Includes {0} Windows counted while Windsock was closed",
                    SizeFormatter.Format(bucket.EstimatedDown + bucket.EstimatedUp));

        From = Start.ToString(PatternFor(granularity), CultureInfo.CurrentCulture);
        To = granularity == UsageGranularity.Day || End.Date != Start.Date
            ? End.ToString(PatternFor(granularity), CultureInfo.CurrentCulture)
            : End.ToString(ClockPattern, CultureInfo.CurrentCulture);

        Download = SizeFormatter.Format(bucket.AllDown);
        Upload = SizeFormatter.Format(bucket.AllUp);
        Total = SizeFormatter.Format(bucket.AllTotal);
        PeakDown = Rate(bucket.PeakDown);
        PeakUp = Rate(bucket.PeakUp);
        Recorded = granularity == UsageGranularity.Minute
            ? string.Empty
            : bucket.Minutes.ToString("N0", CultureInfo.CurrentCulture);
    }

    /// <summary>When the bucket begins, in local time.</summary>
    public DateTimeOffset Start { get; }

    /// <summary>When the bucket ends, in local time. Exclusive.</summary>
    public DateTimeOffset End { get; }

    /// <summary>Bucket start, written for the column.</summary>
    public string From { get; }

    /// <summary>Bucket end, written for the column.</summary>
    public string To { get; }

    public string Download { get; }

    public string Upload { get; }

    public string Total { get; }

    public string PeakDown { get; }

    public string PeakUp { get; }

    /// <summary>Minutes of the bucket that were actually recorded.</summary>
    public string Recorded { get; }

    /// <summary>The network the bucket was on, "and N more" where it was on several, or nothing where that is not known.</summary>
    public string Network { get; init; } = string.Empty;

    /// <summary>
    /// The network for a screen reader: the cell's words, or that it is not
    /// known where the cell is blank. A blank cell was read by the row's type.
    /// </summary>
    public string NetworkSpoken => Network.Length > 0 ? Network : "Network not known";

    /// <summary>The row menu's name, with the row in it: every row has one.</summary>
    public string ActionsName => string.Concat("Actions for ", From);

    public long BytesDown { get; }

    public long BytesUp { get; }

    public long BytesTotal { get; }

    /// <summary>Fastest inbound second in the bucket, in bytes per second.</summary>
    public long PeakDownBytes { get; }

    /// <summary>Fastest outbound second in the bucket, in bytes per second.</summary>
    public long PeakUpBytes { get; }

    /// <summary>How many recorded minutes went into the bucket.</summary>
    public int MinutesRecorded { get; }

    /// <summary>Whether any of the row is Windows' count rather than Windsock's.</summary>
    public bool IsCountedByWindows { get; }

    /// <summary>Whether all of the row is Windows' count: time Windsock was closed.</summary>
    public bool IsOnlyWindows { get; }

    /// <summary>Where the figures came from, said on hover; empty when all recorded.</summary>
    public string Source { get; }

    /// <summary>
    /// Whether this row is the one being pointed at, here or on the chart.
    /// </summary>
    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    /// <summary>Whether anything came in, so the figure carries its colour.</summary>
    public bool HasReceive => BytesDown > 0;

    /// <summary>Whether anything went out, so the figure carries its colour.</summary>
    public bool HasSend => BytesUp > 0;

    /// <summary>
    /// What a screen reader says for the row: the stretch, what moved each
    /// way, the network and where the figures came from. Without it the row
    /// was read by its type's name.
    /// </summary>
    public override string ToString() => string.Join(
        ", ",
        new[]
        {
            string.Concat(From, " to ", To),
            string.Concat(Download, " down"),
            string.Concat(Upload, " up"),
            string.Concat(Total, " in all"),
            Network.Length > 0 ? string.Concat("on ", Network) : string.Empty,
            Source,
        }.Where(part => part.Length > 0));

    private static DateTimeOffset EndOf(UsageBucket bucket, UsageGranularity granularity)
    {
        DateTimeOffset start = bucket.Start;

        // By the minute, a stretch Windows only counts by the hour is one row
        // covering all of it, not an hour's bytes put down in its first minute.
        DateTime local = granularity switch
        {
            UsageGranularity.Minute => start.LocalDateTime.AddMinutes(Math.Max(1, bucket.Minutes == 0 ? bucket.EstimatedMinutes : 1)),
            UsageGranularity.Hour => start.LocalDateTime.AddHours(1),
            _ => start.LocalDateTime.AddDays(1),
        };

        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static string Rate(long bytesPerSecond) =>
        bytesPerSecond <= 0 ? "—" : SizeFormatter.Format(bytesPerSecond) + "/s";

    private static string PatternFor(UsageGranularity granularity) => granularity switch
    {
        UsageGranularity.Minute or UsageGranularity.Hour => "ddd d MMM, " + ClockPattern,
        _ => "ddd d MMM yyyy",
    };
}
