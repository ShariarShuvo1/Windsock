using Windsock.Core.Formatting;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class PageWindowTests
{
    [Theory]
    [InlineData(0, 100, 1, 1, 1, 0, 0)]
    [InlineData(1, 100, 1, 1, 1, 0, 1)]
    [InlineData(100, 100, 1, 1, 1, 0, 100)]
    [InlineData(101, 100, 2, 2, 2, 100, 1)]
    [InlineData(8_760, 100, 88, 88, 88, 8_700, 60)]
    [InlineData(8_760, 100, 3, 3, 88, 200, 100)]
    public void APage_HoldsTheRightItems(int total, int size, int asked, int page, int pages, int start, int count)
    {
        PageWindow window = PageWindow.Of(total, size, asked);

        Assert.Equal(page, window.Page);
        Assert.Equal(pages, window.Pages);
        Assert.Equal(start, window.Start);
        Assert.Equal(count, window.Count);
        Assert.Equal(total, window.Total);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(999)]
    public void APageThatDoesNotExist_IsTheNearestThatDoes(int asked)
    {
        PageWindow window = PageWindow.Of(250, 100, asked);

        Assert.Equal(asked < 1 ? 1 : 3, window.Page);
        Assert.Equal(asked < 1 ? 0 : 200, window.Start);
    }

    [Fact]
    public void DeletingTheLastItemOfTheLastPage_StepsBack()
    {
        PageWindow before = PageWindow.Of(201, 100, 3);
        Assert.Equal(1, before.Count);

        PageWindow after = PageWindow.Of(200, 100, before.Page);

        Assert.Equal(2, after.Page);
        Assert.Equal(100, after.Count);
        Assert.False(after.HasNext);
        Assert.True(after.HasPrevious);
    }

    [Fact]
    public void ChangingTheSize_KeepsTheFirstItemOnScreen()
    {
        // Rows 501-600 on screen; at 250 a page, row 501 is on page 3.
        PageWindow before = PageWindow.Of(8_760, 100, 6);

        Assert.Equal(3, PageWindow.KeepingFirst(before, 250));
        Assert.Equal(1, PageWindow.KeepingFirst(PageWindow.Of(0, 100, 1), 50));
    }

    [Fact]
    public void SortingAndFiltering_ApplyToTheWholeList_NotThePage()
    {
        int[] items = [.. Enumerable.Range(1, 250)];
        PagedView<int> view = new(100);
        view.SetSource(items, toFirstPage: true);
        view.GoTo(3);

        Assert.Equal(201, view.Current[0]);

        // Largest first: page one holds 250 down to 151, not page three's rows reversed.
        view.Sort((left, right) => right.CompareTo(left));
        Assert.Equal(1, view.Window.Page);
        Assert.Equal(250, view.Current[0]);
        Assert.Equal(151, view.Current[^1]);

        view.Filter(item => item % 10 == 0);
        Assert.Equal(25, view.Window.Total);
        Assert.Equal(1, view.Window.Pages);
        Assert.Equal(250, view.Current[0]);
        Assert.Equal(250, view.SourceCount);
    }

    [Fact]
    public void TheSort_IsStable()
    {
        (string Name, int Size)[] items = [("a", 2), ("b", 1), ("c", 2), ("d", 1), ("e", 2)];
        PagedView<(string Name, int Size)> view = new(10);
        view.SetSource(items, toFirstPage: true);

        view.Sort((left, right) => left.Size.CompareTo(right.Size));

        Assert.Equal("bdace", string.Concat(view.Current.Select(item => item.Name)));
    }

    [Fact]
    public void NewItems_LeaveThePageWhereItWas_UnlessAskedOtherwise()
    {
        PagedView<int> view = new(100);
        view.SetSource([.. Enumerable.Range(0, 1_000)], toFirstPage: true);
        view.GoTo(5);

        view.SetSource([.. Enumerable.Range(0, 1_005)], toFirstPage: false);
        Assert.Equal(5, view.Window.Page);

        // Shorter than the page reached: the last page.
        view.SetSource([.. Enumerable.Range(0, 250)], toFirstPage: false);
        Assert.Equal(3, view.Window.Page);

        view.SetSource([.. Enumerable.Range(0, 250)], toFirstPage: true);
        Assert.Equal(1, view.Window.Page);
    }

    [Fact]
    public void Revealing_GoesToThePageHoldingTheItem()
    {
        PagedView<int> view = new(100);
        int changes = 0;
        view.Changed += (_, _) => changes++;
        view.SetSource([.. Enumerable.Range(0, 1_000)], toFirstPage: true);

        int at = view.Reveal(item => item == 742);

        Assert.Equal(8, view.Window.Page);
        Assert.Equal(42, at);
        Assert.Equal(742, view.Current[at]);
        Assert.Equal(-1, view.Reveal(item => item == 5_000));
        Assert.Equal(8, view.Window.Page);
        Assert.True(changes >= 2);

        // A bigger page keeps row 701 on screen.
        view.PageSize = 250;
        Assert.Equal(3, view.Window.Page);
        Assert.Contains(742, view.Current);
    }

    [Theory]
    [InlineData(0, 100, 1)]
    [InlineData(99, 100, 1)]
    [InlineData(100, 100, 2)]
    [InlineData(8_759, 100, 88)]
    public void AnItem_IsOnItsPage(int index, int size, int page) =>
        Assert.Equal(page, PageWindow.PageOf(index, size));
}
