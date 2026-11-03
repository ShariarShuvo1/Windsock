namespace Windsock.Core.History;

/// <summary>What can be done with the history file this run.</summary>
public enum HistoryFileState
{
    /// <summary>Read and written as usual.</summary>
    Ready,

    /// <summary>The file could not be opened at all; every call fails.</summary>
    Unavailable,

    /// <summary>
    /// A newer Windsock wrote the file in a way this version would damage, so it
    /// is read but never written. Nothing is recorded or erased until that
    /// version, or a later one, runs again.
    /// </summary>
    NewerVersion,
}

/// <summary>The history file as a file: its state, its copies and its bookmarks.</summary>
public interface IHistoryFile
{
    /// <summary>What can be done with it this run.</summary>
    HistoryFileState State { get; }

    /// <summary>The schema version written in the file, which is newer than this build's when <see cref="State"/> says so.</summary>
    int FileSchema { get; }

    /// <summary>Whether the previous file was unreadable and was set aside.</summary>
    bool RecoveredFromCorruption { get; }

    /// <summary>
    /// Whether this run finished an erase an older version of Windsock made.
    /// It happens while the file opens, before anyone can hear of it, so
    /// copies of the file made before it - which still hold what was erased -
    /// are let go on this rather than on <see cref="IUsageHistoryStore.Erased"/>.
    /// </summary>
    bool SettledOlderErase { get; }

    /// <summary>Where the file is.</summary>
    string FilePath { get; }

    /// <summary>
    /// Copies the whole file to a new one with SQLite's online backup, while
    /// nothing else - a write, an erase, a VACUUM - can touch it, and checks
    /// the copy. The copy is a single file, with no write-ahead log beside it.
    /// Throws a storage failure (see <see cref="HistoryFailure.IsStorage"/>) when
    /// the copy could not be made or does not check out; nothing is left behind then.
    /// </summary>
    void BackupTo(string path);

    /// <summary>A bookmark kept in the file, such as how far scheduled export has got; null when never set.</summary>
    long? ReadMark(string name);

    /// <summary>Sets a bookmark. Bookmarks are not history: erasing leaves them.</summary>
    void WriteMark(string name, long value);
}
