using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Formatting;
using Windsock.Core.History;

namespace Windsock.App.ViewModels;

/// <summary>One row of the history table: a minute, an hour or a day.</summary>
public sealed partial class HistoryRowViewModel : ObservableObject
{
    // A 12 hour clock, matching the picker the range is chosen with.
    private const string ClockPattern = "h:mm tt";

    public HistoryRowViewModel(UsageBucket bucket, UsageGranularity granularity)
    {
        Start = bucket.Start;
        End = EndOf(bucket.Start, granularity);
        BytesDown = bucket.BytesDown;
        BytesUp = bucket.BytesUp;
        BytesTotal = bucket.BytesTotal;
        PeakDownBytes = bucket.PeakDown;
        PeakUpBytes = bucket.PeakUp;
        MinutesRecorded = bucket.Minutes;

        From = Start.ToString(PatternFor(granularity), CultureInfo.CurrentCulture);
        To = granularity == UsageGranularity.Day || End.Date != Start.Date
            ? End.ToString(PatternFor(granularity), CultureInfo.CurrentCulture)
            : End.ToString(ClockPattern, CultureInfo.CurrentCulture);

        Download = SizeFormatter.Format(bucket.BytesDown);
        Upload = SizeFormatter.Format(bucket.BytesUp);
        Total = SizeFormatter.Format(bucket.BytesTotal);
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

    public long BytesDown { get; }

    public long BytesUp { get; }

    public long BytesTotal { get; }

    /// <summary>Fastest inbound second in the bucket, in bytes per second.</summary>
    public long PeakDownBytes { get; }

    /// <summary>Fastest outbound second in the bucket, in bytes per second.</summary>
    public long PeakUpBytes { get; }

    /// <summary>How many recorded minutes went into the bucket.</summary>
    public int MinutesRecorded { get; }

    /// <summary>
    /// Whether this row is the one being pointed at, here or on the chart.
    /// </summary>
    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    /// <summary>Whether anything came in, so the figure carries its colour.</summary>
    public bool HasReceive => BytesDown > 0;

    /// <summary>Whether anything went out, so the figure carries its colour.</summary>
    public bool HasSend => BytesUp > 0;

    private static DateTimeOffset EndOf(DateTimeOffset start, UsageGranularity granularity)
    {
        DateTime local = granularity switch
        {
            UsageGranularity.Minute => start.LocalDateTime.AddMinutes(1),
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
