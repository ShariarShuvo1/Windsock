using Windsock.Core.Connectivity;

namespace Windsock.Core.Security;

/// <summary>
/// The outside contacts step 7 adds, for the privacy page to list beside the
/// connection tools' own. Both start off.
/// </summary>
/// <remarks>
/// VirusTotal is not here: its permission is the person's key, kept outside
/// the settings (<see cref="ApiKeyVault"/>), so a settings switch cannot say
/// whether it is allowed. The page shows it from <see cref="ReputationService.HasKey"/>.
/// After a switch changes, <see cref="Blocklists.Apply"/> acts on it at once
/// rather than at the next look.
/// </remarks>
public static class InsightContacts
{
    /// <summary>Spamhaus DROP.</summary>
    public static OutsideContact SpamhausDrop { get; } = new(
        "blocklist-spamhaus-drop",
        "Spamhaus DROP blocklist",
        "Downloads Spamhaus's list of criminal netblocks, at most once an hour, so connections to them are marked. Your connections are checked on this PC and never sent.",
        _ => "www.spamhaus.org",
        settings => settings.Insight.SpamhausDrop,
        (settings, on) => settings.Insight.SpamhausDrop = on,
        FullEditionOnly: false);

    /// <summary>abuse.ch Feodo Tracker.</summary>
    public static OutsideContact FeodoTracker { get; } = new(
        "blocklist-feodo-tracker",
        "Feodo Tracker blocklist",
        "Downloads abuse.ch's list of botnet control servers, at most once an hour, so connections to them are marked. Your connections are checked on this PC and never sent.",
        _ => "feodotracker.abuse.ch",
        settings => settings.Insight.FeodoTracker,
        (settings, on) => settings.Insight.FeodoTracker = on,
        FullEditionOnly: false);

    /// <summary>Every contact here, in the order the page shows them.</summary>
    public static IReadOnlyList<OutsideContact> All { get; } = [SpamhausDrop, FeodoTracker];
}
