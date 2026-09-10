using Windsock.Core.Formatting;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _file;

    public SettingsStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Windsock-tests", Guid.NewGuid().ToString("N"));
        _file = Path.Combine(_directory, "settings.json");
    }

    [Fact]
    public void SavedSettings_ComeBack()
    {
        var store = new SettingsStore(_file);

        store.Save(new WindsockSettings { Theme = ThemePreference.Light });

        Assert.Equal(ThemePreference.Light, store.Load().Theme);
    }

    [Fact]
    public void RecordingHistory_IsOnUntilItIsTurnedOff()
    {
        var store = new SettingsStore(_file);

        // A meter that only counts while you are watching it is not a meter.
        Assert.True(new WindsockSettings().RecordHistory);

        store.Save(new WindsockSettings { RecordHistory = false });

        Assert.False(store.Load().RecordHistory);
    }

    /// <summary>
    /// Settings written before a cell could look like anything list their slots
    /// as bare names. Those files are on disk now, and have to open.
    /// </summary>
    [Fact]
    public void SlotsWrittenAsBareNames_StillOpen()
    {
        Directory.CreateDirectory(_directory);

        File.WriteAllText(
            _file,
            @"{ ""Taskbar"": { ""Rows"": 1, ""Columns"": 2,"
            + @" ""Slots"": [ ""Download"", ""ProcessorLoad"" ] } }");

        TaskbarSettings taskbar = new SettingsStore(_file).Load().Taskbar;

        Assert.Equal(2, taskbar.Slots.Count);
        Assert.Equal(MeterField.Download, taskbar.Slot(0, 0));
        Assert.Equal(MeterField.ProcessorLoad, taskbar.Slot(0, 1));

        // And nothing was invented about how they look.
        Assert.Null(taskbar.At(0, 0)!.ValueColour);
        Assert.Null(taskbar.At(0, 0)!.Family);
    }

    [Fact]
    public void HowACellLooks_SurvivesBeingSaved()
    {
        var store = new SettingsStore(_file);
        var settings = new WindsockSettings();

        settings.Taskbar.Rows = 1;
        settings.Taskbar.Columns = 2;
        settings.Taskbar.Slots =
        [
            new MeterSlot
            {
                Field = MeterField.Download,
                Family = RateFamily.Bits,
                ValueColour = "#4cd97a",
                Bold = true,
            },
            new MeterSlot { Field = MeterField.ProcessorLoad },
        ];

        store.Save(settings);

        TaskbarSettings back = store.Load().Taskbar;

        Assert.Equal(RateFamily.Bits, back.At(0, 0)!.Family);
        Assert.Equal("#4cd97a", back.At(0, 0)!.ValueColour);
        Assert.True(back.At(0, 0)!.Bold);

        // And the cell beside it kept its own silence.
        Assert.Null(back.At(0, 1)!.Family);
        Assert.Null(back.At(0, 1)!.ValueColour);
    }

    [Fact]
    public void NoFileYet_MeansTheDefaults()
    {
        var store = new SettingsStore(_file);

        // A first run has nothing saved, and following Windows is the default.
        Assert.Equal(ThemePreference.System, store.Load().Theme);
    }

    [Fact]
    public void AFileThatIsNotSettings_FallsBackToTheDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_file, "{ this is not json");
        Assert.Equal(ThemePreference.System, new SettingsStore(_file).Load().Theme);
    }

    [Fact]
    public void AnUnknownValue_FallsBackToTheDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_file, """{ "Theme": "Sepia" }""");

        // Written by a version that knows a theme this one does not.
        Assert.Equal(ThemePreference.System, new SettingsStore(_file).Load().Theme);
    }

    [Fact]
    public void SettingsAreWrittenAsNames_SoTheFileCanBeReadAndEdited()
    {
        var store = new SettingsStore(_file);
        store.Save(new WindsockSettings { Theme = ThemePreference.Dark });

        string text = File.ReadAllText(_file);

        Assert.Contains("Dark", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingTwice_LeavesOneFileAndTheLastValue()
    {
        var store = new SettingsStore(_file);

        store.Save(new WindsockSettings { Theme = ThemePreference.Dark });
        store.Save(new WindsockSettings { Theme = ThemePreference.Light });

        Assert.Equal(ThemePreference.Light, store.Load().Theme);
        Assert.Single(Directory.GetFiles(_directory));
    }

    /// <summary>
    /// The switch is turned into cells as the file is opened, so nothing that
    /// reads the settings afterwards has to know there was ever a switch.
    /// </summary>
    [Fact]
    public void AFileSavedWithOneColourPerReading_OpensWithCellsSayingSo()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            _file,
            """
            {
              "Taskbar": {
                "Rows": 1,
                "Columns": 1,
                "Colouring": "ByGroup",
                "Slots": [ "Download" ]
              }
            }
            """);

        TaskbarSettings meter = new SettingsStore(_file).Load().Taskbar;

        Assert.Equal(MeterSlot.Own, meter.Slots[0].ValueColour);
        Assert.Equal(MeterColouring.Plain, meter.Colouring);
    }

    /// <summary>
    /// How a cell is marked is a choice like any other, so it has to come back
    /// the way it went in - and a file written before there was a choice has to
    /// open as what it meant, which is a picture.
    /// </summary>
    [Fact]
    public void HowACellIsMarked_SurvivesBeingSaved()
    {
        var store = new SettingsStore(_file);
        var settings = new WindsockSettings();

        settings.Taskbar.Slots =
        [
            new MeterSlot { Field = MeterField.Download, MarkAs = MeterMarkStyle.Letters },
            new MeterSlot { Field = MeterField.Upload },
        ];

        store.Save(settings);

        List<MeterSlot> opened = store.Load().Taskbar.Slots;

        Assert.Equal(MeterMarkStyle.Letters, opened[0].MarkAs);
        Assert.Equal(MeterMarkStyle.Icon, opened[1].MarkAs);
    }

    [Fact]
    public void AFileSavedBeforeMarksWereDrawn_OpensWithPictures()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            _file,
            """
            {
              "Taskbar": { "Rows": 1, "Columns": 1, "Slots": [ "Download" ] }
            }
            """);

        Assert.Equal(MeterMarkStyle.Icon, new SettingsStore(_file).Load().Taskbar.Slots[0].MarkAs);
    }

    [Fact]
    public void WhichEndAPartSitsAt_SurvivesBeingSaved()
    {
        var store = new SettingsStore(_file);
        var settings = new WindsockSettings();

        settings.Taskbar.Slots =
        [
            new MeterSlot { Field = MeterField.Download, MarkSide = MeterSide.Left },
            new MeterSlot { Field = MeterField.Upload },
        ];

        store.Save(settings);

        List<MeterSlot> opened = store.Load().Taskbar.Slots;

        Assert.Equal(MeterSide.Left, opened[0].MarkSide);
        Assert.Equal(1, opened[0].PartsOnTheLeft);
        Assert.Equal(MeterSide.Right, opened[1].MarkSide);
    }

    /// <summary>
    /// A meter saved when it could be wider. The slots are one flat list read
    /// against the shape, so reading five columns of them at three columns
    /// would put the fourth reading under the first.
    /// </summary>
    [Fact]
    public void AFileSavedWiderThanTheMeterMayBe_KeepsEachRowFromItsStart()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            _file,
            """
            {
              "Taskbar": {
                "Rows": 2,
                "Columns": 5,
                "Slots": [
                  "Download", "Upload", "ProcessorLoad", "MemoryLoad", "StorageLoad",
                  "GraphicsLoad", "ProcessorClock", "MemoryUsed", "StorageUsed", "NetworkTotal"
                ]
              }
            }
            """);

        TaskbarSettings meter = new SettingsStore(_file).Load().Taskbar;

        Assert.Equal(TaskbarSettings.MaximumColumns, meter.Columns);

        // The first three of each row, where their reader left them.
        Assert.Equal(MeterField.Download, meter.Slot(0, 0));
        Assert.Equal(MeterField.Upload, meter.Slot(0, 1));
        Assert.Equal(MeterField.ProcessorLoad, meter.Slot(0, 2));
        Assert.Equal(MeterField.GraphicsLoad, meter.Slot(1, 0));
        Assert.Equal(MeterField.ProcessorClock, meter.Slot(1, 1));
        Assert.Equal(MeterField.MemoryUsed, meter.Slot(1, 2));
    }

    [Fact]
    public void ABackgroundColour_SurvivesBeingSavedAndReadBack()
    {
        WindsockSettings settings = new();
        settings.Taskbar.Background = "#80123456";

        new SettingsStore(_file).Save(settings);
        WindsockSettings read = new SettingsStore(_file).Load();

        Assert.Equal("#80123456", read.Taskbar.Background);
    }

    [Fact]
    public void AMeterSavedWithNoBackground_ReadsBackWithNone()
    {
        new SettingsStore(_file).Save(new WindsockSettings());

        Assert.Null(new SettingsStore(_file).Load().Taskbar.Background);
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
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
