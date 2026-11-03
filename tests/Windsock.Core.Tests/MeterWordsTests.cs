using Windsock.Core.Docking;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// What the Taskbar page says about where the meter is, and the small
/// helpers around it.
/// </summary>
public sealed class MeterWordsTests
{
    private static StationNote Bottom(string display = "Display 1") =>
        new(display, TaskbarEdge.Bottom, MeterArrangement.AsSet);

    [Fact]
    public void Off_SaysNothingIsAdded()
    {
        (string text, bool warning) = MeterWords.Describe(MeterSituation.Off);

        Assert.Equal("The meter is off. Nothing is added to the taskbar.", text);
        Assert.False(warning);
    }

    [Fact]
    public void OneDockedMeter_SaysWhereItIs()
    {
        (string text, _) = MeterWords.Describe(new MeterSituation { State = MeterState.Docked, Stations = [Bottom()] });

        Assert.Equal("Showing in the taskbar, beside the notification area.", text);
    }

    [Fact]
    public void SeveralDockedMeters_AreCounted()
    {
        (string text, _) = MeterWords.Describe(new MeterSituation
        {
            State = MeterState.Docked,
            Stations = [Bottom(), Bottom("Display 2")],
        });

        Assert.Equal("Docked on 2 taskbars.", text);
    }

    [Fact]
    public void SeveralFloatingMeters_AreCountedByDisplay()
    {
        (string text, _) = MeterWords.Describe(new MeterSituation
        {
            State = MeterState.Floating,
            Stations = [new StationNote("Display 1", null, MeterArrangement.AsSet), Bottom("Display 2")],
        });

        Assert.Equal("Floating on 2 displays.", text);
    }

    [Theory]
    [InlineData(TaskbarEdge.Left, "The taskbar is on the left, so readings are stacked.")]
    [InlineData(TaskbarEdge.Right, "The taskbar is on the right, so readings are stacked.")]
    public void ASideBar_SaysTheReadingsAreStacked(TaskbarEdge edge, string sentence)
    {
        (string text, _) = MeterWords.Describe(new MeterSituation
        {
            State = MeterState.Docked,
            Stations = [new StationNote("Display 1", edge, MeterArrangement.Stacked)],
        });

        Assert.Equal("Showing in the taskbar, beside the notification area. " + sentence, text);
    }

    [Fact]
    public void AThinBar_SaysTheReadingsSitOnOneLine()
    {
        (string text, _) = MeterWords.Describe(new MeterSituation
        {
            State = MeterState.Docked,
            Stations = [new StationNote("Display 1", TaskbarEdge.Bottom, MeterArrangement.OneLine)],
        });

        Assert.EndsWith("too thin for more than one row, so readings sit on one line.", text);
    }

    [Fact]
    public void EveryMeterOutOfTheWay_SaysSoPlainly()
    {
        (string text, _) = MeterWords.Describe(new MeterSituation
        {
            State = MeterState.Docked,
            Stations = [Bottom() with { FullScreenHidden = true }],
        });

        Assert.EndsWith("Hidden while a full-screen app is in front.", text);
    }

    [Fact]
    public void SomeMetersOutOfTheWay_SayWhich()
    {
        (string text, _) = MeterWords.Describe(new MeterSituation
        {
            State = MeterState.Docked,
            Stations = [Bottom(), Bottom("Display 2") with { FullScreenHidden = true }],
        });

        Assert.Equal("Docked on 2 taskbars. Hidden on Display 2 while a full-screen app is in front.", text);
    }

    [Fact]
    public void ACrowdedBar_IsMentioned()
    {
        (string text, _) = MeterWords.Describe(new MeterSituation
        {
            State = MeterState.Docked,
            Stations = [Bottom() with { Crowded = true }],
        });

        Assert.Contains("crowded", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AReplacedTaskbarThatMadeRoom_IsNamed()
    {
        (string text, _) = MeterWords.Describe(new MeterSituation
        {
            State = MeterState.Docked,
            Stations = [Bottom() with { MadeRoom = true }],
            Replacement = "StartAllBack",
        });

        Assert.EndsWith("On StartAllBack's taskbar the task buttons move aside to make room.", text);
    }

    [Fact]
    public void AnUnrecognisedBar_SaysTheMeterFloats()
    {
        (string text, _) = MeterWords.Describe(new MeterSituation
        {
            State = MeterState.Floating,
            Stations = [new StationNote("Display 1", TaskbarEdge.Bottom, MeterArrangement.AsSet, Floated: true)],
        });

        Assert.Contains("shape was not recognised", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingDisplay_IsNamed()
    {
        (string text, _) = MeterWords.Describe(new MeterSituation
        {
            State = MeterState.Docked,
            Stations = [Bottom()],
            MissingDisplay = "Display 2",
        });

        Assert.EndsWith("Display 2 is not connected, so the meter is on the main taskbar.", text);
    }

    [Fact]
    public void ADisplayWithoutATaskbar_IsNamed()
    {
        (string text, _) = MeterWords.Describe(new MeterSituation
        {
            State = MeterState.Docked,
            Stations = [Bottom()],
            BarlessDisplay = "Display 3",
        });

        Assert.EndsWith("Display 3 has no taskbar to dock into, so the meter is on the main one.", text);
    }

    [Fact]
    public void ARemoteSession_SaysWhoseReadingsTheseAre()
    {
        (string text, bool warning) = MeterWords.Describe(new MeterSituation
        {
            State = MeterState.Docked,
            Stations = [Bottom()],
            RemoteMachine = "BUILD-07",
        });

        Assert.EndsWith("This is a remote session, so these are BUILD-07's readings.", text);
        Assert.False(warning);
    }

    [Fact]
    public void HiddenInARemoteSession_SaysWhy()
    {
        (string text, _) = MeterWords.Describe(new MeterSituation
        {
            State = MeterState.RemoteHidden,
            RemoteMachine = "BUILD-07",
        });

        Assert.Equal("Hidden in this remote session: the readings would be BUILD-07's, not this computer's.", text);
    }

    [Fact]
    public void NoTaskbar_IsTheOneWarning()
    {
        (string text, bool warning) = MeterWords.Describe(new MeterSituation { State = MeterState.Unavailable });

        Assert.True(warning);
        Assert.StartsWith("The taskbar could not be found", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTooltipInARemoteSession_NamesTheComputer()
    {
        Assert.Equal(
            "Readings from BUILD-07, the computer this remote session runs on",
            MeterWords.RemotePreface("BUILD-07"));
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY1", "Display 1")]
    [InlineData(@"\\.\DISPLAY12", "Display 12")]
    [InlineData(@"\\.\WINDISC", "WINDISC")]
    [InlineData("", "Display")]
    [InlineData(null, "Display")]
    public void ADisplay_IsNamedTheWayWindowsNumbersIt(string? device, string expected)
    {
        Assert.Equal(expected, MeterWords.DisplayName(device));
    }

    [Fact]
    public void ADisplaysLabel_GivesItsSizeAndWhetherItIsMain()
    {
        Assert.Equal("Display 1 (main, 1920 × 1080)", MeterWords.DisplayLabel(@"\\.\DISPLAY1", 1920, 1080, true));
        Assert.Equal("Display 2 (2560 × 1440)", MeterWords.DisplayLabel(@"\\.\DISPLAY2", 2560, 1440, false));
    }
}
