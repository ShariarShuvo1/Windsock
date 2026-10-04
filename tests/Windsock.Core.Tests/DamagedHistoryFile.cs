using Windsock.Core.History;

namespace Windsock.Core.Tests;

/// <summary>
/// Makes a history database that opens but cannot be read, as the file behind
/// the crash on 2026-09-13 did: SQLite Error 11, "database disk image is
/// malformed", raised by the first query to touch a table.
/// </summary>
internal static class DamagedHistoryFile
{
    // SQLite's default page size. Page one holds the header and the schema.
    private const int PageSize = 4096;

    public static void Create(string path)
    {
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
        // schema version; every page of both tables behind it is overwritten.
        using FileStream file = new(path, FileMode.Open, FileAccess.ReadWrite);
        byte[] rubbish = new byte[file.Length - PageSize];
        Array.Fill(rubbish, (byte)0xFF);

        file.Position = PageSize;
        file.Write(rubbish);
    }
}
