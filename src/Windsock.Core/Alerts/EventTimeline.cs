using Windsock.Core.Connectivity;
using Windsock.Core.History;
using Windsock.Core.Notifications;

namespace Windsock.Core.Alerts;

/// <summary>What a mark on a chart stands for.</summary>
public enum MarkerKind
{
    /// <summary>The internet was gone, from <see cref="TimelineMarker.At"/> to <see cref="TimelineMarker.Until"/>.</summary>
    Outage,

    /// <summary>The PC moved to another network.</summary>
    NetworkChange,

    /// <summary>A VPN went on or off.</summary>
    VpnChange,

    /// <summary>An alert was raised.</summary>
    Alert,
}

/// <summary>One thing to mark on the live or history chart.</summary>
/// <param name="At">When it happened, or began.</param>
/// <param name="Until">When it ended, for a span; nothing for a moment, or a span still going.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Level">How much it matters, for its colour.</param>
/// <param name="Title">What it was, in a few words, for the mark's tooltip.</param>
/// <param name="Detail">A line more.</param>
public sealed record TimelineMarker(
    DateTimeOffset At,
    DateTimeOffset? Until,
    MarkerKind Kind,
    AlertLevel Level,
    string Title,
    string Detail)
{
    /// <summary>The alert's row in the record, for a mark that is one; zero otherwise.</summary>
    public long AlertId { get; init; }

    /// <summary>The alert's kind, for a mark that is one.</summary>
    public string? Topic { get; init; }

    /// <summary>The outage's row in its log, for a mark that is one; zero otherwise.</summary>
    public long OutageId { get; init; }

    /// <summary>Whether it is a span rather than a moment.</summary>
    public bool IsSpan => Kind == MarkerKind.Outage;
}

/// <summary>
/// Outages, network changes and alerts as time-stamped marks, for the live and
/// history charts to draw.
/// </summary>
/// <remarks>
/// <para>
/// Read from the two records that already exist - the outage log and the
/// record of alerts - and merged here, so each chart asks one question.
/// </para>
/// <para>
/// Each happening is marked once. The connection's own alerts - lost, back,
/// network and VPN changes - are drawn as the outage or the change itself,
/// which is kept whether or not anybody asked to be told (network and VPN
/// changes as events in the record), so the marks do not depend on a switch
/// and never come twice.
/// </para>
/// <para>
/// Every member may throw for a record that cannot be read; callers catch what
/// <see cref="HistoryFailure.IsStorage"/> recognises and draw no marks.
/// </para>
/// </remarks>
public sealed class EventTimeline : IDisposable
{
    private static readonly HashSet<string> ConnectionTopics = new(StringComparer.Ordinal)
    {
        NoticeTopics.ConnectionLost,
        NoticeTopics.ConnectionRestored,
        NoticeTopics.NetworkChanged,
        NoticeTopics.VpnChanged,
    };

    private readonly IAlertLog _alerts;
    private readonly IOutageStore _outages;

    public EventTimeline(IAlertLog alerts, IOutageStore outages)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(outages);

        _alerts = alerts;
        _outages = outages;

        _alerts.Changed += OnChanged;
        _outages.Changed += OnChanged;
    }

    /// <summary>Raised, on the writing thread, when either record changed; a chart reads its marks again.</summary>
    public event EventHandler? Changed;

    /// <summary>Every mark that touches a stretch, oldest first.</summary>
    public IReadOnlyList<TimelineMarker> Read(DateTimeOffset from, DateTimeOffset until)
    {
        IReadOnlyList<Outage> outages = _outages.Read(from, until);
        IReadOnlyList<AlertRecord> records = _alerts.Read(new AlertQuery(from, until)
        {
            IncludeEvents = true,
            Take = 10_000,
        });

        return Merge(outages, records);
    }

    /// <summary>Merges outages and records into marks, oldest first, each happening once.</summary>
    public static IReadOnlyList<TimelineMarker> Merge(IReadOnlyList<Outage> outages, IReadOnlyList<AlertRecord> records)
    {
        ArgumentNullException.ThrowIfNull(outages);
        ArgumentNullException.ThrowIfNull(records);

        List<TimelineMarker> marks = new(outages.Count + records.Count);

        foreach (Outage outage in outages)
        {
            string detail = outage.Duration is { } duration
                ? string.Concat(OutageWords.Spoken(duration), outage.Network.Length > 0 ? string.Concat(", on ", outage.Network) : string.Empty)
                : outage.Network;

            marks.Add(new TimelineMarker(outage.Start, outage.End, MarkerKind.Outage, AlertLevel.Problem, OutageWords.Headline(outage.Cause), detail)
            {
                OutageId = outage.Id,
            });
        }

        foreach (AlertRecord record in records)
        {
            if (!record.IsAlert)
            {
                MarkerKind kind = string.Equals(record.Topic, NoticeTopics.VpnChanged, StringComparison.Ordinal)
                    ? MarkerKind.VpnChange
                    : MarkerKind.NetworkChange;

                marks.Add(new TimelineMarker(record.At, null, kind, AlertLevel.Info, record.Title, record.Message)
                {
                    Topic = record.Topic,
                    AlertId = record.Id,
                });

                continue;
            }

            if (ConnectionTopics.Contains(record.Topic))
            {
                continue;
            }

            marks.Add(new TimelineMarker(record.At, null, MarkerKind.Alert, record.Level, record.Title, record.Message)
            {
                Topic = record.Topic,
                AlertId = record.Id,
            });
        }

        marks.Sort((a, b) => a.At.CompareTo(b.At));
        return marks;
    }

    public void Dispose()
    {
        _alerts.Changed -= OnChanged;
        _outages.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
