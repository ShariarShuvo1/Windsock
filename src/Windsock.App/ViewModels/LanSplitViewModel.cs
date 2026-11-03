using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
#if !STORE
using Microsoft.Extensions.DependencyInjection;
using Windsock.Core.Formatting;
using Windsock.Core.Timeline;
#endif

namespace Windsock.App.ViewModels;

/// <summary>
/// The live split of the network chart's traffic: what stayed on the local
/// network and what crossed the internet connection, each second.
/// </summary>
/// <remarks>
/// <para>
/// Only the kernel trace can tell the two apart - it sees each packet's far
/// end - so this is the full edition's, while it runs as administrator. The
/// Store edition has no trace and shows nothing here.
/// </para>
/// <para>
/// The trace counts payload, not headers, so the two together read a little
/// under the chart's figure, which is the adapter's. The figures say so on
/// hover rather than be stretched to fit.
/// </para>
/// </remarks>
public sealed partial class LanSplitViewModel : ObservableObject, IDisposable
{
#if !STORE
    private readonly ITrafficSplitSource? _source;
    private readonly ThroughputOverviewViewModel _units;
    private readonly DispatcherTimer _timer;
    private TrafficSplit _last;
    private long _lastAt;
#endif

    public LanSplitViewModel(ThroughputOverviewViewModel units, Dispatcher dispatcher, IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(provider);

#if !STORE
        _units = units;
        _source = provider.GetService<ITrafficSplitSource>();
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Sample();
#endif
    }

    /// <summary>Whether the split is being measured, so the line is worth showing.</summary>
    [ObservableProperty]
    public partial bool IsMeasuring { get; private set; }

    /// <summary>"↓ 1.2 MB/s  ↑ 40 KB/s" on the local network.</summary>
    [ObservableProperty]
    public partial string Local { get; private set; } = "—";

    /// <summary>The same over the internet connection.</summary>
    [ObservableProperty]
    public partial string Internet { get; private set; } = "—";

    /// <summary>Begins sampling, while the chart can be seen.</summary>
    public void Start()
    {
#if !STORE
        if (_source is null || _timer.IsEnabled)
        {
            return;
        }

        _last = _source.ReadSplit();
        _lastAt = Environment.TickCount64;
        _timer.Start();
#else
        IsMeasuring = false;
#endif
    }

    /// <summary>Stops sampling with the window's scope, so a running timer never holds it.</summary>
    public void Dispose() => Stop();

    /// <summary>Stops sampling.</summary>
    public void Stop()
    {
#if !STORE
        _timer.Stop();
#endif
        IsMeasuring = false;
    }

#if !STORE
    private void Sample()
    {
        if (_source is null)
        {
            return;
        }

        long now = Environment.TickCount64;
        TrafficSplit totals = _source.ReadSplit();
        double seconds = (now - _lastAt) / 1000d;
        TrafficSplit moved = totals.Since(_last);
        _last = totals;
        _lastAt = now;

        IsMeasuring = _source.CanAttribute;

        if (!IsMeasuring || seconds <= 0)
        {
            return;
        }

        RateFamily family = _units.SelectedFamily;
        Local = Pair(moved.LocalReceived / seconds, moved.LocalSent / seconds, family);
        Internet = Pair(moved.InternetReceived / seconds, moved.InternetSent / seconds, family);
    }

    private static string Pair(double down, double up, RateFamily family) => string.Concat(
        "↓ ",
        RateFormatter.Format(down, family, RateScale.Auto),
        "   ↑ ",
        RateFormatter.Format(up, family, RateScale.Auto));
#endif
}
