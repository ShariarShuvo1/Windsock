using Microsoft.Data.Sqlite;
using Windsock.Core.History;

namespace Windsock.Core.Tests;

/// <summary>
/// Makes a history database that opens but cannot be read, as the file behind
/// the crash on 2026-09-13 did: SQLite Error 11, "database disk image is
/// malformed", raised by the first query to touch a table.
/// </summary>
internal static class DamagedHistoryFile
{
    // Page one holds the header and the schema. Schema 6's tables and triggers
    // run past SQLite's default 4 KB page, so the file is made with pages big
    // enough that the whole schema still sits on the first one, and that page
    // alone is left whole.
    private const int PageSize = 65536;

    public static void Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (SqliteConnection empty = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
        {
            empty.Open();
            using SqliteCommand size = empty.CreateCommand();
            size.CommandText = $"PRAGMA page_size={PageSize}; VACUUM;";
            size.ExecuteNonQuery();
        }

        using (SqliteUsageHistoryStore store = new(path))
        {
            long start = EpochMinute.From(new DateTimeOffset(2026, 3, 14, 12, 0, 0, TimeSpan.Zero));
            List<UsageMinute> plenty = [];

            // Enough rows that the tables run to many pages.
            for (int each = 0; each < 5_000; each++)
            {
                plenty.Add(new UsageMinute(start + each, 1000, 100, 9, 9));
            }

            store.Write(plenty);
            store.WriteCoverage(start, start + 5_000);
        }

        // The first page is left whole, so the file opens and answers for its
        // schema version; every page of every table behind it is overwritten.
        using FileStream file = new(path, FileMode.Open, FileAccess.ReadWrite);
        byte[] rubbish = new byte[file.Length - PageSize];
        Array.Fill(rubbish, (byte)0xFF);

        file.Position = PageSize;
        file.Write(rubbish);
    }
}
