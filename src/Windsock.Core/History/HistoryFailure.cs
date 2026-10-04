using System.Data.Common;

namespace Windsock.Core.History;

/// <summary>
/// Tells a history database that could not answer apart from a bug in the code
/// that asked it.
/// </summary>
/// <remarks>
/// A damaged, locked, full or unopenable file is something Windsock carries on
/// without, so every caller of <see cref="IUsageHistoryStore"/> catches these and
/// degrades. Anything else is left to reach the global handlers, which end the
/// app rather than let it go on showing figures nobody can trust.
/// </remarks>
public static class HistoryFailure
{
    /// <summary>
    /// Whether an exception from the store means the file could not be read
    /// or written, as opposed to a defect.
    /// </summary>
    public static bool IsStorage(Exception exception) =>
        exception is DbException or IOException or InvalidOperationException;
}
