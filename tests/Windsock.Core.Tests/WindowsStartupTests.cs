using Microsoft.Win32;
using Windsock.Core.Startup;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// Exercises the real registry, under a key of the test's own making.
/// </summary>
public sealed class WindowsStartupTests : IDisposable
{
    private readonly string _key = @"Software\Windsock-tests\" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Nothing to be done, and nothing worth failing a test over.
        }
    }

    [Fact]
    public void ByDefault_WindsockIsNotOnTheList()
    {
        WindowsStartup startup = new(_key);

        Assert.False(startup.IsEnabled);
        Assert.Null(startup.Read());
    }

    /// <summary>
    /// The registry route and the packaged route are chosen at runtime through
    /// one interface, so this one has to answer in the same terms. A State that
    /// disagreed with IsEnabled would show the setting on while the app was not
    /// on the list.
    /// </summary>
    [Fact]
    public async Task TheSwitch_AnswersTheSameAsTheRegistry()
    {
#pragma warning disable CA1859 // The interface is the thing under test; the concrete type would not exercise it.
        IStartupSwitch startup = new WindowsStartup(_key);
#pragma warning restore CA1859

        Assert.Equal(StartupState.Off, startup.State);
        Assert.Equal(StartupState.Off, await startup.RefreshAsync());

        Assert.Equal(StartupState.On, await startup.SetAsync(true));
        Assert.Equal(StartupState.On, startup.State);

        Assert.Equal(StartupState.Off, await startup.SetAsync(false));
        Assert.Equal(StartupState.Off, startup.State);
    }

    /// <summary>
    /// Nothing to explain on this route, and the panel hides the note when it
    /// is empty. A non-empty string here would put a permanent warning under a
    /// setting that works.
    /// </summary>
    [Fact]
    public void TheRegistryRoute_HasNothingToExplain() =>
        Assert.Empty(new WindowsStartup(_key).Detail);

    [Fact]
    public void TurnedOn_WindsockIsOnTheList()
    {
        WindowsStartup startup = new(_key);

        Assert.True(startup.Set(true, @"C:\Program Files\Windsock\Windsock.exe"));
        Assert.True(startup.IsEnabled);
        Assert.Equal(@"""C:\Program Files\Windsock\Windsock.exe"" --background", startup.Read());
    }

    [Fact]
    public void TurnedOff_WindsockComesOffAgain()
    {
        WindowsStartup startup = new(_key);

        startup.Set(true, @"C:\Windsock\Windsock.exe");

        Assert.True(startup.Set(false));
        Assert.False(startup.IsEnabled);
    }

    [Fact]
    public void TurnedOffWhenItWasNeverOn_IsNotAnError()
    {
        WindowsStartup startup = new(_key);

        Assert.True(startup.Set(false));
        Assert.False(startup.IsEnabled);
    }

    [Fact]
    public void TurnedOnAgain_TakesTheNewPath()
    {
        WindowsStartup startup = new(_key);

        startup.Set(true, @"C:\old\Windsock.exe");
        startup.Set(true, @"C:\new\Windsock.exe");

        Assert.Equal(@"""C:\new\Windsock.exe"" --background", startup.Read());
    }

    [Fact]
    public void ThePathIsQuoted_SoOneWithSpacesSurvives()
    {
        // "C:\Program Files\..." unquoted is an instruction to run C:\Program.
        Assert.StartsWith(
            @"""C:\Program Files\Windsock\Windsock.exe""",
            WindowsStartup.Command(@"C:\Program Files\Windsock\Windsock.exe"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommandSaysWhoStartedIt() =>
        Assert.EndsWith(
            WindowsStartup.BackgroundSwitch,
            WindowsStartup.Command(@"C:\Windsock\Windsock.exe"),
            StringComparison.Ordinal);

    [Theory]
    [InlineData("--background")]
    [InlineData("--BACKGROUND")]
    public void StartedByWindows_IsRecognised(string argument) =>
        Assert.True(WindowsStartup.IsQuiet([argument]));

    [Fact]
    public void StartedByAPerson_IsNot()
    {
        Assert.False(WindowsStartup.IsQuiet([]));
        Assert.False(WindowsStartup.IsQuiet(null));
        Assert.False(WindowsStartup.IsQuiet(["--something-else"]));
    }

    [Fact]
    public void TheSwitchAmongOthers_IsStillFound() =>
        Assert.True(WindowsStartup.IsQuiet(["--verbose", "--background", "--whatever"]));
}
