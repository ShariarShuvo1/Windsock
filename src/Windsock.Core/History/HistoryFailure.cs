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

    /// <summary>
    /// Whether a write was refused because a newer version of Windsock wrote the
    /// file (see <see cref="HistoryFileState.NewerVersion"/>). That lasts the whole
    /// run, so a recorder says so once rather than at every minute; see
    /// <see cref="WriteFailureLog"/>.
    /// </summary>
    public static bool IsReadOnly(Exception exception) => exception is HistoryReadOnlyException;
}

/// <summary>
/// A write refused because the history file belongs to a newer version of
/// Windsock, which this version only reads. A storage failure like any other
/// (<see cref="HistoryFailure.IsStorage"/>), told apart by
/// <see cref="HistoryFailure.IsReadOnly"/>.
/// </summary>
public sealed class HistoryReadOnlyException : InvalidOperationException
{
    public HistoryReadOnlyException()
    {
    }

    public HistoryReadOnlyException(string message)
        : base(message)
    {
    }

    public HistoryReadOnlyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Decides which of a recorder's write failures to log: every one, except a
/// refusal from a file a newer version owns after the first, which would
/// otherwise be logged every minute of the run without anything changing.
/// </summary>
public sealed class WriteFailureLog
{
    private int _readOnlySeen;

    /// <summary>Whether this failure is worth a line in the log.</summary>
    public bool ShouldLog(Exception exception) =>
        !HistoryFailure.IsReadOnly(exception) || Interlocked.Exchange(ref _readOnlySeen, 1) == 0;
}
