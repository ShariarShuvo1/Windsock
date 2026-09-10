using Windsock.Core.Hardware;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class GpuEngineNameTests
{
    [Fact]
    public void AnEngineInstance_YieldsWhoWhichAndWhat()
    {
        GpuEngine engine = Assert.NotNull(
            GpuEngineName.Parse("pid_1916_luid_0x00000000_0x0000dcdd_phys_0_eng_0_engtype_3d"));

        Assert.Equal(1916, engine.ProcessId);
        Assert.Equal("0x00000000_0x0000dcdd", engine.Luid);
        Assert.Equal("3d", engine.Engine);
    }

    [Fact]
    public void ASecondAdapter_IsToldFromTheFirst()
    {
        GpuEngine built = Assert.NotNull(
            GpuEngineName.Parse("pid_88_luid_0x00000000_0x0000f042_phys_0_eng_0_engtype_3d"));

        GpuEngine discrete = Assert.NotNull(
            GpuEngineName.Parse("pid_88_luid_0x00000000_0x0000dcdd_phys_0_eng_0_engtype_3d"));

        Assert.NotEqual(built.Luid, discrete.Luid);
    }

    [Fact]
    public void ASecondPhysicalEngineOfTheSameKind_StillNamesItsAdapter()
    {
        GpuEngine engine = Assert.NotNull(
            GpuEngineName.Parse("pid_4_luid_0x00000000_0x0000dcdd_phys_1_eng_3_engtype_copy"));

        Assert.Equal("0x00000000_0x0000dcdd", engine.Luid);
        Assert.Equal("copy", engine.Engine);
    }

    [Theory]
    [InlineData("")]
    [InlineData("_Total")]
    [InlineData("luid_0x00000000_0x0000dcdd_phys_0")]
    [InlineData("pid_notanumber_luid_0x0_0x1_phys_0_eng_0_engtype_3d")]
    [InlineData("pid_12_nothing_like_it")]
    public void AnythingElse_IsNotAnEngine(string instance) =>
        Assert.Null(GpuEngineName.Parse(instance));

    [Fact]
    public void AMemoryInstance_YieldsItsAdapter()
    {
        Assert.Equal(
            "0x00000000_0x0000dcdd",
            GpuEngineName.Adapter("luid_0x00000000_0x0000dcdd_phys_0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("_Total")]
    [InlineData("pid_4_luid_0x0_0x1_phys_0_eng_0_engtype_3d")]
    public void AnythingElse_NamesNoAdapter(string instance) =>
        Assert.Equal(string.Empty, GpuEngineName.Adapter(instance));

    [Theory]
    [InlineData("3d", "3D")]
    [InlineData("copy", "Copy")]
    [InlineData("videodecode", "Video decode")]
    [InlineData("VideoDecode", "Video decode")]
    [InlineData("VideoEncode", "Video encode")]
    [InlineData("compute", "Compute")]
    public void AKnownEngine_IsNamedTheSameWhicheverWayWindowsSpeltIt(string kind, string expected)
    {
        Assert.Equal(expected, GpuEngineName.Describe(kind));
    }

    [Theory]
    [InlineData("NeuralProcessing", "Neural processing")]
    [InlineData("raytrace", "Raytrace")]
    public void AnUnfamiliarEngine_IsShownRatherThanHidden(string kind, string expected)
    {
        Assert.Equal(expected, GpuEngineName.Describe(kind));
    }
}
