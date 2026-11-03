using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Windsock.Core.Appearance;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The colours in both palettes, checked for people with each kind of colour
/// blindness rather than trusted, straight from the source files.
/// </summary>
/// <remarks>
/// <para>
/// Two thresholds, in CIEDE2000 under Machado's matrices at full severity
/// (accessibility.md has the method and the wider table):
/// </para>
/// <list type="bullet">
/// <item>Colours that share a chart, where colour is the only thing telling two
/// thin lines or bars apart, must be at least <see cref="SharedChart"/> apart
/// for every kind of colour vision.</item>
/// <item>The good and the hot state are never shown by colour alone - every
/// one comes with its words ("Online", "Outage") - and must be at least
/// <see cref="Labelled"/> apart.</item>
/// </list>
/// <para>
/// The five part hues are identity colours: each part has a page, a lane and a
/// legend entry of its own and is always named, so they are held apart only
/// for typical vision. Under dichromacy several pairs come close (network and
/// memory are 1.3 apart for a deuteranope), which is why they never meet on a
/// chart without their names.
/// </para>
/// </remarks>
public sealed partial class PaletteColourTests
{
    private const double SharedChart = 20;
    private const double Labelled = 10;

    public static TheoryData<string, string, string> ChartPairs => new()
    {
        // The network chart, History's bars and every table: down against up.
        { "Dark", "DownloadColor", "UploadColor" },
        { "Light", "DownloadColor", "UploadColor" },

        // Connection › Status's response time: the router's line, the
        // internet's line and the outage shading behind them.
        { "Dark", "RouterColor", "DownloadColor" },
        { "Light", "RouterColor", "DownloadColor" },
        { "Dark", "RouterColor", "HotBrush" },
        { "Light", "RouterColor", "HotBrush" },
        { "Dark", "DownloadColor", "HotBrush" },
        { "Light", "DownloadColor", "HotBrush" },

        // History's machine lanes: outages drawn across the ping lane.
        { "Dark", "NetworkColor", "HotBrush" },
        { "Light", "NetworkColor", "HotBrush" },

        // Adapters and Wi-Fi's channel crowding: the quietest channel among the others.
        { "Dark", "MapBusyBrush", "NetworkColor" },
        { "Light", "MapBusyBrush", "NetworkColor" },
    };

    [Theory]
    [MemberData(nameof(ChartPairs))]
    public void ColoursThatShareAChart_StayApartForEveryKindOfColourVision(string palette, string first, string second)
    {
        Dictionary<string, Rgb> colours = Colours(palette);

        foreach (ColourVisionKind kind in Enum.GetValues<ColourVisionKind>())
        {
            double apart = ColourVision.Difference(colours[first], colours[second], kind);

            Assert.True(
                apart >= SharedChart,
                $"{palette}: {first} and {second} are {apart:F1} apart for {kind} vision; a shared chart needs {SharedChart}.");
        }
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void GoodAndHot_StayApartForEveryKindOfColourVision(string palette)
    {
        Dictionary<string, Rgb> colours = Colours(palette);

        double apart = ColourVision.LeastDifference(colours["GoodBrush"], colours["HotBrush"]);

        Assert.True(apart >= Labelled, $"{palette}: good and hot are only {apart:F1} apart for someone.");
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void ThePartHues_AreDistinctForTypicalVision(string palette)
    {
        Dictionary<string, Rgb> colours = Colours(palette);
        string[] parts = ["NetworkColor", "ProcessorColor", "GraphicsColor", "MemoryColor", "StorageColor", "BatteryColor"];

        foreach (string first in parts)
        {
            foreach (string second in parts.Where(each => string.CompareOrdinal(each, first) > 0))
            {
                double apart = ColourVision.Difference(colours[first], colours[second]);

                Assert.True(apart >= Labelled, $"{palette}: {first} and {second} are only {apart:F1} apart.");
            }
        }
    }

    /// <summary>
    /// Every series and state colour can be seen against the page and a band:
    /// WCAG's 3:1 for graphical objects.
    /// </summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void SeriesAndStates_StandOutFromThePageAndABand(string palette)
    {
        Dictionary<string, Rgb> colours = Colours(palette);
        string[] drawn = ["DownloadColor", "UploadColor", "RouterColor", "HotBrush", "GoodBrush", "NetworkColor", "ProcessorColor", "GraphicsColor", "MemoryColor", "StorageColor", "BatteryColor"];

        foreach (string key in drawn)
        {
            foreach (string ground in new[] { "SurfaceBrush", "SectionBrush" })
            {
                double contrast = ColourVision.Contrast(colours[key], colours[ground]);

                Assert.True(contrast >= 3, $"{palette}: {key} on {ground} is {contrast:F2}:1.");
            }
        }
    }

    /// <summary>Every opaque colour a palette names, brushes included, by key.</summary>
    private static Dictionary<string, Rgb> Colours(string palette)
    {
        string path = Path.Combine(RepositoryRoot(), "src", "Windsock.App", "Styles", $"Palette.{palette}.xaml");
        string text = File.ReadAllText(path);
        Dictionary<string, Rgb> colours = new(StringComparer.Ordinal);

        foreach (Match match in ColourPattern().Matches(text))
        {
            colours[match.Groups[1].Value] = Rgb.Parse(match.Groups[2].Value);
        }

        foreach (Match match in BrushPattern().Matches(text))
        {
            colours[match.Groups[1].Value] = Rgb.Parse(match.Groups[2].Value);
        }

        return colours;
    }

    private static string RepositoryRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    [GeneratedRegex("<Color x:Key=\"(\\w+)\">(#[0-9A-Fa-f]{6,8})</Color>")]
    private static partial Regex ColourPattern();

    [GeneratedRegex("<SolidColorBrush x:Key=\"(\\w+)\" Color=\"(#[0-9A-Fa-f]{6,8})\"")]
    private static partial Regex BrushPattern();
}
