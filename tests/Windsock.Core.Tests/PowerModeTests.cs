using Windsock.Core.Power;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>The effective power mode, as powersetting.h numbers it and Settings names it.</summary>
public sealed class PowerModeTests
{
    /// <summary>EFFECTIVE_POWER_MODE's values, in the header's order.</summary>
    [Fact]
    public void Modes_AreTheHeadersValues()
    {
        Assert.Equal(0, (int)PowerMode.Saver);
        Assert.Equal(1, (int)PowerMode.Efficiency);
        Assert.Equal(2, (int)PowerMode.Balanced);
        Assert.Equal(3, (int)PowerMode.HighPerformance);
        Assert.Equal(4, (int)PowerMode.MaxPerformance);
        Assert.Equal(5, (int)PowerMode.GameMode);
        Assert.Equal(6, (int)PowerMode.MixedReality);
    }

    /// <summary>The slider's three positions in Windows 11's words.</summary>
    [Theory]
    [InlineData(PowerMode.Efficiency, "Best power efficiency")]
    [InlineData(PowerMode.Balanced, "Balanced")]
    [InlineData(PowerMode.MaxPerformance, "Best performance")]
    [InlineData(PowerMode.GameMode, "Game mode")]
    public void Describe_UsesSettingsWords(PowerMode mode, string expected)
    {
        Assert.Equal(expected, PowerModeNames.Describe(mode, 26200));
    }

    /// <summary>Battery saver became energy saver in Windows 11 24H2.</summary>
    [Fact]
    public void Saver_IsNamedForTheBuild()
    {
        Assert.Equal("Battery saver", PowerModeNames.Describe(PowerMode.Saver, 22631));
        Assert.Equal("Energy saver", PowerModeNames.Describe(PowerMode.Saver, 26100));
    }

    /// <summary>
    /// Registration works unelevated, and Windows answers with the mode at
    /// once (measured on the development machine: Best performance, within
    /// two seconds). A server build may never answer, so only a mode that does
    /// arrive is checked.
    /// </summary>
    [Fact]
    public async Task Watch_RegistersAndLetsGo()
    {
        PowerModeWatch? watch = PowerModeWatch.Start();

        if (watch is null)
        {
            return;
        }

        for (int wait = 0; wait < 30 && watch.Current is null; wait++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        if (watch.Current is { } mode)
        {
            Assert.True(Enum.IsDefined(mode));
        }

        watch.Dispose();
        watch.Dispose();
    }
}
