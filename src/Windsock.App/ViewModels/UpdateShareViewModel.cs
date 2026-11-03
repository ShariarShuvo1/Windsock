using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Formatting;
using Windsock.Core.Insight;

namespace Windsock.App.ViewModels;

/// <summary>
/// The Apps page's band on Windows Update and Delivery Optimization this
/// month: Delivery Optimization's own count, Windows' count of the update
/// programs it names, and in the full edition what the trace measured of the
/// update services.
/// </summary>
/// <remarks>
/// Read when the band comes into view, and again after a minute if it stays
/// there; it reads WMI and the history file, so never on the window's thread.
/// </remarks>
public sealed partial class UpdateShareViewModel : ObservableObject
{
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(1);

    private readonly WindowsUpdateShare _share;
    private readonly Dispatcher _dispatcher;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;
    private int _reading;

    public UpdateShareViewModel(WindowsUpdateShare share, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _share = share;
        _dispatcher = dispatcher;
    }

    /// <summary>Downloaded from Microsoft this month.</summary>
    [ObservableProperty]
    public partial string FromMicrosoft { get; private set; } = "—";

    /// <summary>Downloaded from other PCs and cache servers on the same network.</summary>
    [ObservableProperty]
    public partial string FromNearby { get; private set; } = "—";

    /// <summary>Downloaded from other PCs over the internet.</summary>
    [ObservableProperty]
    public partial string FromInternetPeers { get; private set; } = "—";

    /// <summary>Uploaded to other PCs.</summary>
    [ObservableProperty]
    public partial string Uploaded { get; private set; } = "—";

    /// <summary>What crossed the internet connection, for a data plan.</summary>
    [ObservableProperty]
    public partial string OverInternet { get; private set; } = "—";

    /// <summary>Windows' count of the update programs it names, both directions.</summary>
    [ObservableProperty]
    public partial string Programs { get; private set; } = "—";

    /// <summary>The update services as the trace measured them, or empty where it never ran this month.</summary>
    [ObservableProperty]
    public partial string Measured { get; private set; } = string.Empty;

    /// <summary>What the figures are, or why one is missing.</summary>
    [ObservableProperty]
    public partial string Note { get; private set; } = string.Empty;

    /// <summary>Reads the month again, when the last reading is a minute old.</summary>
    public void Refresh()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (now - _readAt < Freshness || Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        {
            return;
        }

        _readAt = now;

        _ = Task.Run(() =>
        {
            try
            {
                UpdateShareReport report = _share.Read();
                _dispatcher.BeginInvoke(() => Show(report));
            }
            finally
            {
                Interlocked.Exchange(ref _reading, 0);
            }
        });
    }

    private void Show(UpdateShareReport report)
    {
        string month = report.MonthStart.ToString("MMMM", CultureInfo.CurrentCulture);

        if (report.DeliveryOptimization is { } delivery)
        {
            FromMicrosoft = SizeFormatter.Format(delivery.FromMicrosoft);
            FromNearby = SizeFormatter.Format(delivery.FromLocalPeers + delivery.FromCacheServers);
            FromInternetPeers = SizeFormatter.Format(delivery.FromInternetPeers);
            Uploaded = SizeFormatter.Format(delivery.Uploaded);
            OverInternet = SizeFormatter.Format(delivery.OverInternet);
        }
        else
        {
            FromMicrosoft = FromNearby = FromInternetPeers = Uploaded = OverInternet = "—";
        }

        Programs = SizeFormatter.Format(report.ProgramsReceived + report.ProgramsSent);

        Measured = report.MeasuredServices is { Count: > 0 } services
            ? string.Format(
                CultureInfo.CurrentCulture,
                "Measured by Windsock as administrator: {0} for the update services on the days it ran.",
                SizeFormatter.Format(services.Sum(each => each.BytesTotal)))
            : string.Empty;

        Note = report.DeliveryOptimization is null
            ? "Delivery Optimization did not answer, so only the update programs Windows names are counted."
            : string.Format(
                CultureInfo.CurrentCulture,
                "Since 1 {0}, as Delivery Optimization counts it - the same figures as Windows' Activity monitor. Windows files the update services' own traffic under no app, so the table below cannot show it.",
                month);
    }
}
