using System.Text.Json.Serialization;

namespace Windsock.Core.Settings;

/// <summary>
/// Choices for what Windsock says about the programs using the network and
/// the places they reach: new-app notices, blocklists, and the connection
/// timeline.
/// </summary>
/// <remarks>
/// The blocklists are outside contact - each is fetched from its publisher -
/// so both start off and the privacy page lists them. The timeline keeps the
/// names of places programs reached, which is close to browsing history, so it
/// starts off too. A VirusTotal key is not kept here: it is the person's
/// secret, protected for their account in a file of its own, and must never
/// travel in an exported settings file.
/// </remarks>
public sealed class InsightSettings
{
    /// <summary>The choices for how long the timeline keeps what it recorded, in days.</summary>
    public static IReadOnlyList<int> TimelineChoices { get; } = [1, 7, 30, 90, 365];

    /// <summary>
    /// Whether a notice is sent when a program uses the network for the first
    /// time. Off until switched on, on the Alerts page, like the other alerts
    /// added in 0.2.0.
    /// </summary>
    public bool NewAppNotices { get; set; }

    /// <summary>
    /// Whether Spamhaus DROP is fetched (at most once an hour, from
    /// spamhaus.org) and connections to its netblocks marked.
    /// </summary>
    public bool SpamhausDrop { get; set; }

    /// <summary>
    /// Whether abuse.ch's Feodo Tracker botnet list is fetched (at most once an
    /// hour, from abuse.ch) and connections to its addresses marked.
    /// </summary>
    public bool FeodoTracker { get; set; }

    /// <summary>
    /// Whether the full edition, running as administrator, keeps a record of
    /// which app connected where and when, and each day's top places and
    /// countries.
    /// </summary>
    public bool Timeline { get; set; }

    /// <summary>How many days the timeline keeps, one of <see cref="TimelineChoices"/>.</summary>
    public int TimelineDays { get; set; } = 30;

    /// <summary>The settings to carry to another machine: all of them, as none names this one.</summary>
    public InsightSettings Copy() => new()
    {
        NewAppNotices = NewAppNotices,
        SpamhausDrop = SpamhausDrop,
        FeodoTracker = FeodoTracker,
        Timeline = Timeline,
        TimelineDays = TimelineDays,
    };

    /// <summary>The kept days, held to the nearest choice offered.</summary>
    [JsonIgnore]
    public int KeptDays =>
        TimelineChoices.MinBy(choice => Math.Abs(choice - TimelineDays));
}
