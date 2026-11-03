using System.Text;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public void TheSidebar_OpensNamedUntilItIsFolded()
    {
        var store = new SettingsStore(_file);

        Assert.True(new WindsockSettings().SidebarOpen);

        store.Save(new WindsockSettings { SidebarOpen = false });

        Assert.False(store.Load().SidebarOpen);
    }

    [Fact]
    public void EveryOutsideContact_IsOffUntilAskedFor()
    {
        var settings = new WindsockSettings();

        Assert.False(settings.TraceRoutes);
        Assert.False(settings.ReverseLookups);
        Assert.Equal(InternetCheck.WindowsOnly, settings.Connection.InternetCheck);
    }

    [Fact]
    public void RouteTracing_SurvivesBeingSaved()
    {
        var store = new SettingsStore(_file);

        store.Save(new WindsockSettings { TraceRoutes = true, ReverseLookups = true });

        WindsockSettings read = store.Load();

        Assert.True(read.TraceRoutes);
        Assert.True(read.ReverseLookups);
    }

    /// <summary>
    /// The old route switch was on by default and sat under a second one on the
    /// map. Every file written before the two became one says true, so reading
    /// it as the new switch would start tracing for people who never asked.
    /// </summary>
    [Fact]
    public void AFileWithTheOldRouteSwitch_LeavesTracingOff()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_file, """{ "FollowRoutes": true, "ProcessMapOpen": true, "Theme": "Dark" }""");

        WindsockSettings loaded = new SettingsStore(_file).Load();

        Assert.False(loaded.TraceRoutes);
        Assert.Equal(ThemePreference.Dark, loaded.Theme);
    }

    [Fact]
    public void ChartUnits_SurviveBeingSaved()
    {
        var store = new SettingsStore(_file);

        Assert.Equal(RateFamily.Bytes, new WindsockSettings().ChartFamily);
        Assert.Equal(RateScale.Auto, new WindsockSettings().ChartScale);

        store.Save(new WindsockSettings { ChartFamily = RateFamily.Bits, ChartScale = RateScale.Mega });

        WindsockSettings read = store.Load();

        Assert.Equal(RateFamily.Bits, read.ChartFamily);
        Assert.Equal(RateScale.Mega, read.ChartScale);
    }

    [Fact]
    public void AFileSavedBeforeTheSidebar_OpensItNamed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_file, """{ "Theme": "Dark", "OnClose": "KeepRunning" }""");

        WindsockSettings loaded = new SettingsStore(_file).Load();

        Assert.Equal(ThemePreference.Dark, loaded.Theme);
        Assert.True(loaded.SidebarOpen);
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

    /// <summary>
    /// Something holding the file open for writing - an editor, a backup - used
    /// to make the settings read as the defaults, since a read wanted it shared
    /// for reading only. The store's read lets others write while it copies.
    /// </summary>
    [Fact]
    public void AFileHeldOpenForWriting_StillReads()
    {
        var store = new SettingsStore(_file);
        store.Save(new WindsockSettings { Theme = ThemePreference.Dark });

        using FileStream writer = new(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.Equal(ThemePreference.Dark, store.Load().Theme);
    }

    /// <summary>
    /// A second Windsock reads the file while the running one may save it. The
    /// read holds the file only while it copies it; a save that meets that hold,
    /// or anyone else's, waits and tries again rather than losing what it was
    /// saving. Two holders: the store's own read, and a reader sharing it for
    /// reading only, as an older Windsock does.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASaveFindingTheFileHeld_TriesAgain(bool heldByTheStoresRead)
    {
        var store = new SettingsStore(_file);
        store.Save(new WindsockSettings { Theme = ThemePreference.Dark });

        FileStream held = heldByTheStoresRead
            ? SettingsStore.OpenShared(_file)
            : new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.Read);
        List<int> retries = [];
        store.BeforeRetry = retry =>
        {
            retries.Add(retry);
            held.Dispose();
        };

        try
        {
            store.Save(new WindsockSettings { Theme = ThemePreference.Light });
        }
        finally
        {
            held.Dispose();
        }

        Assert.Equal([1], retries);
        Assert.Equal(ThemePreference.Light, store.Load().Theme);
    }

    /// <summary>Held for good, the save gives up after its tries, says so once, and leaves the file as it was.</summary>
    [Fact]
    public void ASaveThatNeverGetsTheFile_GivesUpAndSaysSo()
    {
        var log = new WarningCounter();
        var store = new SettingsStore(_file, log);
        store.Save(new WindsockSettings { Theme = ThemePreference.Dark });
        int retries = 0;
        store.BeforeRetry = _ => retries++;

        using (new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.Save(new WindsockSettings { Theme = ThemePreference.Light });
        }

        Assert.Equal(SettingsStore.Retries, retries);
        Assert.Equal(1, log.Warnings);
        Assert.Equal(ThemePreference.Dark, store.Load().Theme);
    }

    /// <summary>A file saved by Notepad starts with a byte order mark, and has always opened.</summary>
    [Fact]
    public void AFileWithAByteOrderMark_StillOpens()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_file, """{ "Theme": "Dark" }""", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(0xEF, File.ReadAllBytes(_file)[0]);
        Assert.Equal(ThemePreference.Dark, new SettingsStore(_file).Load().Theme);
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

    private sealed class WarningCounter : ILogger<SettingsStore>
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning && exception is not null)
            {
                Warnings++;
            }
        }
    }
}
