using System.Net;
using Windsock.Core.Home;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class HomeStoreTests : IDisposable
{
    private static readonly DateTimeOffset Morning = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly MacAddress Printer = new(0x001122334455);
    private static readonly MacAddress Phone = new(0xDAA119000001);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "windsock-home-" + Guid.NewGuid().ToString("N"));

    private SqliteHomeStore Open() => new(Path.Combine(_folder, SqliteHomeStore.FileName));

    [Fact]
    public void FirstSighting_IsNew_AndOnlyOnce()
    {
        using SqliteHomeStore store = Open();

        IReadOnlyList<HomeDevice> first = store.Record("home", "Home", [new DeviceSighting(Printer, IPAddress.Parse("192.168.0.20"), null, false)], Morning);
        IReadOnlyList<HomeDevice> again = store.Record("home", "Home", [new DeviceSighting(Printer, IPAddress.Parse("192.168.0.21"), null, false)], Morning);
        IReadOnlyList<HomeDevice> later = store.Record("home", "Home", [new DeviceSighting(Printer, null, IPAddress.Parse("fe80::1"), false), new DeviceSighting(Phone, null, null, false)], Morning.AddHours(1));

        Assert.Equal(Printer, Assert.Single(first).Mac);
        Assert.Empty(again);
        Assert.Equal(Phone, Assert.Single(later).Mac);

        HomeDevice printer = store.Read("home").Single(each => each.Mac == Printer);
        Assert.Equal(Morning, printer.FirstSeen);
        Assert.Equal(Morning.AddHours(1), printer.LastSeen);
        Assert.Equal(IPAddress.Parse("192.168.0.21"), printer.Address);
        Assert.Equal(IPAddress.Parse("fe80::1"), printer.Address6);
    }

    [Fact]
    public void Devices_AreKeptPerNetwork()
    {
        using SqliteHomeStore store = Open();

        store.Record("home", "Home", [new DeviceSighting(Printer, null, null, false)], Morning);
        IReadOnlyList<HomeDevice> elsewhere = store.Record("cottage", "Cottage", [new DeviceSighting(Printer, null, null, false)], Morning);

        Assert.Single(elsewhere);
        Assert.Single(store.Read("home"));
        Assert.Single(store.Read("cottage"));
    }

    [Fact]
    public void Names_Nicknames_AndForgetting()
    {
        using SqliteHomeStore store = Open();
        store.Record("home", "Home", [new DeviceSighting(Printer, null, null, false)], Morning);

        store.Name("home", Printer, "HP0A1B2C", DeviceNameSource.Multicast, Morning);
        store.Nickname("home", Printer, "  Office printer ");

        HomeDevice named = Assert.Single(store.Read("home"));
        Assert.Equal("HP0A1B2C", named.FoundName);
        Assert.Equal(DeviceNameSource.Multicast, named.NameSource);
        Assert.Equal("Office printer", named.Name);

        store.Nickname("home", Printer, "");
        Assert.Equal("HP0A1B2C", Assert.Single(store.Read("home")).Name);

        Assert.True(store.Forget("home", Printer));
        Assert.Empty(store.Read("home"));
        Assert.Single(store.Record("home", "Home", [new DeviceSighting(Printer, null, null, false)], Morning.AddDays(1)));
    }

    [Fact]
    public void Baseline_IsSetOnce()
    {
        using SqliteHomeStore store = Open();

        Assert.Null(store.Baseline("home"));
        store.SetBaseline("home", Morning);
        store.SetBaseline("home", Morning.AddDays(3));

        Assert.Equal(Morning, store.Baseline("home"));
    }

    [Fact]
    public void Erasing_TakesDevicesFirstSeenInThePeriod()
    {
        using SqliteHomeStore store = Open();
        store.Record("home", "Home", [new DeviceSighting(Printer, null, null, false)], Morning);
        store.Record("home", "Home", [new DeviceSighting(Phone, null, null, false)], Morning.AddDays(2));
        store.WriteRouterReport("home", Morning.AddDays(2), "{}");

        store.Delete(Morning.AddDays(1), Morning.AddDays(3));

        Assert.Equal(Printer, Assert.Single(store.Read("home")).Mac);
        Assert.Null(store.ReadRouterReport("home"));

        store.RemoveEverything();
        Assert.Empty(store.Read("home"));
        Assert.Null(store.Baseline("home"));
    }

    [Fact]
    public void RouterReports_AreKeptPerNetwork()
    {
        using SqliteHomeStore store = Open();

        store.WriteRouterReport("home", Morning, "{\"a\":1}");
        store.WriteRouterReport("home", Morning.AddDays(7), "{\"a\":2}");

        (DateTimeOffset at, string report) = store.ReadRouterReport("home")!.Value;
        Assert.Equal(Morning.AddDays(7), at);
        Assert.Equal("{\"a\":2}", report);
    }

    /// <summary>Looking at the page with the features off reads the store; that must not make the file.</summary>
    [Fact]
    public void Reading_ANeverMadeStore_MakesNoFile()
    {
        string path = Path.Combine(_folder, SqliteHomeStore.FileName);
        using SqliteHomeStore store = new(path);

        Assert.Empty(store.Read("home"));
        Assert.Null(store.Baseline("home"));
        Assert.Null(store.ReadRouterReport("home"));
        Assert.False(store.Forget("home", Printer));
        store.Nickname("home", Printer, "Printer");
        Assert.Equal(0, store.Delete(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        Assert.Equal(0, store.RemoveEverything());
        Assert.False(File.Exists(path));

        store.Record("home", "Home", [new DeviceSighting(Printer, null, null, false)], Morning);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AFileThatCannotBeOpened_FailsEachCall_NotConstruction()
    {
        Directory.CreateDirectory(_folder);
        string directory = Path.Combine(_folder, "is-a-folder");
        Directory.CreateDirectory(directory);

        using SqliteHomeStore store = new(directory);
        Assert.Throws<InvalidOperationException>(() => store.Record("home", "Home", [new DeviceSighting(Printer, null, null, false)], Morning));
        Assert.Throws<InvalidOperationException>(() => store.Read("home"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
