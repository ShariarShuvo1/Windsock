using Windsock.Core.Feed;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class UserPathTests
{
    private const string Folder = @"C:\Users\me\AppData\Local\Windsock\current";

    private static string Expand(string text) => text.Replace("%LOCALAPPDATA%", @"C:\Users\me\AppData\Local", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Adding_AppendsOnce_AndKeepsTheRest()
    {
        string path = @"C:\Tools;%USERPROFILE%\bin";

        string added = UserPath.Add(path, Folder, Expand);

        Assert.Equal(@"C:\Tools;%USERPROFILE%\bin;" + Folder, added);
        Assert.Equal(added, UserPath.Add(added, Folder, Expand));
        Assert.Equal(Folder, UserPath.Add(null, Folder, Expand));
        Assert.Equal(@"C:\Tools;" + Folder, UserPath.Add(@"C:\Tools;", Folder + @"\", Expand));
    }

    /// <summary>An entry written with a variable, quotes, a trailing slash or other case is the same folder.</summary>
    [Theory]
    [InlineData(@"%LOCALAPPDATA%\Windsock\current")]
    [InlineData(@"""C:\Users\me\AppData\Local\Windsock\current""")]
    [InlineData(@"c:\users\ME\appdata\local\windsock\CURRENT\")]
    [InlineData(@"  C:\Users\me\AppData\Local\Windsock\current  ")]
    public void TheSameFolder_IsRecognisedHoweverItIsWritten(string entry)
    {
        string path = @"C:\Tools;" + entry + @";D:\Other";

        Assert.True(UserPath.Contains(path, Folder, Expand));
        Assert.Equal(@"C:\Tools;D:\Other", UserPath.Remove(path, Folder, Expand));
    }

    [Fact]
    public void Removing_TakesOnlyThatFolder()
    {
        Assert.Equal(@"C:\Tools", UserPath.Remove(@"C:\Tools;" + Folder + ";" + Folder, Folder, Expand));
        Assert.Equal(string.Empty, UserPath.Remove(Folder, Folder, Expand));
        Assert.Equal(@"C:\Tools;C:\Users\me\AppData\Local\Windsock\current-old", UserPath.Remove(@"C:\Tools;C:\Users\me\AppData\Local\Windsock\current-old", Folder, Expand));
        Assert.False(UserPath.Contains(@"C:\Tools;;", Folder, Expand));
    }
}
