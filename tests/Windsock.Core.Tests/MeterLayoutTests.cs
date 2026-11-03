using Windsock.Core.Docking;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// How the readings are laid out to fit the bar they are on.
/// </summary>
/// <remarks>
/// Line heights are Segoe UI's, measured through WPF on 2026-10-05: 14.63 at
/// 11 px, 17.29 at 13, 19.95 at 15 and 22.61 at 17 (1.33 times the size). The
/// room across a bar is its thickness less a 3-pixel inset each side: 42 on
/// the measured 48-pixel Windows 11 bar, 26 on a 32-pixel Small bar, whose real
/// height Microsoft has not published.
/// </remarks>
public sealed class MeterLayoutTests
{
    private const double Normal = 17.29;
    private const double Large = 19.95;
    private const double Huge = 22.61;

    [Theory]
    [InlineData(42, Normal, MeterArrangement.AsSet)]
    [InlineData(42, Large, MeterArrangement.AsSet)]
    [InlineData(42, Huge, MeterArrangement.OneLine)]
    [InlineData(26, Normal, MeterArrangement.OneLine)]
    [InlineData(34.58, Normal, MeterArrangement.AsSet)]
    public void TwoRows_StayTwoRowsOnlyWhereTheyFit(double room, double line, MeterArrangement expected)
    {
        Assert.Equal(expected, MeterLayout.Choose(TaskbarEdge.Bottom, rows: 2, columns: 1, room, line));
        Assert.Equal(expected, MeterLayout.Choose(TaskbarEdge.Top, rows: 2, columns: 1, room, line));
    }

    [Fact]
    public void OneRow_IsAlreadyOneLine()
    {
        Assert.Equal(MeterArrangement.AsSet, MeterLayout.Choose(TaskbarEdge.Bottom, rows: 1, columns: 3, 26, Huge));
    }

    [Theory]
    [InlineData(TaskbarEdge.Left)]
    [InlineData(TaskbarEdge.Right)]
    public void DownASide_ReadingsAreStacked(TaskbarEdge edge)
    {
        Assert.Equal(MeterArrangement.Stacked, MeterLayout.Choose(edge, rows: 1, columns: 3, 56, Normal));
        Assert.Equal(MeterArrangement.Stacked, MeterLayout.Choose(edge, rows: 2, columns: 1, 56, Normal));
    }

    [Fact]
    public void ASinglePlace_NeedsNoRearranging()
    {
        Assert.Equal(MeterArrangement.AsSet, MeterLayout.Choose(TaskbarEdge.Left, rows: 1, columns: 1, 56, Normal));
    }

    /// <summary>
    /// One reading with an empty place beside it is still two columns wide,
    /// which a side bar has no room for: it is stacked, and stacking drops the
    /// empty place. (Found by review: it used to count readings, not places,
    /// and kept the two columns.)
    /// </summary>
    [Fact]
    public void OneReadingAndAnEmptyPlace_AreStackedDownASide()
    {
        Assert.Equal(MeterArrangement.Stacked, MeterLayout.Choose(TaskbarEdge.Right, rows: 1, columns: 2, 56, Normal));
        Assert.Equal([0], MeterLayout.Order(MeterArrangement.Stacked, 1, 2, [true, false]));
    }

    [Fact]
    public void WithNoBarUnderIt_TheMeterIsAsSet()
    {
        Assert.Equal(MeterArrangement.AsSet, MeterLayout.Choose(null, rows: 2, columns: 2, 10, Huge));
    }

    [Theory]
    [InlineData(MeterArrangement.AsSet, 2, 3, 6, 2, 3)]
    [InlineData(MeterArrangement.OneLine, 2, 3, 5, 1, 5)]
    [InlineData(MeterArrangement.Stacked, 2, 3, 5, 5, 1)]
    [InlineData(MeterArrangement.Stacked, 2, 1, 0, 1, 1)]
    public void TheShape_FollowsTheArrangement(
        MeterArrangement arrangement, int rows, int columns, int places, int expectedRows, int expectedColumns)
    {
        Assert.Equal((expectedRows, expectedColumns), MeterLayout.Shape(arrangement, rows, columns, places));
    }

    [Fact]
    public void AsSet_EveryPlaceKeepsItsPlace()
    {
        Assert.Equal([0, 1, 2, 3], MeterLayout.Order(MeterArrangement.AsSet, 2, 2, [true, false, true, true]));
    }

    /// <summary>
    /// The default meter, download over upload, on one line: download first.
    /// </summary>
    [Fact]
    public void OnOneLine_TheDefaultMeterReadsDownloadThenUpload()
    {
        Assert.Equal([0, 1], MeterLayout.Order(MeterArrangement.OneLine, 2, 1, [true, true]));
    }

    /// <summary>
    /// Built as two columns - network over network, processor over memory -
    /// the columns stay pairs: column by column, not row by row.
    /// </summary>
    [Theory]
    [InlineData(MeterArrangement.OneLine)]
    [InlineData(MeterArrangement.Stacked)]
    public void Rearranged_ColumnsStayTogether(MeterArrangement arrangement)
    {
        Assert.Equal([0, 2, 1, 3], MeterLayout.Order(arrangement, 2, 2, [true, true, true, true]));
    }

    [Fact]
    public void Rearranged_EmptyPlacesAreLeftOut()
    {
        // Row by row the places are 0 1 2 over 3 4 5; column by column, the filled ones are 0, 3 and 4.
        Assert.Equal([0, 3, 4], MeterLayout.Order(MeterArrangement.OneLine, 2, 3, [true, false, false, true, true, false]));
    }

    [Fact]
    public void Rearranged_ANothingMeterStillHasOnePlace()
    {
        Assert.Equal([0], MeterLayout.Order(MeterArrangement.Stacked, 2, 1, [false, false]));
    }

    [Theory]
    [InlineData(50, 56, 1.0)]
    [InlineData(56, 56, 1.0)]
    [InlineData(60, 56, 0.93)]
    [InlineData(80, 56, 0.7)]
    [InlineData(200, 56, MeterLayout.SmallestShrink)]
    [InlineData(double.NaN, 56, 1.0)]
    [InlineData(60, 0, 1.0)]
    public void Shrinking_FitsTheRoomButNeverPastLegible(double wanted, double room, double expected)
    {
        Assert.Equal(expected, MeterLayout.Shrink(wanted, room), 3);
    }
}
