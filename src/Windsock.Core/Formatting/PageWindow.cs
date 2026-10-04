namespace Windsock.Core.Formatting;

/// <summary>
/// One page of a list: which page it is, how many there are, and which items
/// it holds. Pages are counted from one.
/// </summary>
/// <param name="Page">The page shown, from one; one for an empty list.</param>
/// <param name="Pages">How many pages there are; one for an empty list, so there is always a page to be on.</param>
/// <param name="Start">The index of the page's first item.</param>
/// <param name="Count">How many items the page holds.</param>
/// <param name="Total">How many items the whole list holds.</param>
public readonly record struct PageWindow(int Page, int Pages, int Start, int Count, int Total)
{
    /// <summary>Whether there is a page before this one.</summary>
    public bool HasPrevious => Page > 1;

    /// <summary>Whether there is a page after this one.</summary>
    public bool HasNext => Page < Pages;

    /// <summary>
    /// The page of a list asked for, moved onto the nearest page that exists:
    /// asking past the end gives the last page, so deleting the last item of
    /// the last page steps back rather than showing nothing.
    /// </summary>
    public static PageWindow Of(int total, int pageSize, int page)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        int pages = Math.Max(1, (int)(((long)total + pageSize - 1) / pageSize));
        int shown = Math.Clamp(page, 1, pages);
        int start = (int)Math.Min(total, (long)(shown - 1) * pageSize);

        return new PageWindow(shown, pages, start, Math.Min(pageSize, total - start), total);
    }

    /// <summary>The page an item at an index is on.</summary>
    public static int PageOf(int index, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        return (index / pageSize) + 1;
    }

    /// <summary>
    /// The page to show after the page size changes, so the first item that
    /// was on screen still is.
    /// </summary>
    public static int KeepingFirst(PageWindow before, int pageSize) =>
        before.Total == 0 ? 1 : PageOf(before.Start, pageSize);
}
