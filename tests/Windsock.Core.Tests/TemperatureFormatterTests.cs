using Windsock.Core.Formatting;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>Temperatures in the scale the user chose.</summary>
public sealed class TemperatureFormatterTests
{
    /// <summary>The fixed points of both scales.</summary>
    [Theory]
    [InlineData(0, 32)]
    [InlineData(100, 212)]
    [InlineData(-40, -40)]
    [InlineData(37, 98.6)]
    public void Fahrenheit_IsNineFifthsPlusThirtyTwo(double celsius, double fahrenheit)
    {
        Assert.Equal(fahrenheit, TemperatureFormatter.Convert(celsius, TemperatureUnit.Fahrenheit), 9);
    }

    /// <summary>Celsius is left alone.</summary>
    [Fact]
    public void Celsius_IsUnchanged()
    {
        Assert.Equal(64.2, TemperatureFormatter.Convert(64.2, TemperatureUnit.Celsius));
    }

    /// <summary>A difference has no offset: ten degrees warmer is eighteen Fahrenheit warmer.</summary>
    [Fact]
    public void Difference_HasNoOffset()
    {
        Assert.Equal(18, TemperatureFormatter.ConvertDifference(10, TemperatureUnit.Fahrenheit), 9);
        Assert.Equal(10, TemperatureFormatter.ConvertDifference(10, TemperatureUnit.Celsius));
    }

    /// <summary>The pages' form: a whole number with its unit.</summary>
    [Theory]
    [InlineData(64.2, TemperatureUnit.Celsius, "64°C")]
    [InlineData(64.2, TemperatureUnit.Fahrenheit, "148°F")]
    [InlineData(27.85, TemperatureUnit.Celsius, "28°C")]
    [InlineData(27.85, TemperatureUnit.Fahrenheit, "82°F")]
    [InlineData(100, TemperatureUnit.Fahrenheit, "212°F")]
    public void Format_IsAWholeNumberAndItsUnit(double celsius, TemperatureUnit unit, string expected)
    {
        Assert.Equal(expected, TemperatureFormatter.Format(celsius, unit));
    }

    /// <summary>Halves round away from zero, as a reader expects, not to the even neighbour.</summary>
    [Fact]
    public void Format_RoundsHalvesUp()
    {
        Assert.Equal("65°C", TemperatureFormatter.Format(64.5, TemperatureUnit.Celsius));
    }

    /// <summary>Nothing read is a dash, never a zero.</summary>
    [Fact]
    public void Format_NothingIsADash()
    {
        Assert.Equal("—", TemperatureFormatter.Format((double?)null, TemperatureUnit.Fahrenheit));
        Assert.Equal("—", TemperatureFormatter.Format((double?)double.NaN, TemperatureUnit.Celsius));
    }

    /// <summary>The meter's form: figure and unit apart.</summary>
    [Fact]
    public void Split_GivesFigureAndUnitApart()
    {
        Assert.Equal(("147", "°F"), TemperatureFormatter.Split(63.9, TemperatureUnit.Fahrenheit));
        Assert.Equal(("64", "°C"), TemperatureFormatter.Split(63.9, TemperatureUnit.Celsius));
    }

    /// <summary>A reading that rounds to zero is "0", never "-0".</summary>
    [Fact]
    public void Format_NeverWritesMinusZero()
    {
        Assert.Equal("0°C", TemperatureFormatter.Format(-0.3, TemperatureUnit.Celsius));
    }

    /// <summary>Celsius until the user says otherwise.</summary>
    [Fact]
    public void Setting_StartsInCelsius()
    {
        Assert.Equal(TemperatureUnit.Celsius, new WindsockSettings().Temperature);
    }

    /// <summary>The choice survives a save and a load, by name.</summary>
    [Fact]
    public void Setting_SurvivesTheFile()
    {
        string folder = Path.Combine(Path.GetTempPath(), "windsock-temperature-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "settings.json");

        try
        {
            SettingsStore store = new(path);
            store.Save(new WindsockSettings { Temperature = TemperatureUnit.Fahrenheit });

            Assert.Contains("\"Fahrenheit\"", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Equal(TemperatureUnit.Fahrenheit, store.Load().Temperature);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>An imported settings file brings its scale with it.</summary>
    [Fact]
    public void Setting_IsImported()
    {
        WindsockSettings here = new();
        here.CopyFrom(new WindsockSettings { Temperature = TemperatureUnit.Fahrenheit });

        Assert.Equal(TemperatureUnit.Fahrenheit, here.Temperature);
    }
}
