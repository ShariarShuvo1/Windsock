using Microsoft.Data.Sqlite;

namespace Windsock.Core.History;

/// <summary>Notes on history; see <see cref="IHistoryNotes"/>.</summary>
public sealed partial class SqliteUsageHistoryStore
{
    /// <inheritdoc />
    public event EventHandler? NotesChanged;

    /// <inheritdoc />
    public HistoryNote AddNote(DateTimeOffset from, DateTimeOffset until, string text)
    {
        (long fromMinute, long untilMinute, string clean) = CheckNote(from, until, text);
        long created = EpochMinute.From(_clock.GetUtcNow());
        long id;

        lock (_gate)
        {
            using SqliteCommand command = WritableConnection().CreateCommand();
            command.CommandText =
                """
                INSERT INTO note (from_minute, until_minute, text, created) VALUES (@from, @until, @text, @created)
                RETURNING id;
                """;
            command.Parameters.AddWithValue("@from", fromMinute);
            command.Parameters.AddWithValue("@until", untilMinute);
            command.Parameters.AddWithValue("@text", clean);
            command.Parameters.AddWithValue("@created", created);
            id = (long)command.ExecuteScalar()!;
        }

        NotesChanged?.Invoke(this, EventArgs.Empty);
        return new HistoryNote(id, EpochMinute.ToLocal(fromMinute), EpochMinute.ToLocal(untilMinute), clean, EpochMinute.ToLocal(created));
    }

    /// <inheritdoc />
    public bool UpdateNote(long id, DateTimeOffset from, DateTimeOffset until, string text)
    {
        (long fromMinute, long untilMinute, string clean) = CheckNote(from, until, text);
        int changed;

        lock (_gate)
        {
            using SqliteCommand command = WritableConnection().CreateCommand();
            command.CommandText = "UPDATE note SET from_minute = @from, until_minute = @until, text = @text WHERE id = @id;";
            command.Parameters.AddWithValue("@from", fromMinute);
            command.Parameters.AddWithValue("@until", untilMinute);
            command.Parameters.AddWithValue("@text", clean);
            command.Parameters.AddWithValue("@id", id);
            changed = command.ExecuteNonQuery();
        }

        if (changed > 0)
        {
            NotesChanged?.Invoke(this, EventArgs.Empty);
        }

        return changed > 0;
    }

    /// <inheritdoc />
    public bool RemoveNote(long id)
    {
        int removed;

        lock (_gate)
        {
            using SqliteCommand command = WritableConnection().CreateCommand();
            command.CommandText = "DELETE FROM note WHERE id = @id;";
            command.Parameters.AddWithValue("@id", id);
            removed = command.ExecuteNonQuery();
        }

        if (removed > 0)
        {
            NotesChanged?.Invoke(this, EventArgs.Empty);
        }

        return removed > 0;
    }

    /// <inheritdoc />
    public IReadOnlyList<HistoryNote> ReadNotes(DateTimeOffset from, DateTimeOffset until)
    {
        List<HistoryNote> notes = [];

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                SELECT id, from_minute, until_minute, text, created FROM note
                WHERE from_minute < @to AND until_minute > @from
                ORDER BY from_minute, id;
                """;
            command.Parameters.AddWithValue("@from", EpochMinute.From(from));
            command.Parameters.AddWithValue("@to", EpochMinute.From(until));

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                notes.Add(new HistoryNote(
                    reader.GetInt64(0),
                    EpochMinute.ToLocal(reader.GetInt64(1)),
                    EpochMinute.ToLocal(reader.GetInt64(2)),
                    reader.GetString(3),
                    EpochMinute.ToLocal(reader.GetInt64(4))));
            }
        }

        return notes;
    }

    private static (long From, long Until, string Text) CheckNote(DateTimeOffset from, DateTimeOffset until, string text)
    {
        string clean = HistoryNote.Clean(text) ?? throw new ArgumentException("A note needs some text.", nameof(text));
        long fromMinute = EpochMinute.From(from);
        long untilMinute = EpochMinute.From(until);

        if (untilMinute <= fromMinute)
        {
            throw new ArgumentException("A note's stretch must end after it starts.", nameof(until));
        }

        return (fromMinute, untilMinute, clean);
    }

    /// <summary>
    /// Takes an erased period out of the notes: a note inside it goes, and one
    /// reaching into it from either side is cut back to the side left. A note
    /// running right across it stays as it was - the hole does not make the
    /// label wrong. Call inside the erase's transaction.
    /// </summary>
    private static void TrimNotes(SqliteConnection connection, SqliteTransaction transaction, long fromMinute, long untilMinute)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM note WHERE from_minute >= @from AND until_minute <= @to;
            UPDATE note SET from_minute = @to WHERE from_minute >= @from AND from_minute < @to AND until_minute > @to;
            UPDATE note SET until_minute = @from WHERE until_minute > @from AND until_minute <= @to AND from_minute < @from;
            """;
        command.Parameters.AddWithValue("@from", fromMinute);
        command.Parameters.AddWithValue("@to", untilMinute);
        command.ExecuteNonQuery();
    }
}
