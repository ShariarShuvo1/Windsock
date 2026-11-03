using Microsoft.Extensions.Logging.Abstractions;
using Windsock.Core.Insight;
using Windsock.Core.Processes;
using Windsock.Core.Security;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class NewAppWatcherTests : IDisposable
{
    private const string Browser = @"\device\harddiskvolume3\program files\browser\browser.exe";
    private const string Editor = @"\device\harddiskvolume3\program files\editor\editor.exe";
    private const string Updater = @"\device\harddiskvolume3\program files\updater\updater.exe";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "windsock-seen-" + Guid.NewGuid().ToString("N"));
    private readonly Connections _connections = new();
    private readonly Traffic _traffic = new();
    private readonly Dictionary<int, string> _apps = [];
    private readonly List<string> _counted = [];
    private readonly SignatureBook _signatures = new(new SayTrusted(), _ => new FileStamp(1, 1));

    public NewAppWatcherTests() => Directory.CreateDirectory(_folder);

    private string SeenPath => Path.Combine(_folder, "seen-apps.txt");

    [Fact]
    public void TheFirstRunLearnsWhatIsInUseWithoutCallingItNew()
    {
        Connect(100, Browser, "142.250.187.196");

        NewAppWatcher watcher = Watcher(new SeenApps(SeenPath));
        List<NewAppSighting> sighted = Listen(watcher);

        Assert.True(watcher.IsLearning);
        watcher.Pass();

        Assert.Empty(sighted);
        Assert.False(watcher.IsLearning);
        Assert.True(File.Exists(SeenPath));
        Assert.Contains(Browser, File.ReadAllLines(SeenPath));

        // Once learned, a program that connects is new.
        Connect(200, Editor, "2606:4700::6810:84e5");
        watcher.Pass();

        NewAppSighting sighting = Assert.Single(sighted);
        Assert.Equal(Editor, sighting.AttributionId);
        Assert.Equal("2606:4700::6810:84e5", sighting.RemoteAddress);
        Assert.Equal("Someone", sighting.PublisherText);

        // And only once.
        watcher.Pass();
        Assert.Single(sighted);
        Assert.Single(watcher.Recent);
    }

    [Fact]
    public void NothingIsReadWhileNobodyWantsToHearAndTheReturnLearnsQuietly()
    {
        File.WriteAllLines(SeenPath, [Browser]);
        bool wanted = false;
        NewAppWatcher watcher = Watcher(new SeenApps(SeenPath), () => wanted);
        List<NewAppSighting> sighted = Listen(watcher);

        // Off: the tables are never read, so an app used now is not noted.
        Connect(200, Editor, "2606:4700::6810:84e5");
        watcher.Look();
        Assert.Equal(0, _connections.Reads);
        Assert.Empty(sighted);

        // Switched on, the first look learns what was used meanwhile quietly.
        wanted = true;
        watcher.Look();
        Assert.True(_connections.Reads > 0);
        Assert.Empty(sighted);
        Assert.Contains(Editor, File.ReadAllLines(SeenPath));

        // After that, a new app is new again.
        Connect(300, Updater, "1.1.1.1");
        watcher.Look();
        Assert.Equal(Updater, Assert.Single(sighted).AttributionId);
    }

    [Fact]
    public void AnAppSeenInAnEarlierRunIsNotNew()
    {
        File.WriteAllLines(SeenPath, [Browser]);
        Connect(100, Browser, "142.250.187.196");

        NewAppWatcher watcher = Watcher(new SeenApps(SeenPath));
        List<NewAppSighting> sighted = Listen(watcher);

        Assert.False(watcher.IsLearning);
        watcher.Pass();

        Assert.Empty(sighted);
    }

    [Fact]
    public void AnAppWindowsCountedIsNotNew()
    {
        File.WriteAllLines(SeenPath, [Browser]);
        _counted.Add(Updater);

        NewAppWatcher watcher = Watcher(new SeenApps(SeenPath));
        List<NewAppSighting> sighted = Listen(watcher);

        Connect(300, Updater, "13.107.4.50");
        watcher.Pass();

        Assert.Empty(sighted);
        Assert.Contains(Updater, File.ReadAllLines(SeenPath));
    }

    [Fact]
    public void BytesTheTraceCountedAreUseToo()
    {
        File.WriteAllLines(SeenPath, [Browser]);
        _apps[400] = Editor;
        _traffic.Bytes[400] = new ProcessTraffic(10, 20, 2);

        NewAppWatcher watcher = Watcher(new SeenApps(SeenPath));
        List<NewAppSighting> sighted = Listen(watcher);
        watcher.Pass();

        NewAppSighting sighting = Assert.Single(sighted);
        Assert.Null(sighting.RemoteAddress);
    }

    [Fact]
    public void LoopbackListenersAndUnnamedProcessesAreNotUse()
    {
        File.WriteAllLines(SeenPath, [Browser]);
        Connect(500, Editor, "127.0.0.1");
        _connections.All.Add(new ProcessConnection(501, TransportProtocol.Tcp, false, "0.0.0.0", 8080, "0.0.0.0", 0, TcpConnectionState.Listening, null));
        _apps[501] = Updater;

        // A protected service host: Windows names no file for it.
        _connections.All.Add(new ProcessConnection(502, TransportProtocol.Tcp, false, "192.168.1.5", 50000, "13.107.4.50", 443, TcpConnectionState.Established, null));

        NewAppWatcher watcher = Watcher(new SeenApps(SeenPath));
        List<NewAppSighting> sighted = Listen(watcher);
        watcher.Pass();

        Assert.Empty(sighted);
    }

    [Fact]
    public void ErasingEverythingLearnsAgainQuietly()
    {
        File.WriteAllLines(SeenPath, [Browser]);
        NewAppWatcher watcher = Watcher(new SeenApps(SeenPath));
        List<NewAppSighting> sighted = Listen(watcher);

        watcher.ForgetAll();
        Assert.False(File.Exists(SeenPath));
        Assert.True(watcher.IsLearning);

        Connect(100, Browser, "142.250.187.196");
        Connect(200, Editor, "142.250.187.197");
        watcher.Pass();

        Assert.Empty(sighted);
        Assert.Equal(2, File.ReadAllLines(SeenPath).Length);
    }

    [Fact]
    public void AnUnreadableListIsAFreshOne()
    {
        SeenApps seen = new(Path.Combine(_folder, "missing", "seen.txt"));

        Assert.False(seen.Existed);
        Assert.True(seen.Add("a"));
        Assert.False(seen.Add("A"));
        Assert.Equal(1, seen.AddRange(["a", "b", "", "B"]));
        Assert.True(seen.Save());
        Assert.True(new SeenApps(Path.Combine(_folder, "missing", "seen.txt")).Contains("b"));
    }

    [Fact]
    public void TheNoticeNamesTheAppItsSignerAndWhereItWent()
    {
        DateTimeOffset at = DateTimeOffset.UnixEpoch;

        Notifications.Notice signed = NewAppNotices.For(new NewAppSighting(
            Editor, "Editor", null, 1, "1.1.1.1", new SignatureInfo(SignatureState.Trusted, "Contoso", SignatureSource.Embedded, 0), at));

        Assert.Equal(Notifications.NoticeTopics.NewApp, signed.Topic);
        Assert.Equal("Editor used the network for the first time, talking to 1.1.1.1. Signed by Contoso.", signed.Message);
        Assert.Equal("apps", signed.Opens);
        Assert.True(signed.Quiet);
        Assert.NotNull(Alerts.AlertCatalogue.Find(signed.Topic));

        Notifications.Notice unsigned = NewAppNotices.For(new NewAppSighting(
            Editor, "Editor", null, 1, null, new SignatureInfo(SignatureState.NoSignature, null, null, 0), at));

        Assert.Equal("Editor used the network for the first time. It is not signed, so nothing says who made it.", unsigned.Message);
        Assert.False(unsigned.Quiet);
    }

    public void Dispose()
    {
        _signatures.Dispose();

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Connect(int processId, string app, string remote)
    {
        _apps[processId] = app;
        _connections.All.Add(new ProcessConnection(
            processId,
            TransportProtocol.Tcp,
            remote.Contains(':', StringComparison.Ordinal),
            "192.168.1.5",
            50000 + processId,
            remote,
            443,
            TcpConnectionState.Established,
            null));
    }

    private NewAppWatcher Watcher(SeenApps seen, Func<bool>? wanted = null) => new(
        _connections,
        _traffic,
        new Identities(),
        (_, _) => _counted,
        history: null,
        _signatures,
        seen,
        processId => _apps.GetValueOrDefault(processId),
        new InsightClock(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero)),
        NullLogger<NewAppWatcher>.Instance,
        wanted);

    private static List<NewAppSighting> Listen(NewAppWatcher watcher)
    {
        List<NewAppSighting> sighted = [];
        watcher.Sighted += (_, sighting) => sighted.Add(sighting);
        return sighted;
    }

    private sealed class Connections : IProcessConnectionDetailSource
    {
        public List<ProcessConnection> All { get; } = [];

        public int Reads { get; private set; }

        public IReadOnlyList<ProcessConnection> Read(int processId)
        {
            Reads++;
            return [.. All.Where(each => each.ProcessId == processId)];
        }

        public IReadOnlyList<ProcessConnection> ReadAll()
        {
            Reads++;
            return All;
        }
    }

    private sealed class Traffic : IProcessTrafficSource
    {
        public Dictionary<int, ProcessTraffic> Bytes { get; } = [];

        public TrafficSourceState State => TrafficSourceState.Running;

        public string? Detail => null;

        public string? Explanation => null;

        public void Start()
        {
        }

        public void CopyTo(Dictionary<int, ProcessTraffic> destination)
        {
            destination.Clear();

            foreach ((int id, ProcessTraffic traffic) in Bytes)
            {
                destination[id] = traffic;
            }
        }
    }

    private sealed class Identities : IProcessIdentityResolver
    {
        public ProcessIdentity Resolve(int processId) =>
            new("program", "A program", @"C:\Program Files\program.exe", null);

        public void Forget(int processId)
        {
        }
    }

    private sealed class SayTrusted : ISignatureVerifier
    {
        public SignatureInfo Verify(string path) => new(SignatureState.Trusted, "Someone", SignatureSource.Embedded, 0);
    }
}
