using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Windsock.Core.Apps;
using Windsock.Core.Connectivity;
using Windsock.Core.Formatting;
using Windsock.Core.History;
using Windsock.Core.Meter;
using Windsock.Core.Networking;
using Windsock.Core.Settings;
using Windsock.Core.Wifi;

namespace Windsock.App.ViewModels;

/// <summary>One of the busiest apps, as the hover card lists it.</summary>
/// <param name="Name">The app's name.</param>
/// <param name="Amount">What it moved, written out.</param>
public sealed record CardApp(string Name, string Amount);

/// <summary>
/// The card that opens when the pointer rests on the meter: the last minute,
/// today's totals, the connection, Wi-Fi and the busiest apps.
/// </summary>
/// <remarks>
/// Every monitor it reads is started when the card opens and let go when it
/// closes - the connection's checks, the Wi-Fi readings - and today's totals
/// are read once per opening, off the UI thread. Open, it refreshes once a
/// second. One card serves every meter: only one can be under the pointer.
/// </remarks>
public sealed partial class MeterCardViewModel : ObservableObject, IDisposable
{
    private const int AppsShown = 3;

    private readonly ThroughputMonitor _network;
    private readonly ConnectionMonitor _connection;
    private readonly IUsageHistoryStore _history;
    private readonly AppUsageCollector _apps;
    private readonly AppNames _names;
    private readonly WindsockSettings _settings;
    private readonly IServiceProvider _services;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clock;
    private readonly ThroughputSample[] _samples = new ThroughputSample[240];
    private readonly double[] _downRaw = new double[240];
    private readonly double[] _upRaw = new double[240];
    private IDisposable? _connectionWatch;
    private IDisposable? _wifiWatch;
    private WifiMonitor? _wifi;
    private int _opening;
    private bool _disposed;

    public MeterCardViewModel(
        ThroughputMonitor network,
        ConnectionMonitor connection,
        IUsageHistoryStore history,
        AppUsageCollector apps,
        AppNames names,
        WindsockSettings settings,
        IServiceProvider services,
        Dispatcher dispatcher)
    {
        _network = network;
        _connection = connection;
        _history = history;
        _apps = apps;
        _names = names;
        _settings = settings;
        _services = services;
        _dispatcher = dispatcher;

        _clock = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Refresh();
    }

    /// <summary>The download rate now.</summary>
    [ObservableProperty]
    public partial string Download { get; private set; } = MeterReadout.Unknown;

    /// <summary>The upload rate now.</summary>
    [ObservableProperty]
    public partial string Upload { get; private set; } = MeterReadout.Unknown;

    /// <summary>The fastest second of the last minute, both ways.</summary>
    [ObservableProperty]
    public partial string Peak { get; private set; } = string.Empty;

    /// <summary>The last minute's download, a value a second.</summary>
    public double[] DownloadMinute { get; } = new double[MeterHistory.Span];

    /// <summary>The last minute's upload, a value a second.</summary>
    public double[] UploadMinute { get; } = new double[MeterHistory.Span];

    /// <summary>How many seconds of the minute there are.</summary>
    [ObservableProperty]
    public partial int MinuteCount { get; private set; }

    /// <summary>Moved each second, which redraws the minute.</summary>
    [ObservableProperty]
    public partial int MinuteVersion { get; private set; }

    /// <summary>The top of the minute's scale, shared by both lines so they compare.</summary>
    [ObservableProperty]
    public partial double MinuteTop { get; private set; } = double.NaN;

    /// <summary>The connection in a line.</summary>
    [ObservableProperty]
    public partial string Connection { get; private set; } = string.Empty;

    /// <summary>Today's totals in a line, or empty until read.</summary>
    [ObservableProperty]
    public partial string Today { get; private set; } = string.Empty;

    /// <summary>The Wi-Fi link in a line, or empty when the PC is not on Wi-Fi.</summary>
    [ObservableProperty]
    public partial string Wifi { get; private set; } = string.Empty;

    /// <summary>The busiest apps of the last few minutes, by Windows' count.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CardApp> Apps { get; private set; } = [];

    /// <summary>What the apps line says when there are none to list.</summary>
    [ObservableProperty]
    public partial string AppsNote { get; private set; } = string.Empty;

    /// <summary>Every reading on the meter, in full, as the old tooltip had them.</summary>
    [ObservableProperty]
    public partial string Readings { get; private set; } = string.Empty;

    /// <summary>Starts the card's monitors and fills it, as it opens.</summary>
    public void Open(string readings)
    {
        if (_disposed)
        {
            return;
        }

        Readings = readings;
        _opening++;

        _connectionWatch ??= _connection.Watch();

        if (_wifiWatch is null && _services.GetService<WifiMonitor>() is { } wifi)
        {
            _wifi = wifi;
            _wifiWatch = wifi.Watch();
        }

        Refresh();
        _ = ReadTodayAsync(_opening);
        _clock.Start();
    }

    /// <summary>Lets every monitor go, as the card closes.</summary>
    public void Close()
    {
        _clock.Stop();
        _connectionWatch?.Dispose();
        _connectionWatch = null;
        _wifiWatch?.Dispose();
        _wifiWatch = null;
        _wifi = null;
    }

    /// <summary>Keeps the readings line current while the card is open.</summary>
    public void Update(string readings)
    {
        if (_clock.IsEnabled)
        {
            Readings = readings;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
    }

    private void Refresh()
    {
        RateFamily family = _settings.Taskbar.Family;
        RateScale scale = _settings.Taskbar.Scale;
        ThroughputSample latest = _network.Latest;

        Download = RateFormatter.Format(latest.DownloadBytesPerSecond, family, scale);
        Upload = RateFormatter.Format(latest.UploadBytesPerSecond, family, scale);

        Minute(family, scale);

        Connection = CardWords.Connection(_connection.State, _connection.Latest);

        WifiReading? wifi = _wifi?.Latest;
        Wifi = wifi is { IsConnected: true } link
            ? CardWords.Wifi(link.SignalPercent, Band(link.Band), link.Generation, link.ReceiveMbps)
            : string.Empty;

        Busiest();
    }

    /// <summary>The last minute from the samples the network monitor already keeps.</summary>
    private void Minute(RateFamily family, RateScale scale)
    {
        int count = _network.History.CopyTo(_samples);
        int needed = (int)Math.Ceiling(MeterHistory.Span / _network.Interval.TotalSeconds);
        int start = Math.Max(0, count - needed);
        int taken = count - start;

        for (int each = 0; each < taken; each++)
        {
            _downRaw[each] = _samples[start + each].DownloadBytesPerSecond;
            _upRaw[each] = _samples[start + each].UploadBytesPerSecond;
        }

        int seconds = CardWords.Seconds(_downRaw.AsSpan(0, taken), _network.Interval, DownloadMinute);
        CardWords.Seconds(_upRaw.AsSpan(0, taken), _network.Interval, UploadMinute);

        double top = 0;

        for (int each = 0; each < seconds; each++)
        {
            top = Math.Max(top, Math.Max(Finite(DownloadMinute[each]), Finite(UploadMinute[each])));
        }

        MinuteCount = seconds;
        MinuteTop = top > 0 ? top * 1.1 : double.NaN;
        Peak = top > 0
            ? string.Concat("Fastest second ", RateFormatter.Format(top, family, scale))
            : "Nothing has moved this minute";
        MinuteVersion++;
    }

    private void Busiest()
    {
        IReadOnlyDictionary<string, AppUsageRecord> recent = _apps.Recent;

        if (recent.Count == 0)
        {
            Apps = [];
            AppsNote = _apps.State == AppUsageState.Waiting
                ? "Windows has not counted the last few minutes yet."
                : "Nothing has moved in the last five minutes.";
            return;
        }

        IReadOnlyList<BusyApp> busiest = CardWords.Busiest(
            recent.Values.Select(record => new BusyApp(Name(record.AttributionId), record.BytesReceived + record.BytesSent)),
            AppsShown);

        Apps = [.. busiest.Select(app => new CardApp(app.Name, SizeFormatter.Format(app.Bytes)))];
        AppsNote = busiest.Count == 0 ? "Nothing has moved in the last five minutes." : string.Empty;
    }

    private string Name(string id) => id.Length == 0 ? "Not given to any app" : _names.Describe(id).Name;

    private async Task ReadTodayAsync(int opening)
    {
        DateTimeOffset from = new(DateTime.Today);

        try
        {
            UsageSummary today = await Task.Run(() => _history.Summarise(from, from.AddDays(1))).ConfigureAwait(true);

            if (opening == _opening)
            {
                Today = string.Concat("↓ ", SizeFormatter.Format(today.BytesDown), "   ↑ ", SizeFormatter.Format(today.BytesUp));
            }
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            Today = "The history could not be read";
        }
    }

    private static string Band(WifiBand band) => band switch
    {
        WifiBand.TwoPointFour => "2.4 GHz",
        WifiBand.Five => "5 GHz",
        WifiBand.Six => "6 GHz",
        _ => string.Empty,
    };

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
}
