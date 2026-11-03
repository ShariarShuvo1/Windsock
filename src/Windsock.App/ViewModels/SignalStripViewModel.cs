using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Wifi;

namespace Windsock.App.ViewModels;

/// <summary>
/// The Wi-Fi signal drawn under the Overview's throughput chart, so a slow
/// moment can be seen to line up with a weak signal. Shown only while Wi-Fi
/// is connected; watched only while the chart is.
/// </summary>
public sealed partial class SignalStripViewModel(WifiMonitor wifi, Dispatcher dispatcher) : ObservableObject, IDisposable
{
    private static readonly TimeSpan Kept = TimeSpan.FromMinutes(30);

    private IDisposable? _watch;
    private int _queued;

    /// <summary>Whether Wi-Fi is connected, so there is a signal to draw.</summary>
    [ObservableProperty]
    public partial bool IsShowing { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<SignalPoint> Points { get; private set; } = [];

    [ObservableProperty]
    public partial int Revision { get; private set; }

    /// <summary>"Wi-Fi -58 dBm", for the strip's left edge.</summary>
    [ObservableProperty]
    public partial string Label { get; private set; } = string.Empty;

    /// <summary>The strip in a sentence, for screen readers.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>Watches the Wi-Fi while the chart above can be seen, and stops when it cannot.</summary>
    public void Follow(bool seen)
    {
        if (seen && _watch is null)
        {
            wifi.Sampled += OnSampled;
            _watch = wifi.Watch();
            return;
        }

        if (!seen && _watch is not null)
        {
            wifi.Sampled -= OnSampled;
            _watch.Dispose();
            _watch = null;
        }
    }

    private void OnSampled(object? sender, WifiReading reading)
    {
        if (Interlocked.Exchange(ref _queued, 1) == 0)
        {
            dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _queued, 0);
                Apply(wifi.Latest);
            });
        }
    }

    private void Apply(WifiReading reading)
    {
        IsShowing = reading.IsConnected;
        Points = wifi.History.Since(DateTimeOffset.Now - Kept);
        Revision++;
        Label = reading.Rssi is { } rssi ? string.Create(CultureInfo.CurrentCulture, $"Wi-Fi {rssi} dBm") : "Wi-Fi";
        Summary = reading.Rssi is { } now
            ? string.Concat("Wi-Fi signal on the chart's time axis; now ", NetworkWords.Signal(now), ".")
            : "Wi-Fi signal on the chart's time axis.";
    }

    public void Dispose() => Follow(false);
}
