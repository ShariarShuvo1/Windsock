using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Alerts;
using Windsock.Core.Connectivity;
using Windsock.Core.History;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>
/// The live figures the sidebar sets against its groups, each answering the
/// group's question at a glance.
/// </summary>
/// <remarks>
/// <para>
/// Taskbar shows what the meter shows - down and up - or Off while the meter is
/// off; Connection, Online with the last check's time, or Offline; Usage, what
/// today has come to; System, the processor's load. Network and each part of
/// the machine take their own tile's figure straight from
/// <see cref="SystemPanelViewModel"/>, as they always did.
/// </para>
/// <para>
/// Every figure here follows a reading something else already takes while the
/// window can be seen: the machine's, which the window watches, and the
/// connection's, which is watched in the background by default. Nothing is
/// watched for the sidebar's sake alone, so a figure nobody measured stays
/// empty rather than showing a guess.
/// </para>
/// </remarks>
public sealed partial class SidebarViewModel : ObservableObject, IDisposable
{
    private readonly SystemPanelViewModel _machine;
    private readonly ConnectionMonitor _connection;
    private readonly WindsockSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly IAlertLog _alerts;
    private bool _pending;
    private bool _countPending;
    private bool _disposed;

    public SidebarViewModel(
        SystemPanelViewModel machine,
        ConnectionMonitor connection,
        WindsockSettings settings,
        Dispatcher dispatcher,
        IAlertLog alerts)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(alerts);

        _machine = machine;
        _connection = connection;
        _settings = settings;
        _dispatcher = dispatcher;
        _alerts = alerts;
        _alerts.Changed += OnAlertsChanged;

        _machine.PropertyChanged += OnMachineChanged;
        _machine.Processor.PropertyChanged += OnProcessorChanged;
        _connection.StateChanged += OnConnectionChanged;
        _connection.Sampled += OnConnectionSampled;

        DescribeTaskbar();
        DescribeSystem();
        DescribeConnection();
        _ = CountAlertsAsync();
    }

    /// <summary>The count on the Alerts entry: alerts not yet read, empty for none.</summary>
    [ObservableProperty]
    public partial string AlertsBadge { get; private set; } = string.Empty;

    /// <summary>The same count in words, for a screen reader: "3 unread alerts", or empty.</summary>
    [ObservableProperty]
    public partial string AlertsStatus { get; private set; } = string.Empty;

    /// <summary>The meter's own reading in miniature, or Off while the meter is off.</summary>
    [ObservableProperty]
    public partial string Taskbar { get; private set; } = string.Empty;

    /// <summary>Online and how fast, Offline, or Sign-in needed.</summary>
    [ObservableProperty]
    public partial string Connection { get; private set; } = string.Empty;

    /// <summary>The processor's load, for the System group.</summary>
    [ObservableProperty]
    public partial string Machine { get; private set; } = string.Empty;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _machine.PropertyChanged -= OnMachineChanged;
        _machine.Processor.PropertyChanged -= OnProcessorChanged;
        _connection.StateChanged -= OnConnectionChanged;
        _connection.Sampled -= OnConnectionSampled;
        _alerts.Changed -= OnAlertsChanged;
    }

    // Raised on whichever thread wrote; one count is queued however many arrive.
    private void OnAlertsChanged(object? sender, EventArgs e)
    {
        if (_countPending || _disposed)
        {
            return;
        }

        _countPending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _countPending = false;
            _ = CountAlertsAsync();
        }));
    }

    /// <summary>Counts the unread alerts off the UI thread; an unreadable record shows no badge.</summary>
    private async Task CountAlertsAsync()
    {
        int unread = await Task.Run(() =>
        {
            try
            {
                return _alerts.UnreadCount();
            }
            catch (Exception ex) when (HistoryFailure.IsStorage(ex))
            {
                return 0;
            }
        }).ConfigureAwait(true);

        if (!_disposed)
        {
            AlertsBadge = AlertWords.Badge(unread);
            AlertsStatus = unread switch
            {
                <= 0 => string.Empty,
                1 => "1 unread alert",
                _ => string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{unread:N0} unread alerts"),
            };
        }
    }

    private void OnMachineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemPanelViewModel.RatesFigure))
        {
            DescribeTaskbar();
        }
    }

    private void OnProcessorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemTileViewModel.Readout))
        {
            DescribeSystem();
        }
    }

    // Raised on the monitor's own thread; one redraw is queued however many arrive.
    private void OnConnectionChanged(object? sender, ConnectionState state) => Queue();

    private void OnConnectionSampled(object? sender, ConnectionSample sample) => Queue();

    private void Queue()
    {
        if (_pending || _disposed)
        {
            return;
        }

        _pending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _pending = false;

            if (!_disposed)
            {
                DescribeConnection();
            }
        }));
    }

    /// <summary>
    /// Read again with every network reading, about once a second, which is
    /// also how a meter switched on or off on the Taskbar page shows here
    /// without the sidebar listening to that page.
    /// </summary>
    private void DescribeTaskbar() =>
        Taskbar = _settings.Taskbar.Enabled ? _machine.RatesFigure : "Off";

    private void DescribeSystem() =>
        Machine = _machine.Processor.Readout.Length > 0 ? string.Concat("CPU ", _machine.Processor.Readout) : string.Empty;

    private void DescribeConnection() =>
        Connection = ConnectionBrief.Of(_connection.State, _connection.Latest);
}
