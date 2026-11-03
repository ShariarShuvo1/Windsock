namespace Windsock.Core.Alerts;

/// <summary>What became of an alert.</summary>
/// <remarks>Stored as its name, so reordering this list can never change a file.</remarks>
public enum AlertOutcome
{
    /// <summary>Shown when it was raised.</summary>
    Shown,

    /// <summary>Held during quiet hours or a snooze, waiting for them to end.</summary>
    Held,

    /// <summary>Held, then shown in the summary when quiet hours or the snooze ended.</summary>
    Summarised,

    /// <summary>Raised but not shown; <see cref="AlertRecord.Detail"/> says why.</summary>
    NotShown,

    /// <summary>
    /// Not an alert at all: something that happened - a network change - kept
    /// so the charts can mark it, whether or not anybody asked to be told.
    /// </summary>
    Event,
}

/// <summary>Whether an alert was forwarded to the webhook or ntfy topic the person set up.</summary>
public enum ForwardOutcome
{
    /// <summary>Not forwarded: forwarding is off, or not for this kind.</summary>
    None,

    /// <summary>On its way.</summary>
    Waiting,

    /// <summary>The destination took it.</summary>
    Sent,

    /// <summary>It could not be sent; <see cref="AlertRecord.ForwardDetail"/> says why. Not tried again.</summary>
    Failed,
}

/// <summary>One alert raised, as the record keeps it.</summary>
/// <param name="Id">Its row, or zero before it is kept.</param>
/// <param name="At">When it was raised.</param>
/// <param name="Topic">Its kind (see <see cref="AlertCatalogue"/>).</param>
/// <param name="Title">Its first line.</param>
/// <param name="Message">What it said.</param>
public sealed record AlertRecord(long Id, DateTimeOffset At, string Topic, string Title, string Message)
{
    /// <summary>The tag it carried, which names the thing it is about: a drive, a plan, a version.</summary>
    public string? Tag { get; init; }

    /// <summary>The section of the window it opens.</summary>
    public string? Opens { get; init; }

    /// <summary>What became of it.</summary>
    public AlertOutcome Outcome { get; init; }

    /// <summary>Why it was not shown, in a sentence; empty otherwise.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>When it was shown, alone or in a summary; nothing while held or when never shown.</summary>
    public DateTimeOffset? ShownAt { get; init; }

    /// <summary>Whether the person has seen it in the list.</summary>
    public bool IsRead { get; init; }

    /// <summary>Whether it was forwarded.</summary>
    public ForwardOutcome Forward { get; init; }

    /// <summary>Why forwarding failed, in a sentence; empty otherwise.</summary>
    public string ForwardDetail { get; init; } = string.Empty;

    /// <summary>How much it matters.</summary>
    public AlertLevel Level => AlertCatalogue.LevelOf(Topic);

    /// <summary>Whether it is an alert rather than an event kept only for the charts.</summary>
    public bool IsAlert => Outcome != AlertOutcome.Event;
}

/// <summary>Which part of the record to read.</summary>
/// <param name="From">The earliest moment, inclusive.</param>
/// <param name="Until">The latest moment, exclusive.</param>
public sealed record AlertQuery(DateTimeOffset From, DateTimeOffset Until)
{
    /// <summary>Whether events kept only for the charts are included.</summary>
    public bool IncludeEvents { get; init; }

    /// <summary>Whether only alerts not yet read are wanted.</summary>
    public bool OnlyUnread { get; init; }

    /// <summary>Only these kinds, or every kind when empty.</summary>
    public IReadOnlyList<string> Topics { get; init; } = [];

    /// <summary>How many rows to skip, newest first.</summary>
    public int Skip { get; init; }

    /// <summary>The most rows to return.</summary>
    public int Take { get; init; } = 500;

    /// <summary>Everything ever kept, newest first.</summary>
    public static AlertQuery Everything { get; } = new(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
}
