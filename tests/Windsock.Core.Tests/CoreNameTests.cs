using Windsock.Core.Hardware;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class CoreNameTests
{
    [Theory]
    [InlineData("0,0", 0, 0)]
    [InlineData("0,5", 0, 5)]
    [InlineData("0,15", 0, 15)]
    [InlineData("1,3", 1, 3)]
    public void AProcessorInstance_NamesItsGroupAndIndex(string instance, int group, int index)
    {
        (int Group, int Index) core = Assert.NotNull(CoreName.Parse(instance));

        Assert.Equal(group, core.Group);
        Assert.Equal(index, core.Index);
    }

    [Fact]
    public void AMachineWithSeveralGroups_KeepsItsCoresApart()
    {
        Assert.NotEqual(CoreName.Parse("0,0"), CoreName.Parse("1,0"));
    }

    [Theory]
    [InlineData("_Total")]
    [InlineData("0,_Total")]
    [InlineData("")]
    [InlineData("Total")]
    public void ATotal_IsNotACore(string instance) => Assert.Null(CoreName.Parse(instance));

    [Fact]
    public void AnInstanceWithNoGroup_IsReadAsTheFirstGroup()
    {
        // The older counter set names its instances by index alone.
        (int Group, int Index) core = Assert.NotNull(CoreName.Parse("7"));

        Assert.Equal(0, core.Group);
        Assert.Equal(7, core.Index);
    }
}
