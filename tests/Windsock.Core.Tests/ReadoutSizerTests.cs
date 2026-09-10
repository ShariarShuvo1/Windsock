using Windsock.Core.Formatting;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class ReadoutSizerTests
{
    private const double Maximum = 40;

    [Fact]
    public void ShortText_UsesTheMaximumSize()
    {
        var sizer = new ReadoutSizer(Maximum);

        Assert.Equal(Maximum, sizer.Update("843.6".Length));
    }

    [Fact]
    public void LongerText_ShrinksImmediately()
    {
        var sizer = new ReadoutSizer(Maximum);

        double small = sizer.Update("11,534,336".Length);

        Assert.True(small < Maximum, $"expected a reduced size, got {small}");
    }

    [Fact]
    public void HoveringOnABoundary_DoesNotOscillate()
    {
        var sizer = new ReadoutSizer(Maximum);

        // 7 characters shrinks to the next tier down.
        double shrunk = sizer.Update(7);
        Assert.Equal(shrunk, sizer.Update(6));
        Assert.Equal(shrunk, sizer.Update(7));
        Assert.Equal(shrunk, sizer.Update(6));
    }

    [Fact]
    public void ClearlyShorterText_GrowsBack()
    {
        var sizer = new ReadoutSizer(Maximum);
        sizer.Update(7);

        // One character clear of the boundary is a real change, not jitter.
        Assert.Equal(Maximum, sizer.Update(5));
    }

    [Fact]
    public void Shrinking_IsMonotonicAcrossTiers()
    {
        var sizer = new ReadoutSizer(Maximum);

        double a = sizer.Update(6);
        double b = sizer.Update(8);
        double c = sizer.Update(11);
        double d = sizer.Update(14);

        Assert.True(a > b && b > c && c > d, $"expected decreasing sizes, got {a}, {b}, {c}, {d}");
    }

    [Fact]
    public void Current_MatchesTheLastUpdate()
    {
        var sizer = new ReadoutSizer(Maximum);
        double returned = sizer.Update(9);

        Assert.Equal(returned, sizer.Current);
    }
}
