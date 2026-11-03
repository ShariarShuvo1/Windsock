namespace Windsock.Core.Tests;

/// <summary>A clock the step 7 tests move by hand.</summary>
internal sealed class InsightClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset to) => _now = to;
}
