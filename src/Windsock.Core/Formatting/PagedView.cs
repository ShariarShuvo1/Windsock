namespace Windsock.Core.Formatting;

/// <summary>
/// A long list shown a page at a time, filtered and sorted as a whole.
/// </summary>
/// <remarks>
/// <para>
/// A table built of every row of a long period grows without end: History by
/// the hour holds nearly nine thousand rows a year, each a real row, since the
/// table sits in the page's own scroller and cannot recycle them. The list
/// itself costs next to nothing; the rows on screen are what cost. So the whole
/// list is kept here, and the table is given one page of it.
/// </para>
/// <para>
/// Filtering and sorting apply to the whole list, never to the page on screen:
/// sorting page three by size would otherwise show page three's hundred rows
/// in order and call that sorted. The sort is stable, so rows equal under it
/// keep the order the list came in.
/// </para>
/// </remarks>
public sealed class PagedView<T>
{
    private IReadOnlyList<T> _source = [];
    private List<T> _shown = [];
    private Func<T, bool>? _keep;
    private Comparison<T>? _order;
    private int _page = 1;
    private int _pageSize;

    public PagedView(int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _pageSize = pageSize;
    }

    /// <summary>Raised whenever the page on screen may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Everything that passes the filter, in order.</summary>
    public IReadOnlyList<T> Matching => _shown;

    /// <summary>How many items the list holds before filtering.</summary>
    public int SourceCount => _source.Count;

    /// <summary>Where the page on screen stands.</summary>
    public PageWindow Window => PageWindow.Of(_shown.Count, _pageSize, _page);

    /// <summary>The items on the page on screen.</summary>
    public IReadOnlyList<T> Current
    {
        get
        {
            PageWindow window = Window;
            return _shown.GetRange(window.Start, window.Count);
        }
    }

    /// <summary>How many items a page holds. Changing it keeps the first item on screen on screen.</summary>
    public int PageSize
    {
        get => _pageSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);

            if (value == _pageSize)
            {
                return;
            }

            int page = PageWindow.KeepingFirst(Window, value);
            _pageSize = value;
            _page = page;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gives the view a new list. The page stays where it was, moved back onto
    /// the last page if the list got shorter, unless <paramref name="toFirstPage"/>.
    /// </summary>
    public void SetSource(IReadOnlyList<T> items, bool toFirstPage)
    {
        ArgumentNullException.ThrowIfNull(items);

        _source = items;

        if (toFirstPage)
        {
            _page = 1;
        }

        Refresh();
    }

    /// <summary>
    /// Gives the view a new list, filter and sort in one go, working the list
    /// out once. The page stays where it was unless <paramref name="toFirstPage"/>.
    /// </summary>
    public void Reset(IReadOnlyList<T> items, Func<T, bool>? keep, Comparison<T>? order, bool toFirstPage)
    {
        ArgumentNullException.ThrowIfNull(items);

        _source = items;
        _keep = keep;
        _order = order;

        if (toFirstPage)
        {
            _page = 1;
        }

        Refresh();
    }

    /// <summary>Filters the whole list, and goes back to the first page.</summary>
    public void Filter(Func<T, bool>? keep)
    {
        _keep = keep;
        _page = 1;
        Refresh();
    }

    /// <summary>Sorts the whole list, or puts it back in source order, and goes back to the first page.</summary>
    public void Sort(Comparison<T>? order)
    {
        _order = order;
        _page = 1;
        Refresh();
    }

    /// <summary>Goes to a page, or the nearest that exists.</summary>
    public void GoTo(int page)
    {
        int shown = PageWindow.Of(_shown.Count, _pageSize, page).Page;

        if (shown == _page)
        {
            return;
        }

        _page = shown;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Goes to the page holding the first item that matches, and says where
    /// in that page it is; -1 when no item on any page matches.
    /// </summary>
    public int Reveal(Func<T, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);

        int index = _shown.FindIndex(item => match(item));

        if (index < 0)
        {
            return -1;
        }

        GoTo(PageWindow.PageOf(index, _pageSize));
        return index - Window.Start;
    }

    private void Refresh()
    {
        List<T> shown = new(_source.Count);

        foreach (T item in _source)
        {
            if (_keep is null || _keep(item))
            {
                shown.Add(item);
            }
        }

        if (_order is { } order)
        {
            // List.Sort is not stable; the index settles ties the way the list came.
            List<(T Item, int At)> ranked = new(shown.Count);

            for (int at = 0; at < shown.Count; at++)
            {
                ranked.Add((shown[at], at));
            }

            ranked.Sort((left, right) =>
            {
                int compared = order(left.Item, right.Item);
                return compared != 0 ? compared : left.At.CompareTo(right.At);
            });

            shown = ranked.ConvertAll(entry => entry.Item);
        }

        _shown = shown;
        _page = PageWindow.Of(_shown.Count, _pageSize, _page).Page;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
