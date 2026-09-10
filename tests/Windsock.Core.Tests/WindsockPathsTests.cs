using Xunit;

namespace Windsock.Core.Tests;

public sealed class WindsockPathsTests
{
    private static string LocalAppData => Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolderOption.DoNotVerify);

    [Fact]
    public void Root_LivesUnderLocalAppData()
    {
        Assert.StartsWith(LocalAppData, WindsockPaths.Root, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Windsock", Path.GetFileName(WindsockPaths.Root));
    }

    /// <summary>
    /// The installer packs with <c>--packId Windsock</c>, so Velopack owns
    /// <c>%LOCALAPPDATA%\Windsock</c> and its uninstaller deletes that folder
    /// whole. Putting the database directly under local app data therefore put
    /// it somewhere it would be removed from, which is exactly what happened.
    /// This asserts the publisher folder that keeps the two apart is still
    /// there, because nothing else would notice if it went.
    /// </summary>
    [Fact]
    public void Root_IsNotTheFolderTheInstallerOwns()
    {
        string installDirectory = Path.Combine(LocalAppData, "Windsock");

        Assert.NotEqual(
            installDirectory,
            WindsockPaths.Root.TrimEnd(Path.DirectorySeparatorChar),
            StringComparer.OrdinalIgnoreCase);

        string? publisher = Path.GetDirectoryName(WindsockPaths.Root);
        Assert.NotNull(publisher);
        Assert.Equal(LocalAppData, Path.GetDirectoryName(publisher), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Shariar Shuvo", Path.GetFileName(publisher));
    }

    [Fact]
    public void LogsAndDatabase_AreNestedUnderRoot()
    {
        Assert.Equal(WindsockPaths.Root, Path.GetDirectoryName(WindsockPaths.Logs));
        Assert.Equal(WindsockPaths.Root, Path.GetDirectoryName(WindsockPaths.Database));
    }

    [Fact]
    public void Paths_AreFullyQualified()
    {
        Assert.True(Path.IsPathFullyQualified(WindsockPaths.Root));
        Assert.True(Path.IsPathFullyQualified(WindsockPaths.Logs));
        Assert.True(Path.IsPathFullyQualified(WindsockPaths.Database));
    }
}
