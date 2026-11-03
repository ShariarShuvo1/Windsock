using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.App.Controls;
using Windsock.Core.Home;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>
/// The whole household's traffic, from the router's own counters, as two
/// figures on Network › Overview beside today's totals.
/// </summary>
/// <remarks>
/// A figure, not a series on the chart: the router is asked every five
/// seconds, which is too coarse to draw beside a reading every second, and the
/// chart is several other features' too. Shown only while
/// <see cref="HomeSettings.ReadRouterTraffic"/> is on; the router is asked only
/// while a figure is on screen (<see cref="WhileVisible"/>). A router that does
/// not report its counters shows a dash and says so, never a zero.
/// </remarks>
public sealed partial class HouseholdFigureViewModel : ObservableObject, IFollowsVisibility, IDisposable
{
    private readonly HouseholdTraffic _household;
    private readonly WindsockSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly Lock _gate = new();

    private int _showing;
    private IDisposable? _watch;
    private bool _disposed;

    public HouseholdFigureViewModel(HouseholdTraffic household, WindsockSettings settings, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(household);

        _household = household;
        _settings = settings;
        _dispatcher = dispatcher;
        _household.Changed += OnChanged;
        Read();
    }

    /// <summary>Whether the figures are shown at all: the setting is on.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; private set; }

    /// <summary>What every device together is receiving, or a dash.</summary>
    [ObservableProperty]
    public partial string Download { get; private set; } = "—";

    /// <summary>What every device together is sending, or a dash.</summary>
    [ObservableProperty]
    public partial string Upload { get; private set; } = "—";

    /// <summary>Where the figures come from, or why there are none.</summary>
    [ObservableProperty]
    public partial string Note { get; private set; } = string.Empty;

    /// <inheritdoc />
    public void Showing(bool visible)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _showing = Math.Max(0, _showing + (visible ? 1 : -1));

            if (_showing > 0 && _watch is null)
            {
                _watch = _household.Watch();
            }
            else if (_showing == 0 && _watch is not null)
            {
                _watch.Dispose();
                _watch = null;
            }
        }
    }

    private void OnChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(new Action(Read));

    private void Read()
    {
        if (_disposed)
        {
            return;
        }

        IsShown = _settings.Home.ReadRouterTraffic;
        HouseholdRate? rate = _household.Latest;
        Download = HomeText.Rate(rate?.DownBytesPerSecond, _settings.ChartFamily);
        Upload = HomeText.Rate(rate?.UpBytesPerSecond, _settings.ChartFamily);

        Note = _household.State switch
        {
            HouseholdState.Reading when rate is null => "Waiting for the router's second reading",
            HouseholdState.Reading => _household.Router is { } router ? string.Concat("Every device, from ", router) : "Every device, from the router",
            HouseholdState.Starting => "Asking the router",
            HouseholdState.NotOffered => "Your router does not report its traffic",
            HouseholdState.NotHome => "Not on a home network",
            _ => "Every device, from the router",
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _watch?.Dispose();
            _watch = null;
        }

        _household.Changed -= OnChanged;
    }
}
