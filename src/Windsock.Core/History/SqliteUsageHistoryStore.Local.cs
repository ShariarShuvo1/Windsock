using Microsoft.Data.Sqlite;

namespace Windsock.Core.History;

/// <summary>The local-network share of minutes; see <see cref="ILocalTrafficStore"/>.</summary>
public sealed partial class SqliteUsageHistoryStore : ILocalTrafficStore
{
    /// <inheritdoc />
    public IReadOnlyList<long> WriteLocalTraffic(IReadOnlyList<LocalMinute> minutes)
    {
        ArgumentNullException.ThrowIfNull(minutes);

        List<long> set = [];

        if (minutes.Count == 0)
        {
            return set;
        }

        lock (_gate)
        {
            SqliteConnection connection = WritableConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE usage_minute SET lan_down = MIN(@down, down), lan_up = MIN(@up, up)
                WHERE minute = @minute;
                """;
            SqliteParameter minute = command.Parameters.Add("@minute", SqliteType.Integer);
            SqliteParameter down = command.Parameters.Add("@down", SqliteType.Integer);
            SqliteParameter up = command.Parameters.Add("@up", SqliteType.Integer);

            foreach (LocalMinute each in minutes)
            {
                minute.Value = each.Minute;
                down.Value = Math.Max(0, each.BytesDown);
                up.Value = Math.Max(0, each.BytesUp);

                if (command.ExecuteNonQuery() > 0)
                {
                    set.Add(each.Minute);
                }
            }

            transaction.Commit();
        }

        return set;
    }
}
