using Windsock.Core.Formatting;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class TaskbarSettingsTests
{
    /// <summary>
    /// The meter reaches into Explorer's own window, so it is a thing the
    /// reader turns on rather than a thing that happens to them on first run.
    /// </summary>
    [Fact]
    public void Defaults_LeaveTheMeterOff()
    {
        Assert.False(new TaskbarSettings().Enabled);
        Assert.False(new WindsockSettings().Taskbar.Enabled);
    }

    [Fact]
    public void Defaults_StackDownloadOverUpload()
    {
        var settings = new TaskbarSettings();

        Assert.Equal(2, settings.Rows);
        Assert.Equal(1, settings.Columns);
        Assert.Equal(MeterField.Download, settings.Slot(0, 0));
        Assert.Equal(MeterField.Upload, settings.Slot(1, 0));
    }

    [Fact]
    public void Defaults_AreLegibleOnABusyTaskbar()
    {
        var settings = new TaskbarSettings();
        Assert.True(settings.Bold);
        Assert.Equal(MeterColouring.Plain, settings.Colouring);
        Assert.True(settings.FitToContents);
        Assert.Equal(MeterPlacement.Docked, settings.Placement);
        Assert.Equal(RateFamily.Bytes, settings.Family);
        Assert.Equal(RateScale.Auto, settings.Scale);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(99, TaskbarSettings.MaximumRows)]
    public void Rows_StayWithinWhatFitsOnATaskbar(int wanted, int expected)
    {
        Assert.Equal(expected, new TaskbarSettings { Rows = wanted }.Rows);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, TaskbarSettings.MaximumColumns)]
    public void Columns_StayWithinWhatFitsOnATaskbar(int wanted, int expected)
    {
        Assert.Equal(expected, new TaskbarSettings { Columns = wanted }.Columns);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-40)]
    [InlineData(10_000)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Width_StaysWithinWhatFitsOnATaskbar(double wanted)
    {
        var settings = new TaskbarSettings { Width = wanted };

        Assert.InRange(settings.Width, TaskbarSettings.MinimumWidth, TaskbarSettings.MaximumWidth);
    }

    [Fact]
    public void Width_KeepsAReasonableValue()
    {
        Assert.Equal(150, new TaskbarSettings { Width = 150 }.Width);
    }

    [Fact]
    public void SetSlot_GrowsTheListToReachThePosition()
    {
        var settings = new TaskbarSettings { Rows = 2, Columns = 2 };

        settings.SetSlot(1, 1, MeterField.GraphicsTemperature);

        Assert.Equal(MeterField.GraphicsTemperature, settings.Slot(1, 1));
        Assert.Equal(MeterField.Empty, settings.Slot(1, 0));
    }

    /// <summary>
    /// A slot past the end of a short list reads as empty rather than throwing:
    /// a settings file written by another version may hold any length.
    /// </summary>
    [Fact]
    public void Slot_ReadsEmptyPastTheEndOfTheList()
    {
        var settings = new TaskbarSettings
        {
            Rows = TaskbarSettings.MaximumRows,
            Columns = TaskbarSettings.MaximumColumns,
            Slots = [new MeterSlot { Field = MeterField.Download }],
        };

        Assert.Equal(MeterField.Download, settings.Slot(0, 0));
        Assert.Equal(MeterField.Empty, settings.Slot(2, 3));
    }

    /// <summary>
    /// The editor grows the grid with a cell and shrinks it by emptying one, so
    /// what counts as spare has to be exact: the end only, never the middle,
    /// and never the last row or column standing.
    /// </summary>
    [Fact]
    public void Trim_DropsAnEmptyLastColumn()
    {
        var settings = new TaskbarSettings { Rows = 2, Columns = 2, Slots = [] };

        settings.SetSlot(0, 0, MeterField.Download);
        settings.SetSlot(1, 0, MeterField.Upload);

        Assert.True(settings.Trim());
        Assert.Equal(1, settings.Columns);
        Assert.Equal(2, settings.Rows);
        Assert.Equal(MeterField.Download, settings.Slot(0, 0));
        Assert.Equal(MeterField.Upload, settings.Slot(1, 0));
    }

    [Fact]
    public void Trim_DropsAnEmptyLastRow()
    {
        var settings = new TaskbarSettings { Rows = 2, Columns = 2, Slots = [] };

        settings.SetSlot(0, 0, MeterField.Download);
        settings.SetSlot(0, 1, MeterField.Upload);

        Assert.True(settings.Trim());
        Assert.Equal(1, settings.Rows);
        Assert.Equal(2, settings.Columns);
        Assert.Equal(MeterField.Download, settings.Slot(0, 0));
        Assert.Equal(MeterField.Upload, settings.Slot(0, 1));
    }

    [Fact]
    public void Trim_KeepsAGapInTheMiddle()
    {
        var settings = new TaskbarSettings { Rows = 1, Columns = 3, Slots = [] };

        settings.SetSlot(0, 0, MeterField.Download);
        settings.SetSlot(0, 2, MeterField.Upload);

        Assert.False(settings.Trim());
        Assert.Equal(3, settings.Columns);
        Assert.Equal(MeterField.Empty, settings.Slot(0, 1));
    }

    [Fact]
    public void Trim_LeavesOneRowAndOneColumnStanding()
    {
        var settings = new TaskbarSettings { Rows = 2, Columns = 2, Slots = [] };

        Assert.True(settings.Trim());
        Assert.Equal(1, settings.Rows);
        Assert.Equal(1, settings.Columns);
    }

    [Fact]
    public void Trim_SaysNothingChangedWhenTheGridIsFull()
    {
        var settings = new TaskbarSettings { Rows = 1, Columns = 2, Slots = [] };

        settings.SetSlot(0, 0, MeterField.Download);
        settings.SetSlot(0, 1, MeterField.Upload);

        Assert.False(settings.Trim());
    }

    /// <summary>
    /// Two columns emptied at once, which is what clearing the last reading in
    /// a wide meter does.
    /// </summary>
    [Fact]
    public void Trim_DropsEveryEmptyColumnAtTheEnd()
    {
        var settings = new TaskbarSettings { Rows = 2, Columns = 4, Slots = [] };

        settings.SetSlot(0, 0, MeterField.Download);
        settings.SetSlot(0, 1, MeterField.ProcessorLoad);
        settings.SetSlot(1, 0, MeterField.Upload);

        Assert.True(settings.Trim());
        Assert.Equal(2, settings.Columns);
        Assert.Equal(2, settings.Rows);
        Assert.Equal(MeterField.Download, settings.Slot(0, 0));
        Assert.Equal(MeterField.ProcessorLoad, settings.Slot(0, 1));
        Assert.Equal(MeterField.Upload, settings.Slot(1, 0));
    }

    /// <summary>
    /// The editor adds cells, not rows: putting a reading on a line that is not
    /// there yet is what brings the line into being.
    /// </summary>
    [Fact]
    public void Put_GrowsTheMeterToReachThePlace()
    {
        var settings = new TaskbarSettings { Rows = 1, Columns = 1, Slots = [] };

        settings.Put(0, 0, MeterField.Download);
        settings.Put(1, 0, MeterField.Upload);

        Assert.Equal(2, settings.Rows);
        Assert.Equal(1, settings.Columns);
        Assert.Equal(MeterField.Download, settings.Slot(0, 0));
        Assert.Equal(MeterField.Upload, settings.Slot(1, 0));
    }

    /// <summary>
    /// Widening the grid must not slide the readings along it: the list is read
    /// against the shape, so a second row moves the moment the shape changes.
    /// </summary>
    [Fact]
    public void Put_KeepsEveryReadingWhereItWasWhenTheGridWidens()
    {
        var settings = new TaskbarSettings { Rows = 2, Columns = 2, Slots = [] };

        settings.Put(0, 0, MeterField.Download);
        settings.Put(0, 1, MeterField.ProcessorLoad);
        settings.Put(1, 0, MeterField.Upload);
        settings.Put(1, 1, MeterField.GraphicsPower);

        settings.Put(0, 2, MeterField.MemoryLoad);

        Assert.Equal(3, settings.Columns);
        Assert.Equal(2, settings.Rows);
        Assert.Equal(MeterField.Download, settings.Slot(0, 0));
        Assert.Equal(MeterField.ProcessorLoad, settings.Slot(0, 1));
        Assert.Equal(MeterField.MemoryLoad, settings.Slot(0, 2));
        Assert.Equal(MeterField.Upload, settings.Slot(1, 0));
        Assert.Equal(MeterField.GraphicsPower, settings.Slot(1, 1));
        Assert.Equal(MeterField.Empty, settings.Slot(1, 2));
    }

    [Fact]
    public void Put_IgnoresAPlaceTheMeterMayNotHave()
    {
        var settings = new TaskbarSettings { Rows = 1, Columns = 1, Slots = [] };

        settings.Put(TaskbarSettings.MaximumRows, 0, MeterField.Download);
        settings.Put(0, TaskbarSettings.MaximumColumns, MeterField.Download);

        Assert.Equal(1, settings.Rows);
        Assert.Equal(1, settings.Columns);
        Assert.Equal(MeterField.Empty, settings.Slot(0, 0));
    }

    /// <summary>
    /// The two together, which is what the editor does: a cell is added, then
    /// taken away again, and the meter ends up the size it started.
    /// </summary>
    [Fact]
    public void PutThenClear_LeavesTheMeterAsItWas()
    {
        var settings = new TaskbarSettings { Rows = 1, Columns = 1, Slots = [] };

        settings.Put(0, 0, MeterField.Download);
        settings.Put(1, 0, MeterField.Upload);

        settings.SetSlot(1, 0, MeterField.Empty);
        Assert.True(settings.Trim());

        Assert.Equal(1, settings.Rows);
        Assert.Equal(1, settings.Columns);
        Assert.Equal(MeterField.Download, settings.Slot(0, 0));
    }

    /// <summary>
    /// How a cell looks belongs to the cell, so it has to travel with it: the
    /// grid reflows whenever a column is added or taken away, and a colour left
    /// behind would land on somebody else's reading.
    /// </summary>
    [Fact]
    public void Widening_CarriesEachCellsLookWithIt()
    {
        var settings = new TaskbarSettings { Rows = 2, Columns = 2, Slots = [] };

        settings.Put(0, 0, MeterField.Download);
        settings.Put(1, 0, MeterField.Upload);

        settings.Reach(1, 0)!.Family = RateFamily.Bits;
        settings.Reach(1, 0)!.ValueColour = "#ff8800";

        settings.Put(0, 2, MeterField.MemoryLoad);

        Assert.Equal(3, settings.Columns);
        Assert.Equal(MeterField.Upload, settings.Slot(1, 0));
        Assert.Equal(RateFamily.Bits, settings.At(1, 0)!.Family);
        Assert.Equal("#ff8800", settings.At(1, 0)!.ValueColour);
    }

    [Fact]
    public void Trimming_CarriesEachCellsLookWithIt()
    {
        var settings = new TaskbarSettings { Rows = 2, Columns = 3, Slots = [] };

        settings.Put(0, 0, MeterField.Download);
        settings.Reach(0, 0)!.Bold = true;
        settings.Reach(0, 0)!.TextSize = MeterTextSize.Large;

        Assert.True(settings.Trim());

        Assert.Equal(1, settings.Rows);
        Assert.Equal(1, settings.Columns);
        Assert.True(settings.At(0, 0)!.Bold);
        Assert.Equal(MeterTextSize.Large, settings.At(0, 0)!.TextSize);
    }

    /// <summary>
    /// Nothing said about a cell means the cell does whatever the meter does,
    /// so a meter nobody has fiddled with is uniform.
    /// </summary>
    [Fact]
    public void ANewCell_SaysNothingAboutHowItLooks()
    {
        var settings = new TaskbarSettings { Rows = 1, Columns = 1, Slots = [] };

        settings.Put(0, 0, MeterField.Download);

        MeterSlot slot = settings.At(0, 0)!;

        Assert.Null(slot.Family);
        Assert.Null(slot.Scale);
        Assert.Null(slot.ShowUnit);
        Assert.Null(slot.ShowLabel);
        Assert.Null(slot.TextSize);
        Assert.Null(slot.Bold);
        Assert.Null(slot.LabelColour);
        Assert.Null(slot.ValueColour);
        Assert.Null(slot.UnitColour);
    }

    /// <summary>
    /// Giving every reading its own colour was one switch for the whole meter
    /// before each part of each cell could be painted. A meter saved that way
    /// has to keep looking the same, which means the switch has to become what
    /// it would be said with now.
    /// </summary>
    [Fact]
    public void AMeterSavedWithOneColourPerReading_KeepsItsColours()
    {
        var settings = new TaskbarSettings
        {
            Colouring = MeterColouring.ByGroup,
            Slots =
            [
                new MeterSlot { Field = MeterField.Download },
                new MeterSlot { Field = MeterField.Upload },
            ],
        };

        settings.Settle();

        Assert.Equal(MeterSlot.Own, settings.Slots[0].LabelColour);
        Assert.Equal(MeterSlot.Own, settings.Slots[0].ValueColour);
        Assert.Equal(MeterSlot.Own, settings.Slots[1].ValueColour);
        Assert.Null(settings.Slots[0].UnitColour);

        // And the switch itself is spent: nothing reads it after this.
        Assert.Equal(MeterColouring.Plain, settings.Colouring);
    }

    [Fact]
    public void SettlingAMeterSavedWithOneColourPerReading_LeavesAPaintedPartAlone()
    {
        var settings = new TaskbarSettings
        {
            Colouring = MeterColouring.ByGroup,
            Slots = [new MeterSlot { Field = MeterField.Download, ValueColour = "#FF0000" }],
        };

        settings.Settle();

        Assert.Equal("#FF0000", settings.Slots[0].ValueColour);
    }

    [Fact]
    public void SettlingAPlainMeter_PaintsNothing()
    {
        var settings = new TaskbarSettings
        {
            Slots = [new MeterSlot { Field = MeterField.Download }],
        };

        settings.Settle();

        Assert.Null(settings.Slots[0].ValueColour);
        Assert.Null(settings.Slots[0].LabelColour);
    }

    /// <summary>
    /// Two figures from one piece of hardware wear the same mark, because the
    /// figure beside it says which of the two it is. That is the whole reason
    /// there are seven marks for fourteen readings.
    /// </summary>
    [Theory]
    [InlineData(MeterField.Download, MeterMark.Download)]
    [InlineData(MeterField.Upload, MeterMark.Upload)]
    [InlineData(MeterField.NetworkTotal, MeterMark.Network)]
    [InlineData(MeterField.ProcessorLoad, MeterMark.Processor)]
    [InlineData(MeterField.ProcessorClock, MeterMark.Processor)]
    [InlineData(MeterField.GraphicsTemperature, MeterMark.Graphics)]
    [InlineData(MeterField.GraphicsMemory, MeterMark.Graphics)]
    [InlineData(MeterField.MemoryUsed, MeterMark.Memory)]
    [InlineData(MeterField.StorageTemperature, MeterMark.Storage)]
    [InlineData(MeterField.Empty, MeterMark.None)]
    public void AReadingIsMarked_ByThePartOfTheMachineItIsAbout(
        MeterField field, MeterMark expected)
    {
        Assert.Equal(expected, MeterReadout.MarkOf(field));
    }

    /// <summary>
    /// Every mark the app has to be able to draw. A reading whose mark had no
    /// picture would be drawn with whatever the fallback is, which reads as a
    /// missing icon rather than as a reading.
    /// </summary>
    [Fact]
    public void EveryReading_HasAMark()
    {
        foreach (MeterField field in Enum.GetValues<MeterField>())
        {
            if (field == MeterField.Empty)
            {
                continue;
            }

            Assert.NotEqual(MeterMark.None, MeterReadout.MarkOf(field));
        }
    }

    [Fact]
    public void ANewCell_IsMarkedWithAPicture()
    {
        Assert.Equal(MeterMarkStyle.Icon, new MeterSlot().MarkAs);
        Assert.False(new MeterSlot().MarkIsChanged);
    }

    /// <summary>
    /// Each sheet in the editor puts its own part back. A reset that reached
    /// further than its own part would take away choices the reader made
    /// somewhere else without being asked.
    /// </summary>
    [Fact]
    public void PuttingOnePartBack_LeavesTheOtherTwoAlone()
    {
        var slot = new MeterSlot
        {
            Field = MeterField.Download,
            ShowLabel = false,
            MarkAs = MeterMarkStyle.Letters,
            LabelColour = "#FF0000",
            Family = RateFamily.Bits,
            Bold = true,
            ValueColour = "#00FF00",
            ShowUnit = false,
            UnitColour = "#0000FF",
        };

        slot.PlainMark();

        Assert.False(slot.MarkIsChanged);
        Assert.Null(slot.ShowLabel);
        Assert.Equal(MeterMarkStyle.Icon, slot.MarkAs);
        Assert.True(slot.ValueIsChanged);
        Assert.True(slot.UnitIsChanged);

        slot.PlainValue();

        Assert.False(slot.ValueIsChanged);
        Assert.Null(slot.Family);
        Assert.True(slot.UnitIsChanged);

        slot.PlainUnit();

        Assert.False(slot.UnitIsChanged);
        Assert.Equal(MeterField.Download, slot.Field);
    }

    [Theory]
    [InlineData(MeterSide.Right, MeterSide.Right, MeterSide.Right, 0)]
    [InlineData(MeterSide.Left, MeterSide.Right, MeterSide.Right, 1)]

    // The figure free to sit beside the mark instead of beside the unit.
    [InlineData(MeterSide.Left, MeterSide.Left, MeterSide.Right, 2)]

    // Everything to the left, with the room after it.
    [InlineData(MeterSide.Left, MeterSide.Left, MeterSide.Left, 3)]
    [InlineData(MeterSide.Left, MeterSide.Right, MeterSide.Left, 1)]
    [InlineData(MeterSide.Right, MeterSide.Left, MeterSide.Left, 0)]
    public void TheRoomInAPlace_FallsAfterThePartsAskingForTheLeft(
        MeterSide mark, MeterSide value, MeterSide unit, int expected)
    {
        var slot = new MeterSlot
        {
            Field = MeterField.Download,
            MarkSide = mark,
            ValueSide = value,
            UnitSide = unit,
        };

        Assert.Equal(expected, slot.PartsOnTheLeft);
    }

    [Fact]
    public void ANewCell_HasEveryPartAgainstTheRightEdge()
    {
        var slot = new MeterSlot();

        Assert.Equal(MeterSide.Right, slot.MarkSide);
        Assert.Equal(MeterSide.Right, slot.ValueSide);
        Assert.Equal(MeterSide.Right, slot.UnitSide);
        Assert.Equal(0, slot.PartsOnTheLeft);
        Assert.False(slot.MarkIsChanged);
        Assert.False(slot.ValueIsChanged);
        Assert.False(slot.UnitIsChanged);
    }

    [Fact]
    public void PuttingAPartBack_BringsItBackToTheRightEdge()
    {
        var slot = new MeterSlot
        {
            Field = MeterField.Download,
            MarkSide = MeterSide.Left,
            ValueSide = MeterSide.Left,
            UnitSide = MeterSide.Left,
        };

        Assert.True(slot.MarkIsChanged);

        slot.PlainMark();

        Assert.Equal(MeterSide.Right, slot.MarkSide);
        Assert.Equal(MeterSide.Left, slot.ValueSide);

        slot.PlainValue();
        slot.PlainUnit();

        Assert.Equal(0, slot.PartsOnTheLeft);
    }

    [Fact]
    public void AFreshMeter_IsRoundedLikeTheTaskbarOwnButtons()
    {
        Assert.Equal(TaskbarSettings.DefaultCorner, new TaskbarSettings().Corner);
    }

    [Theory]
    [InlineData(-40, TaskbarSettings.MinimumCorner)]
    [InlineData(9999, TaskbarSettings.MaximumCorner)]
    [InlineData(double.NaN, TaskbarSettings.DefaultCorner)]
    [InlineData(double.PositiveInfinity, TaskbarSettings.DefaultCorner)]
    public void ARoundnessOutsideWhatIsAllowed_IsBroughtBackInside(double asked, double kept)
    {
        TaskbarSettings settings = new() { Corner = asked };

        Assert.Equal(kept, settings.Corner);
    }

    [Fact]
    public void AChangedRoundness_CountsAsAChange()
    {
        TaskbarSettings settings = new();
        Assert.True(settings.IsDefault);

        settings.Corner = 0;

        Assert.False(settings.IsDefault);
    }

    [Fact]
    public void PuttingEverythingBack_RestoresTheRoundness()
    {
        TaskbarSettings settings = new() { Corner = TaskbarSettings.MaximumCorner };

        settings.Reset();

        Assert.Equal(TaskbarSettings.DefaultCorner, settings.Corner);
    }

    [Fact]
    public void AFreshMeter_PaintsNoBackground()
    {
        Assert.Null(new TaskbarSettings().Background);
    }

    [Fact]
    public void AChosenBackground_CountsAsAChange()
    {
        TaskbarSettings settings = new();
        Assert.True(settings.IsDefault);

        settings.Background = "#C00E2231";

        Assert.False(settings.IsDefault);
    }

    [Fact]
    public void PuttingEverythingBack_TakesTheBackgroundAway()
    {
        TaskbarSettings settings = new() { Background = "#C00E2231" };

        settings.Reset();

        Assert.Null(settings.Background);
        Assert.True(settings.IsDefault);
    }

    [Fact]
    public void AMeterNobodyHasTouched_SaysSo()
    {
        Assert.True(new TaskbarSettings().IsDefault);
    }

    /// <summary>
    /// Every choice the tab offers, and whether putting them back catches it.
    /// A choice the reset misses is one a reader cannot undo without going
    /// looking for it, which is what the button is there to save them from.
    /// </summary>
    [Theory]
    [InlineData("placement")]
    [InlineData("click")]
    [InlineData("shape")]
    [InlineData("reading")]
    [InlineData("units")]
    [InlineData("size")]
    [InlineData("colour")]
    [InlineData("side")]
    [InlineData("mark")]
    [InlineData("width")]
    [InlineData("fit")]
    public void AnyChoiceAtAll_IsNoticedAndPutBack(string what)
    {
        var settings = new TaskbarSettings();

        switch (what)
        {
            case "placement": settings.Placement = MeterPlacement.Floating; break;
            case "click": settings.Click = MeterClick.Nothing; break;
            case "shape": settings.Put(0, 1, MeterField.ProcessorLoad); break;
            case "reading": settings.SetSlot(0, 0, MeterField.GraphicsLoad); break;
            case "units": settings.Slots[0].Family = RateFamily.Bits; break;
            case "size": settings.Slots[0].TextSize = MeterTextSize.Large; break;
            case "colour": settings.Slots[0].ValueColour = "#FF0000"; break;
            case "side": settings.Slots[0].MarkSide = MeterSide.Left; break;
            case "mark": settings.Slots[0].MarkAs = MeterMarkStyle.Letters; break;
            case "width": settings.Width = 200; break;
            case "fit": settings.FitToContents = false; break;
            default: throw new ArgumentOutOfRangeException(nameof(what));
        }

        Assert.False(settings.IsDefault);

        settings.Reset();

        Assert.True(settings.IsDefault);
    }

    /// <summary>
    /// The one thing putting everything back leaves alone. A button that takes
    /// the meter off the taskbar as a side effect is one people learn not to
    /// press.
    /// </summary>
    [Fact]
    public void PuttingEverythingBack_LeavesTheMeterShowing()
    {
        var settings = new TaskbarSettings { Enabled = true, Bold = false };

        settings.Reset();

        Assert.True(settings.Enabled);
        Assert.True(settings.Bold);
        Assert.True(settings.IsDefault);
    }

    [Fact]
    public void PuttingEverythingBack_BringsTheReadingsBack()
    {
        var settings = new TaskbarSettings();

        settings.Put(0, 1, MeterField.StorageLoad);
        settings.SetSlot(0, 0, MeterField.MemoryUsed);

        settings.Reset();

        Assert.Equal(MeterField.Download, settings.Slot(0, 0));
        Assert.Equal(MeterField.Upload, settings.Slot(1, 0));
        Assert.Equal(1, settings.Columns);
    }

    [Fact]
    public void Slots_AreNeverNull()
    {
        Assert.NotNull(new TaskbarSettings { Slots = null! }.Slots);
    }

    /// <summary>
    /// A settings file with the key present but empty deserialises to null, and
    /// the meter is read on the path that starts the app.
    /// </summary>
    [Fact]
    public void Taskbar_IsNeverNull()
    {
        var settings = new WindsockSettings { Taskbar = null! };

        Assert.NotNull(settings.Taskbar);
        Assert.False(settings.Taskbar.Enabled);
    }
}
