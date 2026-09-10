namespace Windsock.Core.History;

/// <summary>
/// A stretch of time Windsock was actually running and recording.
/// </summary>
public readonly record struct CoverageWindow(DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>How long the run covered, counting the final minute.</summary>
    public TimeSpan Duration => End - Start + TimeSpan.FromMinutes(1);
}

/// <summary>The oldest and newest minutes on record.</summary>
public readonly record struct UsageExtent(DateTimeOffset? First, DateTimeOffset? Last)
{
    /// <summary>Whether anything has been recorded at all.</summary>
    public bool HasData => First is not null;
}
