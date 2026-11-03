using System.Text;
using Windsock.Core.Formatting;
using Windsock.Core.Meter;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// What step 3's review found: hostile JSON, nulls in a file, feed addresses
/// that reach other computers, double clicks, a layout from another PC, and
/// a rotating place's graphs.
/// </summary>
public sealed class MeterReviewTests
{
    // ------------------------------------------------------------ hostile JSON

    public static TheoryData<string, string> Hostile => new()
    {
        // A lone half of a surrogate pair, escaped: valid JSON, not text.
        { "{ \"a\": \"\\uD800\" }", "a" },
        { "{ \"a\": \"x\\uDC00y\" }", "a" },
        { "{ \"a\": [ \"\\uD83D\" ] }", "a[0]" },

        // The path names a key no reader can compare without transcoding it.
        { "{ \"\\uD800\": 1 }", "b" },

        // Values of every kind a reader might not expect.
        { "{ \"a\": { \"b\": 1 } }", "a.b.c" },
        { "{ \"a\": 1 }", "a[0]" },
        { "{ \"a\": [1, 2] }", "a.b" },
        { "[1, 2, 3]", "a" },
        { "\"just a string\"", "a" },
        { "12", "a" },
        { "null", "a" },
        { "{ \"a\": \"\" }", "a[5]" },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void AHostileDocument_IsADashAndASentence_NeverAnException(string json, string path)
    {
        FieldReading read = JsonField.Read(json, path);

        Assert.True(read.Value is null || read.Value.Length <= JsonField.Longest);

        if (read.Value is null)
        {
            Assert.False(string.IsNullOrWhiteSpace(read.Problem));
        }
    }

    [Fact]
    public void ALoneSurrogate_SaysWhatIsWrong()
    {
        FieldReading read = JsonField.Read("{ \"a\": \"\\uD800\" }", "a");

        Assert.Null(read.Value);
        Assert.Contains("not text", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void APathWithALoneSurrogate_IsADash_NeverAnException()
    {
        FieldReading read = JsonField.Read("{ \"a\": 1 }", "\uD800");

        Assert.Null(read.Value);
        Assert.NotNull(read.Problem);
    }

    [Fact]
    public void NestingFarPastTheLimit_IsNotRead()
    {
        StringBuilder deep = new();
        deep.Append('[', 10_000).Append(1).Append(']', 10_000);

        FieldReading read = JsonField.Read(deep.ToString(), "[0]");

        Assert.Null(read.Value);
        Assert.Contains("not JSON", read.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    [InlineData("123456789012345678901234567890123456789012345678901234567890")]
    public void ANumberBeyondADouble_IsShownCut_WithNoNumberForTheLevels(string number)
    {
        FieldReading read = JsonField.Read(string.Concat("{ \"a\": ", number, " }"), "a");

        Assert.NotNull(read.Value);
        Assert.True(read.Value!.Length <= JsonField.Longest);

        if (read.Number is { } value)
        {
            Assert.True(double.IsFinite(value));
        }
    }

    [Fact]
    public void AnEnormousString_IsCutToTheRoomThereIs()
    {
        string json = string.Concat("{ \"a\": \"", new string('x', 100_000), "\" }");

        FieldReading read = JsonField.Read(json, "a");

        Assert.Equal(JsonField.Longest, read.Value!.Length);
    }

    // ------------------------------------------------------------- nulls in files

    [Fact]
    public void ALayoutWithNullInEveryText_ReadsAsEmptyText()
    {
        const string File = """
            {
              "Format": "Windsock meter layout",
              "Version": 1,
              "Rows": 1,
              "Columns": 2,
              "Slots": [
                {
                  "Field": "Feed",
                  "LabelColour": null,
                  "ValueColour": null,
                  "UnitColour": null,
                  "Card": null,
                  "Levels": [ { "At": 5, "Colour": null } ],
                  "Feed": { "Address": "https://example.com/a.json", "Path": "a", "Unit": null, "Label": null }
                },
                { "Field": "Download", "Rotate": null, "Levels": null, "Feed": null }
              ],
              "Look": { "Background": null }
            }
            """;

        LayoutRead read = MeterLayoutFile.Read(File);

        Assert.Null(read.Problem);

        MeterSlot feedPlace = read.Layout!.Slots[0];

        Assert.Equal(string.Empty, feedPlace.Feed!.Unit);
        Assert.Equal(string.Empty, feedPlace.Feed.Label);
        Assert.Equal(MeterLevels.Fair, feedPlace.Levels![0].Colour);

        // And it can be drawn: the key and the line a JSON place makes.
        MeterReading reading = new(0, 0, Windsock.Core.Hardware.SystemSnapshot.Empty);

        Assert.NotNull(MeterReadout.FeedKey(feedPlace.Feed));
        Assert.NotNull(MeterReadout.Compose(feedPlace, MeterField.Feed, reading, new TaskbarSettings()).Value);
        Assert.NotNull(MeterReadout.Tooltip(read.Layout.Slots, reading, new TaskbarSettings()));
    }

    [Fact]
    public void ALayoutWithANullAddressOrField_IsRefused_NotThrown()
    {
        const string File = """
            {
              "Format": "Windsock meter layout",
              "Version": 1,
              "Rows": 1,
              "Columns": 1,
              "Slots": [ { "Field": "Feed", "Feed": { "Address": null, "Path": null } } ]
            }
            """;

        LayoutRead read = MeterLayoutFile.Read(File);

        Assert.Null(read.Layout);
        Assert.NotNull(read.Problem);
    }

    [Fact]
    public void ALayoutWithALoneSurrogate_IsRefused_NotThrown()
    {
        const string File = """
            { "Format": "Windsock meter layout", "Version": 1, "Rows": 1, "Columns": 1,
              "Slots": [ { "Field": "Feed", "Feed": { "Address": "https://example.com/\uD800", "Path": "a" } } ] }
            """;

        LayoutRead read = MeterLayoutFile.Read(File);

        Assert.Null(read.Layout);
        Assert.NotNull(read.Problem);
    }

    [Fact]
    public void SettingsWithNullsAndNonsenseInAPlace_AreSettled()
    {
        const string Json = """
            {
              "Taskbar": {
                "Rows": 1, "Columns": 1,
                "Slots": [ {
                  "Field": "Download",
                  "Levels": [ null, { "At": 3, "Colour": null } ],
                  "Rotate": [ "Feed", "Upload", "Pacer", "Empty" ],
                  "RotateSeconds": 0,
                  "Feed": { "Address": null, "Path": null, "Unit": null, "Label": null, "Seconds": 1 }
                } ],
                "MiniSpot": { "Monitor": null, "X": 1, "Y": 2 }
              }
            }
            """;

        string folder = Path.Combine(Path.GetTempPath(), "windsock-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            string file = Path.Combine(folder, "settings.json");
            System.IO.File.WriteAllText(file, Json);

            TaskbarSettings meter = new SettingsStore(file).Load().Taskbar;
            MeterSlot place = meter.Slots[0];

            Assert.Single(place.Levels!);
            Assert.Equal(MeterLevels.Fair, place.Levels![0].Colour);
            Assert.Equal([MeterField.Upload], place.Rotate);
            Assert.Equal(1, place.RotateSeconds);
            Assert.Equal(string.Empty, place.Feed!.Address);
            Assert.Equal(15, place.Feed.Seconds);
            Assert.Equal(string.Empty, meter.MiniSpot!.Monitor);

            _ = place.Copy();
            _ = MeterReadout.FeedKey(place.Feed);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ------------------------------------------------------------- feed addresses

    /// <summary>
    /// Only a file on this PC's own drives. A share makes Windows sign in to
    /// the host with the person's credentials, WebDAV may fall back to plain
    /// http, and a device or pipe is not a file at all.
    /// </summary>
    [Theory]
    [InlineData(@"\\server\share\price.json")]
    [InlineData(@"\\attacker.example@SSL\DavWWWRoot\price.json")]
    [InlineData(@"\\attacker.example@SSL@8443\DavWWWRoot\x.json")]
    [InlineData(@"\\attacker.example\x.json")]
    [InlineData("//server/share/price.json")]
    [InlineData(@"\/server\share\price.json")]
    [InlineData("file://server/share/price.json")]
    [InlineData("file:////server/share/price.json")]
    [InlineData("file://localhost/C:/data/price.json")]
    [InlineData(@"\\.\pipe\windsock")]
    [InlineData(@"\\.\CON")]
    [InlineData(@"\\?\C:\data\price.json")]
    [InlineData(@"\\?\UNC\server\share\price.json")]
    [InlineData(@"C:\data\CON")]
    [InlineData(@"C:\data\nul.json")]
    [InlineData(@"C:\COM1\price.json")]
    [InlineData(@"C:\data\LPT9.txt")]
    [InlineData(@"C:\data\conin$")]
    [InlineData(@"C:\data\price.json:hidden")]
    [InlineData("file:///C:/data/CON")]
    [InlineData(@"C:relative\price.json")]
    [InlineData(@"\data\price.json")]
    public void AnythingButAFileOnThisPcsOwnDrives_IsRefused(string address)
    {
        (FeedKind? kind, string? problem) = FeedAddress.Check(address, _ => false);

        Assert.Null(kind);
        Assert.NotNull(problem);
        Assert.Null(FeedAddress.FilePath(address, _ => false));
    }

    [Fact]
    public void AMappedNetworkDrive_IsAShareUnderALetter()
    {
        (FeedKind? kind, string? problem) = FeedAddress.Check(@"Z:\price.json", letter => letter == 'Z');

        Assert.Null(kind);
        Assert.Contains("share", problem, StringComparison.Ordinal);
        Assert.Equal(FeedKind.File, FeedAddress.Check(@"C:\price.json", letter => letter == 'Z').Kind);
    }

    [Theory]
    [InlineData(@"C:\data\price.json", @"C:\data\price.json")]
    [InlineData("C:/data/price.json", "C:/data/price.json")]
    [InlineData("file:///C:/data/price.json", @"C:\data\price.json")]
    [InlineData(@"D:\console.json", @"D:\console.json")]
    [InlineData(@"C:\data\com10.json", @"C:\data\com10.json")]
    public void AFileOnThisPcsOwnDrives_IsReadFromItsPath(string address, string path)
    {
        Assert.Equal(FeedKind.File, FeedAddress.Check(address, _ => false).Kind);
        Assert.Equal(path, FeedAddress.FilePath(address, _ => false));
    }

    // ------------------------------------------------------------- clicks

    [Theory]
    [InlineData(1, MeterClick.Nothing, PressStep.Click)]
    [InlineData(2, MeterClick.Nothing, PressStep.Nothing)]
    [InlineData(3, MeterClick.Nothing, PressStep.Nothing)]
    [InlineData(1, MeterClick.Mini, PressStep.Wait)]
    [InlineData(2, MeterClick.Mini, PressStep.DoubleClick)]
    [InlineData(3, MeterClick.Mini, PressStep.Nothing)]
    [InlineData(4, MeterClick.Mini, PressStep.Nothing)]
    public void APress_IsAClickADoubleClickOrAWait(int presses, MeterClick doubleClick, PressStep step)
    {
        Assert.Equal(step, MeterPresses.Decide(presses, doubleClick));
    }

    // ------------------------------------------------------------- a layout from elsewhere

    [Fact]
    public void ALayoutFromALaptop_LeavesItsBatteryPlacesEmptyHere()
    {
        TaskbarSettings laptop = new();
        MeterPresets.Laptop.ApplyTo(laptop);
        laptop.Slots[0].Rotate = [MeterField.BatteryCharge, MeterField.ProcessorLoad];

        MeterLayout layout = MeterLayoutFile.Read(MeterLayoutFile.Write(laptop)).Layout!;

        (int emptied, IReadOnlyList<MeterField> missing) = MeterLayoutFile.Fit(
            layout,
            field => field is not (MeterField.BatteryCharge or MeterField.BatteryTime));

        Assert.Equal(2, emptied);
        Assert.Equal([MeterField.BatteryCharge, MeterField.BatteryTime], missing.Order());
        Assert.Equal(MeterField.Download, layout.Slots[0].Field);
        Assert.Equal([MeterField.ProcessorLoad], layout.Slots[0].Rotate);
        Assert.Equal(MeterField.Empty, layout.Slots[1].Field);
        Assert.Equal(MeterField.Empty, layout.Slots[3].Field);
    }

    [Fact]
    public void ALayoutThatFits_IsLeftAlone()
    {
        TaskbarSettings mine = new();
        MeterPresets.Gamer.ApplyTo(mine);
        MeterLayout layout = MeterLayoutFile.Read(MeterLayoutFile.Write(mine)).Layout!;

        (int emptied, IReadOnlyList<MeterField> missing) = MeterLayoutFile.Fit(layout, _ => true);

        Assert.Equal(0, emptied);
        Assert.Empty(missing);
        Assert.True(MeterPresets.Gamer.Matches(Applied(layout)));
    }

    // ------------------------------------------------------------- graphs

    [Fact]
    public void KeepingSomeGraphs_ForgetsOnlyTheOthers()
    {
        MeterHistory history = new();

        history.Add("0:Download:", 1, TimeSpan.FromSeconds(1));
        history.Add("0:Upload:", 2, TimeSpan.FromSeconds(1));
        history.Add("1:RouterPing:", 3, TimeSpan.FromSeconds(1));

        history.Keep(new HashSet<string>(StringComparer.Ordinal) { "0:Download:", "0:Upload:" });

        double[] into = new double[MeterHistory.Span];

        Assert.Equal(1, history.CopyTo("0:Download:", into));
        Assert.Equal(1, history.CopyTo("0:Upload:", into));
        Assert.Equal(0, history.CopyTo("1:RouterPing:", into));
    }

    private static TaskbarSettings Applied(MeterLayout layout)
    {
        TaskbarSettings settings = new();
        MeterLayoutFile.Apply(layout, settings);
        return settings;
    }
}
