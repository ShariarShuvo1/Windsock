using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.Core.Formatting;
using Windsock.Core.History;

namespace Windsock.App.ViewModels;

/// <summary>The table, a page at a time; see <see cref="PagedView{T}"/>.</summary>
public sealed partial class HistoryPanelViewModel
{
    private PagedView<UsageBucket> _pages = null!;
    private (DateTimeOffset From, DateTimeOffset Until, UsageGranularity Granularity) _query;
    private (DateTimeOffset From, DateTimeOffset Until, UsageGranularity Granularity)? _paged;
    private string _filters = string.Empty;
    private string? _sortPath;
    private ListSortDirection _sortDirection;
    private bool _atTop = true;
    private DateTimeOffset? _revealing;

    /// <summary>The bar under the table.</summary>
    public PagerViewModel Pager { get; private set; } = null!;

    /// <summary>
    /// Whether minutes have been recorded since the page on screen was read,
    /// and are held back because the reader is elsewhere in the table.
    /// </summary>
    [ObservableProperty]
    public partial bool HasNewerRows { get; private set; }

    /// <summary>Raised when a row asked for is on the page, so the view can scroll to it.</summary>
    public event EventHandler<HistoryRowViewModel>? RowRevealed;

    /// <summary>Raised when another page is put on screen, so the view can go to its top.</summary>
    public event EventHandler? PageTurned;

    private void BuildPager()
    {
        int size = PagerViewModel.Normalise(_settings.HistoryRowsPerPage);

        _pages = new PagedView<UsageBucket>(size);
        _pages.Changed += (_, _) => FillPage();

        Pager = new PagerViewModel(
            size,
            page =>
            {
                int before = _pages.Window.Page;
                _pages.GoTo(page);

                if (_pages.Window.Page != before)
                {
                    PageTurned?.Invoke(this, EventArgs.Empty);
                }

                Catch();
            },
            resize =>
            {
                _pages.PageSize = resize;
                _settings.HistoryRowsPerPage = resize;
                _settingsStore.Save(_settings);
            },
            "row",
            "rows");
    }

    /// <summary>
    /// Whether the table shows where new rows appear: its first page, scrolled
    /// to the top. Only then does a newly recorded minute reload it, so rows
    /// never move under somebody reading further down.
    /// </summary>
    public bool IsFollowing
    {
        get => _atTop && _pages.Window.Page == 1;
        set
        {
            _atTop = value;
            Catch();
        }
    }

    /// <summary>Reloads what was held back, once the table is where new rows show.</summary>
    private void Catch()
    {
        if (IsFollowing && _stale)
        {
            _stale = false;
            HasNewerRows = false;
            _ = LoadAsync();
        }
    }

    /// <summary>Notes that newer minutes wait, for a table not showing where they would go.</summary>
    private void HoldBack()
    {
        _stale = true;
        HasNewerRows = true;
    }

    /// <summary>Goes to the first page and the top, and reads what was recorded since.</summary>
    [RelayCommand]
    private void ShowNewest()
    {
        _pages.GoTo(1);
        _atTop = true;
        PageTurned?.Invoke(this, EventArgs.Empty);
        _stale = true;
        Catch();
    }

    /// <summary>Sorts the whole period by a column, or back to newest first with no path.</summary>
    public void SortBy(string? path, ListSortDirection direction)
    {
        _sortPath = path;
        _sortDirection = direction;
        IsSorted = path is not null;
        Rebuild();
    }

    /// <summary>
    /// Goes to the page holding a bucket's row and lights it. Used when a bar
    /// of the chart is clicked; hovering never turns the page.
    /// </summary>
    public void Reveal(DateTimeOffset start)
    {
        _revealing = start;
        int before = _pages.Window.Page;

        if (_pages.Reveal(bucket => bucket.Start == start) < 0)
        {
            _revealing = null;
            return;
        }

        if (_pages.Window.Page == before)
        {
            // Already on screen: no refill will come to finish the job.
            FinishReveal();
        }
    }

    private void FinishReveal()
    {
        if (_revealing is not { } start)
        {
            return;
        }

        foreach (HistoryRowViewModel row in Rows)
        {
            if (row.Start == start)
            {
                _revealing = null;
                HighlightedStart = start;
                RowRevealed?.Invoke(this, row);
                return;
            }
        }
    }

    /// <summary>
    /// Works the table out again from what was read: the whole period
    /// filtered and sorted, and one page of it on screen. The page stays where
    /// it was across a reload of the same period, and goes back to the first
    /// when the period, the resolution or a filter changes.
    /// </summary>
    private void Rebuild()
    {
        string filters = string.Join('', Columns.Select(column => column.HasFilter ? column.Filter : string.Empty));
        bool fresh = _paged != _query || filters != _filters;

        _paged = _query;
        _filters = filters;

        List<UsageBucket> newestFirst = new(_read.Count);

        for (int i = _read.Count - 1; i >= 0; i--)
        {
            newestFirst.Add(_read[i]);
        }

        UsageGranularity granularity = _shown;
        Func<UsageBucket, bool>? keep = Columns.Any(column => column.HasFilter)
            ? bucket => Columns.All(column => column.Matches(new HistoryRowViewModel(bucket, granularity)))
            : null;

        _pages.Reset(newestFirst, keep, Order(), fresh);
    }

    private Comparison<UsageBucket>? Order()
    {
        Func<UsageBucket, long>? key = _sortPath switch
        {
            nameof(HistoryRowViewModel.Start) or nameof(HistoryRowViewModel.End) => bucket => bucket.Start.UtcTicks,
            nameof(HistoryRowViewModel.BytesDown) => bucket => bucket.AllDown,
            nameof(HistoryRowViewModel.BytesUp) => bucket => bucket.AllUp,
            nameof(HistoryRowViewModel.BytesTotal) => bucket => bucket.AllTotal,
            nameof(HistoryRowViewModel.PeakDownBytes) => bucket => bucket.PeakDown,
            nameof(HistoryRowViewModel.PeakUpBytes) => bucket => bucket.PeakUp,
            nameof(HistoryRowViewModel.MinutesRecorded) => bucket => bucket.Minutes,
            _ => null,
        };

        if (key is null)
        {
            return null;
        }

        int sign = _sortDirection == ListSortDirection.Descending ? -1 : 1;
        return (left, right) => sign * key(left).CompareTo(key(right));
    }

    /// <summary>Puts the page on screen into the table, a few rows a pass so a large page does not stall.</summary>
    private void FillPage()
    {
        // The lit row is about to stop existing.
        _lit = null;
        HighlightedStart = null;
        Rows.Clear();

        PageWindow window = _pages.Window;
        Pager.Show(window, Columns.Any(column => column.HasFilter) ? _pages.SourceCount : null);
        OnPropertyChanged(nameof(IsFollowing));

        List<HistoryRowViewModel> made = [.. _pages.Current.Select(bucket => new HistoryRowViewModel(bucket, _shown))];

        // A new fill supersedes whatever the last one had left to do.
        int token = ++_fills;
        int at = 0;

        void Hand()
        {
            if (token != _fills)
            {
                return;
            }

            int end = Math.Min(at + Batch, made.Count);

            for (; at < end; at++)
            {
                Rows.Add(made[at]);
            }

            if (at < made.Count)
            {
                _dispatcher.BeginInvoke(Hand, DispatcherPriority.Background);
                return;
            }

            IsFilling = false;
            FinishReveal();
        }

        IsFilling = made.Count > Batch;
        Hand();
    }
}
