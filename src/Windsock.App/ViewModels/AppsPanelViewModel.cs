using CommunityToolkit.Mvvm.ComponentModel;

namespace Windsock.App.ViewModels;

/// <summary>What the Apps page shows: what is happening now, or a period of history.</summary>
public enum AppsPeriod
{
    Live,
    LastHour,
    Today,
    Yesterday,
    LastSevenDays,
    LastThirtyDays,
    ThisMonth,
}

/// <summary>One entry in the Apps page's period picker.</summary>
public sealed record AppsPeriodOption(AppsPeriod Period, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Backs the Apps page: every app on the network, live with the network map
/// above it, or totalled over a period of history.
/// </summary>
/// <remarks>
/// <para>
/// Two halves under one picker. Live is <see cref="LiveAppsViewModel"/>: the
/// apps holding sockets now, their rates and the map, and in the full edition
/// run as administrator the same for each process. Every other period is
/// <see cref="AppTotalsViewModel"/>: Windows' own count of each app, kept in
/// history, including time Windsock was not running.
/// </para>
/// <para>
/// Only the half on screen follows the machine. The other lets go of what it
/// was watching - the live half its per-process collection and the map's route
/// tracing, which is the point.
/// </para>
/// </remarks>
public sealed partial class AppsPanelViewModel : ObservableObject, IDisposable
{
    private bool _visible;

    public AppsPanelViewModel(LiveAppsViewModel live, AppTotalsViewModel totals)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(totals);

        Live = live;
        Totals = totals;

        // The heading says how many apps are live, or which dates are totalled.
        live.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LiveAppsViewModel.CountText) && IsLivePeriod)
            {
                OnPropertyChanged(nameof(Subtitle));
            }
        };

        totals.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppTotalsViewModel.RangeLabel) && !IsLivePeriod)
            {
                OnPropertyChanged(nameof(Subtitle));
            }
        };

        Periods =
        [
            new AppsPeriodOption(AppsPeriod.Live, "Live"),
            new AppsPeriodOption(AppsPeriod.LastHour, "Last hour"),
            new AppsPeriodOption(AppsPeriod.Today, "Today"),
            new AppsPeriodOption(AppsPeriod.Yesterday, "Yesterday"),
            new AppsPeriodOption(AppsPeriod.LastSevenDays, "Last 7 days"),
            new AppsPeriodOption(AppsPeriod.LastThirtyDays, "Last 30 days"),
            new AppsPeriodOption(AppsPeriod.ThisMonth, "This month"),
        ];
    }

    /// <summary>What is happening now.</summary>
    public LiveAppsViewModel Live { get; }

    /// <summary>A period of history.</summary>
    public AppTotalsViewModel Totals { get; }

    /// <summary>The periods on offer, Live first.</summary>
    public IReadOnlyList<AppsPeriodOption> Periods { get; }

    /// <summary>The period being shown. Live when the page opens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLivePeriod))]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial AppsPeriod SelectedPeriod { get; set; } = AppsPeriod.Live;

    /// <summary>Whether the page is showing what is happening now.</summary>
    public bool IsLivePeriod => SelectedPeriod == AppsPeriod.Live;

    /// <summary>The line under the page's title.</summary>
    public string Subtitle => IsLivePeriod ? Live.CountText : Totals.RangeLabel;

    /// <summary>Begins following the machine, with whichever half is on screen.</summary>
    public void Start()
    {
        _visible = true;
        Follow();
    }

    /// <summary>Stops following, while the page is out of sight.</summary>
    public void Stop()
    {
        _visible = false;
        Follow();
    }

    public void Dispose()
    {
        Live.Dispose();
        Totals.Dispose();
    }

    partial void OnSelectedPeriodChanged(AppsPeriod value)
    {
        if (value != AppsPeriod.Live)
        {
            Totals.SelectedRange = value switch
            {
                AppsPeriod.LastHour => AppsRange.LastHour,
                AppsPeriod.Yesterday => AppsRange.Yesterday,
                AppsPeriod.LastSevenDays => AppsRange.LastSevenDays,
                AppsPeriod.LastThirtyDays => AppsRange.LastThirtyDays,
                AppsPeriod.ThisMonth => AppsRange.ThisMonth,
                _ => AppsRange.Today,
            };
        }

        Follow();
    }

    private void Follow()
    {
        if (_visible && IsLivePeriod)
        {
            Totals.Stop();
            Live.Start();
        }
        else if (_visible)
        {
            Live.Stop();
            Totals.Start();
        }
        else
        {
            Live.Stop();
            Totals.Stop();
        }
    }
}
