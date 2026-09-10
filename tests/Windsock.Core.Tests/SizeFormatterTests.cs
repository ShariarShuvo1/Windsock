using Windsock.Core.Formatting;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class SizeFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(-1, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(15 * 1024, "15.0 KB")]
    [InlineData(150 * 1024, "150 KB")]
    [InlineData(1024L * 1024, "1.00 MB")]
    [InlineData(1024L * 1024 * 1024, "1.00 GB")]
    [InlineData(1024L * 1024 * 1024 * 1024, "1.00 TB")]
    public void ScalesToThreeSignificantFigures(long bytes, string expected)
    {
        Assert.Equal(expected, SizeFormatter.Format(bytes));
    }

    [Fact]
    public void WholeBytes_CarryNoDecimals()
    {
        // A count of bytes is a count, not a measurement.
        Assert.Equal("1023 B", SizeFormatter.Format(1023));
    }

    [Fact]
    public void HugeValues_StopAtTheLargestUnitRatherThanOverflowing()
    {
        string text = SizeFormatter.Format(long.MaxValue);

        Assert.EndsWith("PB", text, StringComparison.Ordinal);
    }
}
