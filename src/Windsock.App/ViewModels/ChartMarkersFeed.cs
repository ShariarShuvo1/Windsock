using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Alerts;
using Windsock.Core.History;
using Windsock.Core.Networking;

namespace Windsock.App.ViewModels;

/// <summary>
/// The outages, network changes and alerts in the stretch the live chart
/// shows, for it to mark.
/// </summary>
/// <remarks>
/// Read from <see cref="EventTimeline"/> when the chart starts and again when
/// either record changes, off the UI thread, and only while the chart is live:
/// a chart nobody can see reads nothing. Where each mark falls is the chart's
/// business, against the newest sample's time (<see cref="Latest"/>).
/// </remarks>
public sealed partial class ChartMarkersFeed : ObservableObject, IDisposable
{
    private readonly EventTimeline _timeline;
    private readonly ThroughputMonitor _monitor;
    private readonly Dispatcher _dispatcher;
    private bool _running;
    private bool _pending;
    private bool _disposed;
    private int _loads;

    public ChartMarkersFeed(EventTimeline timeline, ThroughputMonitor monitor, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _timeline = timeline;
        _monitor = monitor;
        _dispatcher = dispatcher;
    }

    /// <summary>The marks for the chart's stretch, oldest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TimelineMarker> Markers { get; private set; } = [];

    /// <summary>When the newest sample was taken: the chart's "now".</summary>
    public DateTimeOffset Latest => _monitor.Latest.Timestamp;

    /// <summary>Reads the marks, and keeps them current until stopped.</summary>
    public void Start()
    {
        if (_running || _disposed)
        {
            return;
        }

        _running = true;
        _timeline.Changed += OnTimelineChanged;
        _ = LoadAsync();
    }

    /// <summary>Stops following the records.</summary>
    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _timeline.Changed -= OnTimelineChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    // Raised on whichever thread wrote; one read is queued however many arrive.
    private void OnTimelineChanged(object? sender, EventArgs e)
    {
        if (_pending || !_running)
        {
            return;
        }

        _pending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _pending = false;

            if (_running)
            {
                _ = LoadAsync();
            }
        }));
    }

    private async Task LoadAsync()
    {
        int token = ++_loads;
        DateTimeOffset now = DateTimeOffset.Now;
        TimeSpan window = _monitor.Window;

        IReadOnlyList<TimelineMarker> marks = await Task.Run(() =>
        {
            try
            {
                return _timeline.Read(now - window - TimeSpan.FromMinutes(1), now + TimeSpan.FromMinutes(1));
            }
            catch (Exception ex) when (HistoryFailure.IsStorage(ex))
            {
                // No marks rather than no chart.
                return (IReadOnlyList<TimelineMarker>)[];
            }
        }).ConfigureAwait(true);

        if (token == _loads && !_disposed)
        {
            Markers = marks;
        }
    }
}
