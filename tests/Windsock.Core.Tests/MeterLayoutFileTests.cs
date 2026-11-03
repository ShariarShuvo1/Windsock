using Windsock.Core.Formatting;
using Windsock.Core.Meter;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// Layouts shared as a file, read strictly because they come from someone
/// else, and the one-click presets.
/// </summary>
public sealed class MeterLayoutFileTests
{
    [Fact]
    public void ALayout_ComesBackAsItWent()
    {
        TaskbarSettings mine = new();
        MeterPresets.Gamer.ApplyTo(mine);
        mine.Slots[0].Graph = MeterGraph.Line;
        mine.Slots[1].Levels = [new MeterLevel { At = 90, Colour = MeterLevels.Slow }];
        mine.Slots[2].Card = MeterSlot.EveryCard;
        mine.Slots[3].Rotate = [MeterField.TodayTotal, MeterField.MonthTotal];
        mine.TextSize = MeterTextSize.Large;
        mine.Background = "#C0102030";
        mine.Shadow = true;

        LayoutRead read = MeterLayoutFile.Read(MeterLayoutFile.Write(mine));

        Assert.Null(read.Problem);
        TaskbarSettings theirs = new();
        MeterLayoutFile.Apply(read.Layout!, theirs);

        Assert.Equal(2, theirs.Rows);
        Assert.Equal(3, theirs.Columns);
        Assert.Equal(MeterField.GraphicsLoad, theirs.Slot(0, 2));
        Assert.Equal(MeterGraph.Line, theirs.Slots[0].Graph);
        Assert.Equal(90, theirs.Slots[1].Levels![0].At);
        Assert.Equal(MeterSlot.EveryCard, theirs.Slots[2].Card);
        Assert.Equal([MeterField.TodayTotal, MeterField.MonthTotal], theirs.Slots[3].Rotate);
        Assert.Equal(MeterTextSize.Large, theirs.TextSize);
        Assert.Equal("#C0102030", theirs.Background);
        Assert.True(theirs.Shadow);
    }

    /// <summary>
    /// Where the meter sits, what a click does, its shortcuts and the feeds
    /// switch are this PC's own: a layout neither carries nor changes them.
    /// </summary>
    [Fact]
    public void ALayout_LeavesThisPcsOwnChoicesAlone()
    {
        TaskbarSettings mine = new() { Placement = MeterPlacement.Floating, Click = MeterClick.Mini, Feeds = true };
        mine.Shortcuts.ShowWindow = "Ctrl+Alt+W";

        string written = MeterLayoutFile.Write(mine);

        Assert.DoesNotContain("Placement", written, StringComparison.Ordinal);
        Assert.DoesNotContain("Click", written, StringComparison.Ordinal);
        Assert.DoesNotContain("Ctrl+Alt+W", written, StringComparison.Ordinal);
        Assert.DoesNotContain("Feeds", written, StringComparison.Ordinal);

        TaskbarSettings theirs = new() { Placement = MeterPlacement.Docked, Click = MeterClick.Nothing };
        MeterLayoutFile.Apply(MeterLayoutFile.Read(written).Layout!, theirs);

        Assert.Equal(MeterPlacement.Docked, theirs.Placement);
        Assert.Equal(MeterClick.Nothing, theirs.Click);
        Assert.False(theirs.Feeds);
    }

    [Fact]
    public void TheAddressesAJsonPlaceReads_AreListedBeforeAnyoneSaysYes()
    {
        TaskbarSettings mine = new();
        mine.SetSlot(0, 0, MeterField.Feed);
        mine.Slots[0].Feed = new MeterFeed { Address = "https://example.com/price.json", Path = "data.price", Unit = "$" };

        LayoutRead read = MeterLayoutFile.Read(MeterLayoutFile.Write(mine));

        Assert.Equal(["https://example.com/price.json"], read.Addresses);
    }

    public static TheoryData<string, string> Refused => new()
    {
        { "", "empty" },
        { "{ \"Format\": \"Something else\", \"Version\": 1, \"Rows\": 1, \"Columns\": 1 }", "not a Windsock meter layout" },
        { "{ \"Format\": \"Windsock meter layout\", \"Version\": 2, \"Rows\": 1, \"Columns\": 1 }", "newer Windsock" },
        { "{ \"Format\": \"Windsock meter layout\", \"Version\": 0, \"Rows\": 1, \"Columns\": 1 }", "which version" },
        { "{ \"Format\": \"Windsock meter layout\", \"Version\": 1, \"Rows\": 3, \"Columns\": 1 }", "at most" },
        { "{ \"Format\": \"Windsock meter layout\", \"Version\": 1, \"Rows\": 1, \"Columns\": 1, \"Slots\": [ { \"Field\": \"Download\" }, { \"Field\": \"Upload\" } ] }", "more readings" },
        { "{ \"Format\": \"Windsock meter layout\", \"Version\": 1, \"Rows\": 1, \"Columns\": 1, \"Slots\": [ { \"Field\": \"Teleport\" } ] }", "not a meter layout" },
        { "{ \"Format\": \"Windsock meter layout\", \"Version\": 1, \"Rows\": 1, \"Columns\": 1, \"Slots\": [ { \"Field\": 3 } ] }", "not a meter layout" },
        { "{ \"Format\": \"Windsock meter layout\", \"Version\": 1, \"Rows\": 1, \"Columns\": 1, \"Slots\": [ { \"Field\": \"Download\", \"Sneaky\": true } ] }", "not a meter layout" },
        { "{ \"Format\": \"Windsock meter layout\", \"Version\": 1, \"Rows\": 1, \"Columns\": 1, \"Slots\": [ { \"Field\": \"Download\", \"ValueColour\": \"red; rm -rf\" } ] }", "not a colour" },
        { "{ \"Format\": \"Windsock meter layout\", \"Version\": 1, \"Rows\": 1, \"Columns\": 1, \"Slots\": [ { \"Field\": \"Download\", \"Levels\": [ { \"At\": 1, \"Colour\": \"own\" } ] } ] }", "colour level" },
        { "{ \"Format\": \"Windsock meter layout\", \"Version\": 1, \"Rows\": 1, \"Columns\": 1, \"Slots\": [ { \"Field\": \"Feed\", \"Feed\": { \"Address\": \"http://example.com/x.json\", \"Path\": \"a\" } } ] }", "address cannot be used" },
        { "{ \"Format\": \"Windsock meter layout\", \"Version\": 1, \"Rows\": 1, \"Columns\": 1, \"Slots\": [ { \"Field\": \"Feed\", \"Feed\": { \"Address\": \"https://example.com/x.json\", \"Path\": \"a..b\" } } ] }", "field is not written" },
        { "{ \"Format\": \"Windsock meter layout\", \"Version\": 1, \"Rows\": 1, \"Columns\": 1, \"Slots\": [ { \"Field\": \"Feed\", \"Feed\": { \"Address\": \"https://example.com/x.json\", \"Path\": \"a\", \"Seconds\": 1 } } ] }", "15 seconds" },
        { "{ \"Format\": \"Windsock meter layout\", \"Version\": 1, \"Rows\": 1, \"Columns\": 1, \"Slots\": [ { \"Field\": \"Download\", \"Rotate\": [ \"Feed\" ] } ] }", "rotates" },
        { "{ \"Format\": \"Windsock meter layout\", \"Version\": 1, \"Rows\": 1, \"Columns\": 1, \"Look\": { \"Width\": 9000 } }", "width" },
        { "{ \"Format\": \"Windsock meter layout\", \"Version\": 1, \"Rows\": 1, \"Columns\": 1, \"Look\": { \"Background\": \"good\" } }", "background" },
        { "not json at all", "not a meter layout" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void AnythingItCannotVouchFor_RefusesTheWholeFile(string text, string why)
    {
        LayoutRead read = MeterLayoutFile.Read(text);

        Assert.Null(read.Layout);
        Assert.Contains(why, read.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFileFarTooLarge_IsNotRead()
    {
        Assert.Contains("larger", MeterLayoutFile.Read(new string(' ', MeterLayoutFile.Largest + 1) + "{}").Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void FewerReadingsThanPlaces_AreFilledWithNothing()
    {
        LayoutRead read = MeterLayoutFile.Read(
            "{ \"Format\": \"Windsock meter layout\", \"Version\": 1, \"Rows\": 2, \"Columns\": 2, \"Slots\": [ { \"Field\": \"Download\" } ] }");

        Assert.Null(read.Problem);
        Assert.Equal(4, read.Layout!.Slots.Count);
        Assert.Equal(MeterField.Empty, read.Layout.Slots[3].Field);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("own", true)]
    [InlineData("good", true)]
    [InlineData("#ABC", true)]
    [InlineData("#a0b1c2", true)]
    [InlineData("#80A0B1C2", true)]
    [InlineData("#12345", false)]
    [InlineData("blue", false)]
    [InlineData("#GG0000", false)]
    public void AColour_IsOneOfTheFewThingsAColourMayBe(string? colour, bool valid)
    {
        Assert.Equal(valid, MeterLayoutFile.IsColour(colour));
    }

    [Fact]
    public void APreset_ReplacesThePlaces_AndNothingElse()
    {
        TaskbarSettings settings = new() { Placement = MeterPlacement.Floating, TextSize = MeterTextSize.Huge };

        MeterPresets.Laptop.ApplyTo(settings);

        Assert.Equal(2, settings.Columns);
        Assert.Equal(MeterField.BatteryCharge, settings.Slot(0, 1));
        Assert.Equal(MeterPlacement.Floating, settings.Placement);
        Assert.Equal(MeterTextSize.Huge, settings.TextSize);
        Assert.True(MeterPresets.Laptop.Matches(settings));
        Assert.False(MeterPresets.Gamer.Matches(settings));
    }

    [Fact]
    public void APresetApplied_SharesNothingWithThePreset()
    {
        TaskbarSettings settings = new();

        MeterPresets.Compact.ApplyTo(settings);
        settings.Slots[0].Graph = MeterGraph.None;

        Assert.Equal(MeterGraph.Bars, MeterPresets.Compact.Places[0].Graph);
    }

    [Fact]
    public void TheDefaultMeter_IsTheNetworkPreset()
    {
        Assert.True(MeterPresets.Network.Matches(new TaskbarSettings()));
    }

    [Fact]
    public void TheLaptopPreset_IsOfferedOnlyWithABattery()
    {
        Assert.Contains(MeterPresets.Laptop, MeterPresets.For(hasBattery: true));
        Assert.DoesNotContain(MeterPresets.Laptop, MeterPresets.For(hasBattery: false));
    }

    /// <summary>A preset never turns on anything that reaches beyond this PC.</summary>
    [Fact]
    public void NoPreset_ReachesBeyondThisPc()
    {
        foreach (MeterPreset preset in MeterPresets.All)
        {
            Assert.DoesNotContain(preset.Places, place => place.Field is MeterField.PublicAddress or MeterField.Feed or MeterField.InternetPing);
        }
    }

    [Fact]
    public void EveryPreset_FitsTheMeter()
    {
        foreach (MeterPreset preset in MeterPresets.All)
        {
            Assert.InRange(preset.Rows, 1, TaskbarSettings.MaximumRows);
            Assert.InRange(preset.Columns, 1, TaskbarSettings.MaximumColumns);
            Assert.Equal(preset.Rows * preset.Columns, preset.Places.Count);
        }
    }
}
