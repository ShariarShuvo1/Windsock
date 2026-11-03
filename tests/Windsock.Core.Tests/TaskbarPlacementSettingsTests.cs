using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The meter's choices about where it goes and how it behaves there: which
/// taskbars, full screen, remote sessions, the steady width, the outline, and
/// floating spots.
/// </summary>
public sealed class TaskbarPlacementSettingsTests : IDisposable
{
    private readonly string _directory;
    private readonly string _file;

    public TaskbarPlacementSettingsTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Windsock-tests", Guid.NewGuid().ToString("N"));
        _file = Path.Combine(_directory, "settings.json");
    }

    [Fact]
    public void Defaults_KeepTheMeterOnTheMainTaskbarStillAndOutOfTheWay()
    {
        var settings = new TaskbarSettings();

        Assert.True(settings.SteadyWidth);
        Assert.Equal(MeterMonitors.Main, settings.Monitors);
        Assert.Null(settings.ChosenMonitor);
        Assert.Equal(MeterFullScreen.Hide, settings.FullScreen);
        Assert.Equal(MeterRemote.Name, settings.Remote);
        Assert.False(settings.Shadow);
        Assert.False(settings.Locked);
        Assert.Empty(settings.Spots);
    }

    [Theory]
    [InlineData("steady")]
    [InlineData("every")]
    [InlineData("chosen")]
    [InlineData("fullscreen")]
    [InlineData("remote")]
    [InlineData("shadow")]
    [InlineData("locked")]
    [InlineData("spot")]
    public void EachNewChoice_IsNoticedAndPutBack(string what)
    {
        var settings = new TaskbarSettings();

        switch (what)
        {
            case "steady": settings.SteadyWidth = false; break;
            case "every": settings.Monitors = MeterMonitors.Every; break;
            case "chosen": settings.Monitors = MeterMonitors.Chosen; settings.ChosenMonitor = @"\\.\DISPLAY2"; break;
            case "fullscreen": settings.FullScreen = MeterFullScreen.Stay; break;
            case "remote": settings.Remote = MeterRemote.Hide; break;
            case "shadow": settings.Shadow = true; break;
            case "locked": settings.Locked = true; break;
            case "spot": settings.Remember(@"\\.\DISPLAY1", 400, 20); break;
            default: throw new ArgumentOutOfRangeException(nameof(what));
        }

        Assert.False(settings.IsDefault);

        settings.Reset();

        Assert.True(settings.IsDefault);
        Assert.Null(settings.ChosenMonitor);
    }

    /// <summary>
    /// A display picked and then given up leaves its name behind; that alone
    /// is not a change worth a Reset button.
    /// </summary>
    [Fact]
    public void ADisplayNameLeftBehind_IsNotAChange()
    {
        var settings = new TaskbarSettings { ChosenMonitor = @"\\.\DISPLAY2" };

        Assert.True(settings.IsDefault);
    }

    [Fact]
    public void ASpot_IsRememberedPerMonitorAndMovedRatherThanAddedAgain()
    {
        var settings = new TaskbarSettings();

        settings.Remember(@"\\.\DISPLAY1", 400, 20);
        settings.Remember(@"\\.\DISPLAY2", -300, 900);
        settings.Remember(@"\\.\display1", 410, 30);

        Assert.Equal(2, settings.Spots.Count);
        Assert.Equal(410, settings.SpotOn(@"\\.\DISPLAY1")!.X);
        Assert.Equal(-300, settings.SpotOn(@"\\.\DISPLAY2")!.X);
        Assert.Null(settings.SpotOn(@"\\.\DISPLAY3"));
    }

    [Fact]
    public void ForgettingASpot_SendsThatMeterBackAndNoOther()
    {
        var settings = new TaskbarSettings();

        settings.Remember(@"\\.\DISPLAY1", 400, 20);
        settings.Remember(@"\\.\DISPLAY2", -300, 900);

        Assert.True(settings.Forget(@"\\.\DISPLAY1"));
        Assert.False(settings.Forget(@"\\.\DISPLAY1"));
        Assert.Null(settings.SpotOn(@"\\.\DISPLAY1"));
        Assert.NotNull(settings.SpotOn(@"\\.\DISPLAY2"));
    }

    [Fact]
    public void ASpotForNoMonitor_IsNotKept()
    {
        var settings = new TaskbarSettings();

        settings.Remember(string.Empty, 1, 2);

        Assert.Empty(settings.Spots);
    }

    [Fact]
    public void Spots_AreNeverNull()
    {
        Assert.NotNull(new TaskbarSettings { Spots = null! }.Spots);
    }

    [Fact]
    public void EveryNewChoice_SurvivesTheFile()
    {
        var store = new SettingsStore(_file);
        var settings = new WindsockSettings();
        TaskbarSettings meter = settings.Taskbar;

        meter.SteadyWidth = false;
        meter.Monitors = MeterMonitors.Chosen;
        meter.ChosenMonitor = @"\\.\DISPLAY2";
        meter.FullScreen = MeterFullScreen.Stay;
        meter.Remote = MeterRemote.Hide;
        meter.Shadow = true;
        meter.Locked = true;
        meter.Remember(@"\\.\DISPLAY2", -1234, 567);

        store.Save(settings);
        TaskbarSettings back = store.Load().Taskbar;

        Assert.False(back.SteadyWidth);
        Assert.Equal(MeterMonitors.Chosen, back.Monitors);
        Assert.Equal(@"\\.\DISPLAY2", back.ChosenMonitor);
        Assert.Equal(MeterFullScreen.Stay, back.FullScreen);
        Assert.Equal(MeterRemote.Hide, back.Remote);
        Assert.True(back.Shadow);
        Assert.True(back.Locked);
        Assert.Equal(-1234, back.SpotOn(@"\\.\DISPLAY2")!.X);
        Assert.Equal(567, back.SpotOn(@"\\.\DISPLAY2")!.Y);
    }

    /// <summary>
    /// A meter saved by 0.1.0, which fitted itself to every digit, opens with
    /// the steady width: the upgrade asked for, with nothing to migrate but
    /// the default.
    /// </summary>
    [Fact]
    public void AMeterSavedBeforeTheseChoices_GetsTheirDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            _file,
            """
            {
              "Taskbar": {
                "Enabled": true,
                "Placement": "Floating",
                "FitToContents": true,
                "Width": 120,
                "Rows": 2,
                "Columns": 1,
                "Slots": [ "Download", "Upload" ]
              }
            }
            """);

        TaskbarSettings meter = new SettingsStore(_file).Load().Taskbar;

        Assert.True(meter.FitToContents);
        Assert.True(meter.SteadyWidth);
        Assert.Equal(MeterMonitors.Main, meter.Monitors);
        Assert.Equal(MeterFullScreen.Hide, meter.FullScreen);
        Assert.Equal(MeterRemote.Name, meter.Remote);
        Assert.False(meter.Locked);
        Assert.Empty(meter.Spots);
    }

    /// <summary>
    /// A fixed width was already steady, and stays the fixed width it was.
    /// </summary>
    [Fact]
    public void AFixedWidthSavedBefore_StaysFixed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_file, """{ "Taskbar": { "FitToContents": false, "Width": 150 } }""");

        TaskbarSettings meter = new SettingsStore(_file).Load().Taskbar;

        Assert.False(meter.FitToContents);
        Assert.Equal(150, meter.Width);
    }

    /// <summary>
    /// Only a file edited by hand can say "a chosen display" and name none, or
    /// keep a spot for no display. Both are put right as the file is read.
    /// </summary>
    [Fact]
    public void ChoicesThatCannotBeHonoured_AreSettled()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            _file,
            """
            {
              "Taskbar": {
                "Monitors": "Chosen",
                "Spots": [ { "Monitor": "", "X": 1, "Y": 2 }, { "Monitor": "\\\\.\\DISPLAY1", "X": 3, "Y": 4 } ]
              }
            }
            """);

        TaskbarSettings meter = new SettingsStore(_file).Load().Taskbar;

        Assert.Equal(MeterMonitors.Main, meter.Monitors);
        Assert.Single(meter.Spots);
        Assert.Equal(3, meter.SpotOn(@"\\.\DISPLAY1")!.X);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder is not a failed test.
        }
    }
}
