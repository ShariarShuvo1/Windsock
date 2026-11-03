namespace Windsock.Core.History;

/// <summary>A label on a stretch of history: "game update", "ISP outage", "moved the router".</summary>
/// <param name="Id">Its row.</param>
/// <param name="From">Where the stretch starts.</param>
/// <param name="Until">Where it ends. Exclusive.</param>
/// <param name="Text">What it says.</param>
/// <param name="Created">When it was written.</param>
public sealed record HistoryNote(long Id, DateTimeOffset From, DateTimeOffset Until, string Text, DateTimeOffset Created)
{
    /// <summary>The longest a note may be, in characters.</summary>
    public const int LongestText = 200;

    /// <summary>
    /// The text as it is kept: trimmed, on one line, no longer than
    /// <see cref="LongestText"/>; null when nothing is left to keep.
    /// </summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string oneLine = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        oneLine = oneLine.Trim();

        if (oneLine.Length == 0)
        {
            return null;
        }

        return oneLine.Length <= LongestText ? oneLine : oneLine[..LongestText].TrimEnd();
    }
}

/// <summary>
/// Notes on history. They are history too: erasing a stretch takes the notes
/// inside it, trims the ones reaching into it, and an export carries them.
/// </summary>
public interface IHistoryNotes
{
    /// <summary>Adds a note, to the minute. Throws <see cref="ArgumentException"/> for empty text or a stretch that ends before it starts.</summary>
    HistoryNote AddNote(DateTimeOffset from, DateTimeOffset until, string text);

    /// <summary>Changes a note; false when there is no such note.</summary>
    bool UpdateNote(long id, DateTimeOffset from, DateTimeOffset until, string text);

    /// <summary>Removes a note; false when there was no such note.</summary>
    bool RemoveNote(long id);

    /// <summary>The notes touching a period, earliest first.</summary>
    IReadOnlyList<HistoryNote> ReadNotes(DateTimeOffset from, DateTimeOffset until);

    /// <summary>Raised after a note is added, changed or removed, or erased with its stretch.</summary>
    event EventHandler? NotesChanged;
}
