namespace Windsock.Core.Navigation;

/// <summary>What a search result is: a page, a setting on one, or a reading.</summary>
public enum SearchKind
{
    /// <summary>A whole page of the window.</summary>
    Page,

    /// <summary>Something that can be changed, on a page.</summary>
    Setting,

    /// <summary>Something measured and shown, on a page.</summary>
    Reading,
}

/// <summary>
/// One thing the search box can find: a page, a setting or a reading, the page
/// it is on and where on that page.
/// </summary>
/// <param name="Title">What it is called where it is shown: "Theme", "Response time".</param>
/// <param name="Link">The page it is on, by its link name (see <see cref="PageLink"/>).</param>
public sealed record SearchEntry(string Title, string Link)
{
    /// <summary>The way to it, as the sidebar names it: "Settings › Appearance".</summary>
    public string Where { get; init; } = string.Empty;

    /// <summary>Other words somebody might type for it, separated by spaces: "ping latency".</summary>
    public string Terms { get; init; } = string.Empty;

    /// <summary>
    /// What to bring into view and focus once the page is open: the automation
    /// name or element name of a control on it. Empty for the page itself.
    /// </summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>Whether it is a page, a setting or a reading.</summary>
    public SearchKind Kind { get; init; } = SearchKind.Setting;
}

/// <summary>
/// Everything the search box can find, and the finding.
/// </summary>
/// <remarks>
/// <para>
/// Matching is by words, so it is predictable: every word typed must begin a
/// word of the title or of the entry's other terms. "ping" finds "Address to
/// ping" by its title and "Response time" by its terms; "net ch" finds
/// "Network changes". A title that starts with what was typed comes before one
/// that only contains it, a title before a term, and a page before what is on
/// a page, so a page's own name always leads.
/// </para>
/// <para>
/// No spelling is guessed. A search that finds nothing says so and suggests
/// what to try, which is plainer than a list of near misses.
/// </para>
/// </remarks>
public sealed class SearchIndex
{
    private static readonly char[] Separators = [' ', '-', '/', '(', ')', ',', '.', '›', '\'', '·'];

    private readonly List<Indexed> _entries = [];

    /// <summary>How many entries the index holds.</summary>
    public int Count => _entries.Count;

    /// <summary>Every entry, in the order added.</summary>
    public IEnumerable<SearchEntry> Entries => _entries.Select(each => each.Entry);

    /// <summary>Adds an entry. An entry already held, by title, link and target, is not added twice.</summary>
    public void Add(SearchEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(entry.Title))
        {
            throw new ArgumentException("An entry needs a title.", nameof(entry));
        }

        bool held = _entries.Any(each =>
            string.Equals(each.Entry.Title, entry.Title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(each.Entry.Link, entry.Link, StringComparison.Ordinal)
            && string.Equals(each.Entry.Target, entry.Target, StringComparison.Ordinal));

        if (!held)
        {
            _entries.Add(new Indexed(entry, _entries.Count));
        }
    }

    /// <summary>Adds several entries.</summary>
    public void AddRange(IEnumerable<SearchEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        foreach (SearchEntry entry in entries)
        {
            Add(entry);
        }
    }

    /// <summary>
    /// The entries a query finds, best first, at most <paramref name="most"/>
    /// of them. Nothing for an empty query.
    /// </summary>
    public IReadOnlyList<SearchEntry> Find(string? query, int most = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(most);

        string[] typed = Words(query);

        if (typed.Length == 0)
        {
            return [];
        }

        string whole = string.Join(' ', typed);

        return _entries
            .Select(each => (each, score: Score(each, typed, whole)))
            .Where(pair => pair.score > 0)
            .OrderByDescending(pair => pair.score)
            .ThenBy(pair => pair.each.Entry.Kind)
            .ThenBy(pair => pair.each.Entry.Title.Length)
            .ThenBy(pair => pair.each.Order)
            .Take(most)
            .Select(pair => pair.each.Entry)
            .ToList();
    }

    /// <summary>How well an entry answers the words typed; zero when it does not.</summary>
    private static int Score(Indexed entry, string[] typed, string whole)
    {
        string title = entry.Title;
        int score;

        if (string.Equals(title, whole, StringComparison.Ordinal))
        {
            score = 100;
        }
        else if (title.StartsWith(whole, StringComparison.Ordinal))
        {
            score = 80;
        }
        else if (typed.All(word => Begins(entry.TitleWords, word)))
        {
            score = 60;
        }
        else if (typed.All(word => Begins(entry.TitleWords, word) || Begins(entry.TermWords, word)))
        {
            score = 40;
        }
        else if (whole.Length >= 3 && title.Contains(whole, StringComparison.Ordinal))
        {
            score = 20;
        }
        else
        {
            return 0;
        }

        // A page answers its own name before anything on it does.
        return entry.Entry.Kind == SearchKind.Page ? score + 5 : score;
    }

    private static bool Begins(string[] words, string typed) =>
        words.Any(word => word.StartsWith(typed, StringComparison.Ordinal));

    /// <summary>Text broken into lowercase words.</summary>
    internal static string[] Words(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed class Indexed(SearchEntry entry, int order)
    {
        public SearchEntry Entry { get; } = entry;

        public int Order { get; } = order;

        public string Title { get; } = string.Join(' ', Words(entry.Title));

        public string[] TitleWords { get; } = Words(entry.Title);

        public string[] TermWords { get; } = Words(string.Concat(entry.Terms, " ", entry.Where));
    }
}
