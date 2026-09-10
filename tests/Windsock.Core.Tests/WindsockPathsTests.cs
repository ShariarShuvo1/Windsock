using Xunit;

namespace Windsock.Core.Tests;

public sealed class WindsockPathsTests
{
    [Fact]
    public void Root_LivesUnderLocalAppData()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        Assert.StartsWith(localAppData, WindsockPaths.Root, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Windsock", Path.GetFileName(WindsockPaths.Root));
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
