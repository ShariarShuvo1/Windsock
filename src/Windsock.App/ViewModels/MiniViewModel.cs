using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Formatting;
using Windsock.Core.Meter;
using Windsock.Core.Networking;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>
/// The compact window: the rates and their last minute, nothing else.
/// </summary>
/// <remarks>
/// Reads the network monitor's own samples, which it keeps whether anything
/// watches or not, so the window costs nothing but its drawing. The minute is
/// redrawn once a second; the figures with each sample.
/// </remarks>
public sealed partial class MiniViewModel : ObservableObject, IDisposable
{
    private readonly ThroughputMonitor _network;
    private readonly WindsockSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clock;
    private readonly ThroughputSample[] _samples = new ThroughputSample[240];
    private readonly double[] _downRaw = new double[240];
    private readonly double[] _upRaw = new double[240];
    private bool _disposed;

    public MiniViewModel(ThroughputMonitor network, WindsockSettings settings, Dispatcher dispatcher)
    {
        _network = network;
        _settings = settings;
        _dispatcher = dispatcher;
        _network.SampleTaken += OnSample;

        _clock = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Minute();
        _clock.Start();

        Figures(_network.Latest);
        Minute();
    }

    /// <summary>The download rate now.</summary>
    [ObservableProperty]
    public partial string Download { get; private set; } = MeterReadout.Unknown;

    /// <summary>The upload rate now.</summary>
    [ObservableProperty]
    public partial string Upload { get; private set; } = MeterReadout.Unknown;

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

    /// <summary>The top of the minute's scale, shared by both lines.</summary>
    [ObservableProperty]
    public partial double MinuteTop { get; private set; } = double.NaN;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _clock.Stop();
        _network.SampleTaken -= OnSample;
    }

    private void OnSample(object? sender, ThroughputSample sample) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_disposed)
            {
                Figures(sample);
            }
        }));

    private void Figures(ThroughputSample sample)
    {
        TaskbarSettings meter = _settings.Taskbar;

        Download = RateFormatter.Format(sample.DownloadBytesPerSecond, meter.Family, meter.Scale);
        Upload = RateFormatter.Format(sample.UploadBytesPerSecond, meter.Family, meter.Scale);
    }

    private void Minute()
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
        MinuteVersion++;
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
}
