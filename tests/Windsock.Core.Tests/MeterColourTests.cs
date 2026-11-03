using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Windsock.Core.Formatting;
using Windsock.Core.Meter;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// A colour in a settings file is whoever wrote the file's. WPF's own colour
/// reader takes "ContextColor &lt;address&gt; ..." and fetches a colour profile
/// from that address, and throws on "sc#..."; so a colour that is not hex never
/// survives loading or importing, and nothing is ever fetched.
/// </summary>
public sealed class MeterColourTests
{
    [Theory]
    [InlineData("#abc", true)]
    [InlineData("#8abc", true)]
    [InlineData("#aabbcc", true)]
    [InlineData("#80aabbcc", true)]
    [InlineData("good", true)]
    [InlineData("own", true)]
    [InlineData("ContextColor http://127.0.0.1/profile.icc 1,0,0,0", false)]
    [InlineData("ContextColor file://server/share/profile.icc 1,0,0,0", false)]
    [InlineData("sc#1,2", false)]
    [InlineData("sc#1,0.5,0.5,0.5", false)]
    [InlineData("Red", false)]
    [InlineData("#ggg", false)]
    [InlineData("#12345", false)]
    public void OnlyHexAndTheNamedLevels_AreColours(string colour, bool valid)
    {
        Assert.Equal(valid, MeterLayoutFile.IsColour(colour));
    }

    [Fact]
    public void ASettingsFileWithHostileColours_LoadsWithNothingFetched_AndNothingThrown()
    {
        using Loopback listener = new();
        string context = string.Concat("ContextColor ", listener.Address, "profile.icc 1,0,0,0");

        string json = JsonSerializer.Serialize(new
        {
            Taskbar = new
            {
                Rows = 1,
                Columns = 2,
                Background = context,
                Slots = new object[]
                {
                    new
                    {
                        Field = "Download",
                        LabelColour = context,
                        ValueColour = "sc#1,2",
                        UnitColour = "#ffcc00",
                        Levels = new object[] { new { At = 5, Colour = context }, new { At = 9, Colour = "sc#1,0,0,0" } },
                    },
                    new { Field = "Upload", ValueColour = "own" },
                },
            },
        });

        string folder = Path.Combine(Path.GetTempPath(), "windsock-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            string file = Path.Combine(folder, "settings.json");
            File.WriteAllText(file, json);

            TaskbarSettings meter = new SettingsStore(file).Load().Taskbar;

            Assert.Null(meter.Background);
            Assert.Null(meter.Slots[0].LabelColour);
            Assert.Null(meter.Slots[0].ValueColour);
            Assert.Equal("#ffcc00", meter.Slots[0].UnitColour);
            Assert.All(meter.Slots[0].Levels!, level => Assert.Equal(MeterLevels.Fair, level.Colour));
            Assert.Equal(MeterSlot.Own, meter.Slots[1].ValueColour);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        Assert.Equal(0, listener.Requests);
    }

    [Fact]
    public void AnImportWithHostileColours_IsReadWithNothingFetched_AndNothingThrown()
    {
        using Loopback listener = new();
        string context = string.Concat("ContextColor ", listener.Address, "profile.icc 1,0,0,0");

        WindsockSettings theirs = new();
        theirs.Taskbar.Background = context;
        theirs.Taskbar.Slots[0].ValueColour = "sc#1,2";
        theirs.Taskbar.Slots[0].Levels = [new MeterLevel { At = 5, Colour = context }];

        SettingsImport read = SettingsTransfer.Read(SettingsTransfer.Export(theirs, DateTimeOffset.Now), new WindsockSettings());

        if (read.Settings is { } settings)
        {
            Assert.Null(settings.Taskbar.Background);
            Assert.Null(settings.Taskbar.Slots[0].ValueColour);
            Assert.Equal(MeterLevels.Fair, settings.Taskbar.Slots[0].Levels![0].Colour);
        }

        Assert.Equal(0, listener.Requests);
    }

    [Fact]
    public void Settling_HoldsAPlaceToTheLayoutFileRules()
    {
        TaskbarSettings meter = new() { Rows = 1, Columns = 1 };
        MeterSlot place = new()
        {
            Field = MeterField.Feed,
            Card = new string('c', 500),
            Levels = [.. Enumerable.Range(0, 20).Select(each => new MeterLevel { At = each })],
            Rotate = [MeterField.Upload, MeterField.ProcessorLoad, MeterField.MemoryLoad, MeterField.GraphicsLoad, MeterField.StorageLoad, MeterField.RouterPing, MeterField.TodayTotal, MeterField.MonthTotal],
            Feed = new MeterFeed { Address = "https://example.com/a.json", Path = "a..b", Unit = new string('u', 40), Label = new string('l', 40), Seconds = int.MaxValue },
        };

        meter.Slots = [place, new MeterSlot { Field = MeterField.Upload }, new MeterSlot { Field = MeterField.Download }];
        meter.Settle();

        Assert.Single(meter.Slots);
        Assert.Null(place.Card);
        Assert.Equal(MeterLevels.Most, place.Levels!.Count);
        Assert.Equal(MeterSlot.MostTurns, place.Rotate!.Count);
        Assert.Equal(string.Empty, place.Feed!.Path);
        Assert.Equal(MeterFeed.LongestText, place.Feed.Unit.Length);
        Assert.Equal(MeterFeed.LongestText, place.Feed.Label.Length);
        Assert.Equal(MeterFeed.LatestSeconds, place.Feed.Seconds);
    }

    [Fact]
    public void AnUndefinedReadingOrSide_IsPutBack()
    {
        TaskbarSettings meter = new() { Rows = 1, Columns = 1 };
        meter.Slots = [new MeterSlot { Field = (MeterField)999, MarkSide = (MeterSide)7, Graph = (MeterGraph)9, Family = (RateFamily)5 }];

        meter.Settle();

        Assert.Equal(MeterField.Empty, meter.Slots[0].Field);
        Assert.Equal(MeterSide.Right, meter.Slots[0].MarkSide);
        Assert.Equal(MeterGraph.None, meter.Slots[0].Graph);
        Assert.Null(meter.Slots[0].Family);
    }

    [Fact]
    public void AFileAskingForTwoThousandMillionColumns_LoadsWithoutOverflowing()
    {
        string folder = Path.Combine(Path.GetTempPath(), "windsock-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            string file = Path.Combine(folder, "settings.json");
            File.WriteAllText(file, """
                { "Taskbar": { "Rows": 2, "Columns": 2147483647, "Slots": [ "Download", "Upload", "ProcessorLoad" ] } }
                """);

            TaskbarSettings meter = new SettingsStore(file).Load().Taskbar;

            Assert.True(meter.Columns <= TaskbarSettings.MaximumColumns);
            Assert.True(meter.Slots.Count <= meter.Rows * meter.Columns);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A loopback listener that counts what is asked of it, on a free port.</summary>
    private sealed class Loopback : IDisposable
    {
        private readonly HttpListener _listener = new();
        private int _requests;

        public Loopback()
        {
            int port = FreePort();

            Address = string.Concat("http://127.0.0.1:", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "/");
            _listener.Prefixes.Add(Address);
            _listener.Start();
            _ = Task.Run(ListenAsync);
        }

        public string Address { get; }

        public int Requests
        {
            get
            {
                // A fetch that was started would have arrived by now.
                Thread.Sleep(200);
                return Volatile.Read(ref _requests);
            }
        }

        public void Dispose() => _listener.Close();

        private async Task ListenAsync()
        {
            try
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context = await _listener.GetContextAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref _requests);
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // Closed.
            }
        }

        private static int FreePort()
        {
            using TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
