using Windsock.Core.Appearance;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class ColourVisionTests
{
    /// <summary>
    /// Worked pairs from Sharma, Wu and Dalal (2005), "The CIEDE2000
    /// color-difference formula: implementation notes, supplementary test data,
    /// and mathematical observations", table 1 - pairs 1, 7, 13, 17, 18, 25 and 34.
    /// </summary>
    [Theory]
    [InlineData(50.0000, 2.6772, -79.7751, 50.0000, 0.0000, -82.7485, 2.0425)]
    [InlineData(50.0000, 0.0000, 0.0000, 50.0000, -1.0000, 2.0000, 2.3669)]
    [InlineData(50.0000, 2.4900, -0.0010, 50.0000, -2.4900, 0.0009, 7.1792)]
    [InlineData(50.0000, 2.5000, 0.0000, 73.0000, 25.0000, -18.0000, 27.1492)]
    [InlineData(50.0000, 2.5000, 0.0000, 61.0000, -5.0000, 29.0000, 22.8977)]
    [InlineData(60.2574, -34.0099, 36.2677, 60.4626, -34.1751, 39.4387, 1.2644)]
    [InlineData(2.0776, 0.0795, -1.1350, 0.9033, -0.0636, -0.5514, 0.9082)]
    public void Ciede2000_MatchesThePublishedPairs(double l1, double a1, double b1, double l2, double a2, double b2, double expected)
    {
        double found = ColourVision.Difference(new Lab(l1, a1, b1), new Lab(l2, a2, b2));

        Assert.Equal(expected, found, 4);
        Assert.Equal(expected, ColourVision.Difference(new Lab(l2, a2, b2), new Lab(l1, a1, b1)), 4);
    }

    /// <summary>
    /// Each primary, simulated, is the column of Machado's matrix for it
    /// (severity 1.0), clamped to what a screen can show.
    /// </summary>
    [Theory]
    [InlineData(ColourVisionKind.Protan, 0.152286, 0.114503, 0.0)]
    [InlineData(ColourVisionKind.Deutan, 0.367322, 0.280085, 0.0)]
    [InlineData(ColourVisionKind.Tritan, 1.0, 0.0, 0.004733)]
    public void Red_IsTheMatrixFirstColumn(ColourVisionKind kind, double r, double g, double b)
    {
        (double R, double G, double B) seen = ColourVision.Simulate(new Rgb(255, 0, 0), kind);

        Assert.Equal(r, seen.R, 6);
        Assert.Equal(g, seen.G, 6);
        Assert.Equal(b, seen.B, 6);
    }

    [Theory]
    [InlineData(ColourVisionKind.Protan, 1.0, 0.786281, 0.0)]
    [InlineData(ColourVisionKind.Deutan, 0.860646, 0.672501, 0.042940)]
    [InlineData(ColourVisionKind.Tritan, 0.0, 0.930809, 0.691367)]
    public void Green_IsTheMatrixSecondColumn(ColourVisionKind kind, double r, double g, double b)
    {
        (double R, double G, double B) seen = ColourVision.Simulate(new Rgb(0, 255, 0), kind);

        Assert.Equal(r, seen.R, 6);
        Assert.Equal(g, seen.G, 6);
        Assert.Equal(b, seen.B, 6);
    }

    [Fact]
    public void White_StaysWhiteForEveryone()
    {
        foreach (ColourVisionKind kind in Enum.GetValues<ColourVisionKind>())
        {
            Lab white = ColourVision.ToLab(ColourVision.Simulate(new Rgb(255, 255, 255), kind));

            Assert.Equal(100, white.L, 1);
        }
    }

    [Fact]
    public void ClassicGreenAndRed_FailForDeuteranopes()
    {
        // Tol's green #228833 against red #EE6677: obvious to most, 15.7 apart
        // for a deuteranope - the pair every chart guide warns about.
        Rgb green = Rgb.Parse("#228833");
        Rgb red = Rgb.Parse("#EE6677");

        Assert.True(ColourVision.Difference(green, red) > 60);
        Assert.InRange(ColourVision.Difference(green, red, ColourVisionKind.Deutan), 15, 16.5);
    }

    [Fact]
    public void Contrast_RunsFromOneToTwentyOne()
    {
        Assert.Equal(21, ColourVision.Contrast(new Rgb(0, 0, 0), new Rgb(255, 255, 255)), 6);
        Assert.Equal(1, ColourVision.Contrast(Rgb.Parse("#0288D1"), Rgb.Parse("#0288D1")), 6);
        Assert.Equal(3.50, ColourVision.Contrast(Rgb.Parse("#0288D1"), Rgb.Parse("#F2F4F7")), 2);
    }

    [Fact]
    public void FirstReadable_TakesTheFirstThatStandsOut()
    {
        Rgb ground = Rgb.Parse("#15181C");
        Rgb dim = Rgb.Parse("#003E92");
        Rgb accent = Rgb.Parse("#0078D4");
        Rgb light = Rgb.Parse("#99EBFF");

        Assert.Equal(light, ColourVision.FirstReadable([dim, accent, light], ground, 4.5));
        Assert.Equal(light, ColourVision.FirstReadable([dim, light], ground, 50));
    }

    [Theory]
    [InlineData("#FF2DC1FC", 0x2D, 0xC1, 0xFC)]
    [InlineData("#2dc1fc", 0x2D, 0xC1, 0xFC)]
    public void Parse_ReadsPaletteColours(string text, byte r, byte g, byte b) =>
        Assert.Equal(new Rgb(r, g, b), Rgb.Parse(text));

    [Fact]
    public void Parse_RefusesWhatIsNotAColour() =>
        Assert.Throws<FormatException>(() => Rgb.Parse("#12345"));
}
