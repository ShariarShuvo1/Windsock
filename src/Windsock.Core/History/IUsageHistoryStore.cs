namespace Windsock.Core.History;

/// <summary>
/// Long-term storage for how much the machine transferred, minute by minute.
/// </summary>
public interface IUsageHistoryStore
{
    void Write(IReadOnlyList<UsageMinute> minutes);

    void WriteCoverage(long startMinute, long endMinute);

    IReadOnlyList<UsageBucket> Read(DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity);

    UsageSummary Summarise(DateTimeOffset from, DateTimeOffset until);

    IReadOnlyList<CoverageWindow> ReadCoverage(DateTimeOffset from, DateTimeOffset until);

    UsageExtent Extent();

    int Delete(DateTimeOffset from, DateTimeOffset until);

    int RemoveEverything();

    event EventHandler? Erased;

    int Merge(IReadOnlyList<UsageMinute> minutes);

    int Replace(IReadOnlyList<UsageMinute> minutes);

    int CountExisting(IReadOnlyList<UsageMinute> minutes);
}
