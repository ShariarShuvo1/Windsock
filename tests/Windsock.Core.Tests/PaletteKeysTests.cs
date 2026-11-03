using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The light and dark palettes are swapped whole when the theme changes, so a
/// key only one of them defines leaves a brush unresolved in the other theme -
/// silently, until somebody switches. Read straight from the source files.
/// </summary>
public sealed partial class PaletteKeysTests
{
    [Fact]
    public void BothPalettes_DefineTheSameKeys()
    {
        string styles = Path.Combine(RepositoryRoot(), "src", "Windsock.App", "Styles");

        SortedSet<string> dark = Keys(Path.Combine(styles, "Palette.Dark.xaml"));
        SortedSet<string> light = Keys(Path.Combine(styles, "Palette.Light.xaml"));

        Assert.NotEmpty(dark);
        Assert.Equal(dark, light);
    }

    /// <summary>
    /// The palette used under a Windows contrast theme maps every key to a
    /// system colour. A key it lacks would keep the dark or light palette's own
    /// colour under a contrast theme, which is exactly what such a theme asks
    /// an app not to do.
    /// </summary>
    [Fact]
    public void TheContrastPalette_DefinesEveryKeyToo()
    {
        string styles = Path.Combine(RepositoryRoot(), "src", "Windsock.App", "Styles");

        SortedSet<string> dark = Keys(Path.Combine(styles, "Palette.Dark.xaml"));
        SortedSet<string> contrast = Keys(Path.Combine(styles, "Palette.Contrast.xaml"));

        // It may say more - WPF's own text and fill keys - but never less.
        Assert.Empty(dark.Except(contrast, StringComparer.Ordinal));
        Assert.DoesNotMatch("#[0-9A-Fa-f]{6,8}", File.ReadAllText(Path.Combine(styles, "Palette.Contrast.xaml")));
    }

    /// <summary>The tone ladder of the 2026-10-05 layout, which every banded page draws on.</summary>
    [Theory]
    [InlineData("SectionBrush")]
    [InlineData("InsetBrush")]
    [InlineData("HoverBrush")]
    [InlineData("QuietFillBrush")]
    [InlineData("QuietHoverBrush")]
    [InlineData("QuietPressedBrush")]
    public void TheToneLadder_IsInThePalettes(string key)
    {
        string styles = Path.Combine(RepositoryRoot(), "src", "Windsock.App", "Styles");

        Assert.Contains(key, Keys(Path.Combine(styles, "Palette.Dark.xaml")));
    }

    private static SortedSet<string> Keys(string path) =>
        new(KeyPattern().Matches(File.ReadAllText(path)).Select(match => match.Groups[1].Value), StringComparer.Ordinal);

    /// <summary>
    /// Where the repository is: found from this source file's own path, since the
    /// tests run from an artifacts folder that may be anywhere.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    [GeneratedRegex("x:Key=\"([^\"]+)\"")]
    private static partial Regex KeyPattern();
}
