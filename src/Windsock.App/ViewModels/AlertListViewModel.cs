using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Windsock.Core.Alerts;
using Windsock.Core.Formatting;
using Windsock.Core.History;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>Which alerts the list shows.</summary>
public enum AlertFilter
{
    /// <summary>Every alert kept.</summary>
    All,

    /// <summary>Only those not yet seen in the list.</summary>
    Unread,

    /// <summary>Only those Windows did not show: held, not shown, or summarised.</summary>
    Missed,
}

/// <summary>One choice in a picker of the list's.</summary>
/// <param name="Value">What is chosen.</param>
/// <param name="Label">Its name.</param>
public sealed record ListOption<T>(T Value, string Label)
{
    /// <summary>The label, which UI Automation reads for the item.</summary>
    public override string ToString() => Label;
}

/// <summary>One alert in the list.</summary>
public sealed class AlertRowViewModel(AlertRecord record, DateTimeOffset now, bool isNew)
{
    /// <summary>The alert as kept.</summary>
    public AlertRecord Record { get; } = record;

    /// <summary>Its first line.</summary>
    public string Title { get; } = record.Title;

    /// <summary>What it said.</summary>
    public string Message { get; } = record.Message.Replace('\n', ' ');

    /// <summary>When it was raised, in words.</summary>
    public string When { get; } = AlertWords.When(record.At, now);

    /// <summary>What became of it, when it was not simply shown; empty otherwise.</summary>
    public string Outcome { get; } = AlertWords.Outcome(record, now);

    /// <summary>Whether it was forwarded, in a few words; empty when forwarding had no part.</summary>
    public string Forwarding { get; } = AlertWords.Forwarding(record);

    /// <summary>Whether it was not shown, or forwarding failed: drawn in the warning colour.</summary>
    public bool IsMissed { get; } = record.Outcome is AlertOutcome.NotShown or AlertOutcome.Held || record.Forward == ForwardOutcome.Failed;

    /// <summary>Whether it is new to the person: unread when the page was opened.</summary>
    public bool IsNew { get; } = isNew;

    /// <summary>How much it matters.</summary>
    public AlertLevel Level { get; } = record.Level;

    /// <summary>The kind's name.</summary>
    public string Kind { get; } = AlertCatalogue.NameOf(record.Topic);

    /// <summary>The section it opens, or nothing.</summary>
    public string? Opens { get; } = record.Opens;

    /// <summary>Whether a click opens anything.</summary>
    public bool CanOpen => !string.IsNullOrEmpty(Opens);

    /// <summary>What a screen reader says for the row.</summary>
    public string Spoken => string.Join(
        ". ",
        new[] { IsNew ? "New" : string.Empty, Title, When, Message, Outcome, Forwarding }.Where(part => part.Length > 0));

    /// <summary>The same for the row's place in the list, which is read by its item's text.</summary>
    public override string ToString() => Spoken;
}

/// <summary>
/// The list of alerts sent, on the Alerts page: a page at a time, newest first,
/// with its filter, how long it is kept, and a way to clear it.
/// </summary>
/// <remarks>
/// <para>
/// Alerts unread when the page comes into view are marked read a moment after,
/// so the sidebar's count goes; they keep their "new" mark until the page is
/// left, so the person can still see which they were.
/// </para>
/// <para>
/// Read off the UI thread; the record raises its change on whatever thread
/// wrote, so a reload is queued once on the dispatcher however many arrive.
/// </para>
/// </remarks>
public sealed partial class AlertListViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan MarkAfter = TimeSpan.FromSeconds(2);

    private const string Unreadable = "The record of alerts could not be read. Alerts are still shown as they happen.";

    private readonly IAlertLog _log;
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _store;
    private readonly AlertDesk _desk;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _marker;
    private readonly HashSet<long> _newThisVisit = [];
    private readonly bool _loaded;

    private int _pageSize;
    private int _page = 1;
    private bool _visible;
    private bool _pending;
    private bool _readOnce;
    private bool _disposed;
    private int _loads;

    public AlertListViewModel(AlertDesk desk, WindsockSettings settings, ISettingsStore store, Dispatcher dispatcher, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(desk);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        _desk = desk;
        _log = desk.Log;
        _settings = settings;
        _store = store;
        _dispatcher = dispatcher;
        _logger = logger;

        _pageSize = PagerViewModel.Normalise(50);
        Pager = new PagerViewModel(
            _pageSize,
            page =>
            {
                _page = page;
                _ = LoadAsync();
            },
            size =>
            {
                _pageSize = size;
                _page = 1;
                _ = LoadAsync();
            },
            "alert",
            "alerts");

        Filters =
        [
            new(AlertFilter.All, "All alerts"),
            new(AlertFilter.Unread, "Unread"),
            new(AlertFilter.Missed, "Not shown at the time"),
        ];

        KeepChoices = [.. AlertSettings.KeepChoices.Select(days => new ListOption<int>(days, Days(days)))];

        if (!KeepChoices.Any(each => each.Value == settings.Alerts.KeepDays))
        {
            KeepChoices = [.. KeepChoices.Append(new ListOption<int>(settings.Alerts.KeepDays, Days(settings.Alerts.KeepDays))).OrderBy(each => each.Value)];
        }

        KeepDays = settings.Alerts.KeepDays;

        _marker = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = MarkAfter };
        _marker.Tick += OnMarkTick;

        _log.Changed += OnLogChanged;

        // A change of Windows' clock rewrites the times in the list, like a change to it.
        ClockFormat.Changed += OnLogChanged;
        _loaded = true;
    }

    /// <summary>The filters on offer.</summary>
    public IReadOnlyList<ListOption<AlertFilter>> Filters { get; }

    /// <summary>How long the record may be kept, on offer.</summary>
    public IReadOnlyList<ListOption<int>> KeepChoices { get; }

    /// <summary>The bar under the list.</summary>
    public PagerViewModel Pager { get; }

    /// <summary>The alerts on the page on screen.</summary>
    public ObservableCollection<AlertRowViewModel> Rows { get; } = [];

    /// <summary>Which alerts are shown.</summary>
    [ObservableProperty]
    public partial AlertFilter Filter { get; set; }

    /// <summary>How many days the record keeps.</summary>
    [ObservableProperty]
    public partial int KeepDays { get; set; }

    /// <summary>"12 alerts in the last 90 days, 3 new", under the band's title.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>Whether the page holds any alert.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasRows { get; private set; }

    /// <summary>Whether the record was read and the page holds nothing.</summary>
    public bool IsEmpty => !HasRows && _readOnce;

    /// <summary>What the empty list says will appear here.</summary>
    [ObservableProperty]
    public partial string EmptyTitle { get; private set; } = string.Empty;

    /// <summary>Why it is empty, and what to do.</summary>
    [ObservableProperty]
    public partial string EmptyNote { get; private set; } = string.Empty;

    /// <summary>Said after a clear, or when the record could not be read.</summary>
    [ObservableProperty]
    public partial string Notice { get; private set; } = string.Empty;

    /// <summary>Whether Clear has been pressed once and waits to be confirmed.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingClear { get; private set; }

    /// <summary>Whether any alert is unread, so Mark all read is worth offering.</summary>
    [ObservableProperty]
    public partial bool HasUnread { get; private set; }

    /// <summary>
    /// How many alerts are new to the person: unread, or unread when the page
    /// came into view and marked read since - so the figure and the marks in
    /// the list agree until the page is left.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNew))]
    public partial int Unread { get; private set; }

    /// <summary>Whether anything is new, so Mark all read is worth offering.</summary>
    public bool HasNew => Unread > 0;

    /// <summary>The list comes into view: read it, and mark what is new as seen in a moment.</summary>
    public void Show()
    {
        if (_disposed)
        {
            return;
        }

        _visible = true;
        _ = LoadAsync();
    }

    /// <summary>The list leaves the screen: what was new this visit is new no longer.</summary>
    public void Hide()
    {
        _visible = false;
        _marker.Stop();
        _newThisVisit.Clear();
        Unread = HasUnread ? Unread : 0;
        IsConfirmingClear = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Hide();
        _marker.Tick -= OnMarkTick;
        _log.Changed -= OnLogChanged;
        ClockFormat.Changed -= OnLogChanged;
    }

    partial void OnFilterChanged(AlertFilter value)
    {
        if (_loaded)
        {
            _page = 1;
            _ = LoadAsync();
        }
    }

    partial void OnKeepDaysChanged(int value)
    {
        if (!_loaded || value == _settings.Alerts.KeepDays)
        {
            return;
        }

        _settings.Alerts.KeepDays = value;
        _store.Save(_settings);

        // The desk prunes on its own beat; a shorter keep takes effect now.
        _ = Task.Run(() => Try(() => _log.Prune(DateTimeOffset.Now - _settings.Alerts.Keep)));
    }

    private void OnLogChanged(object? sender, EventArgs e)
    {
        if (_pending || _disposed)
        {
            return;
        }

        _pending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _pending = false;

            if (_visible && !_disposed)
            {
                _ = LoadAsync();
            }
        }));
    }

    private AlertQuery Query() => AlertQuery.Everything with
    {
        OnlyUnread = Filter == AlertFilter.Unread,
    };

    private async Task LoadAsync()
    {
        int token = ++_loads;
        AlertQuery query = Query();
        AlertFilter filter = Filter;
        int size = _pageSize;
        int page = _page;

        (bool ok, int total, IReadOnlyList<AlertRecord> records, int unread) = await Task.Run(() =>
        {
            try
            {
                int unreadCount = _log.UnreadCount();

                if (filter == AlertFilter.Missed)
                {
                    // Few enough to sort here: what Windows did not show at the time.
                    AlertRecord[] missed = [.. _log.Read(query with { Take = 10_000 })
                        .Where(each => each.Outcome is AlertOutcome.Held or AlertOutcome.NotShown or AlertOutcome.Summarised)];
                    PageWindow window = PageWindow.Of(missed.Length, size, page);
                    return (true, missed.Length, (IReadOnlyList<AlertRecord>)[.. missed.Skip(window.Start).Take(window.Count)], unreadCount);
                }

                int count = _log.Count(query);
                PageWindow shown = PageWindow.Of(count, size, page);
                return (true, count, _log.Read(query with { Skip = shown.Start, Take = size }), unreadCount);
            }
            catch (Exception ex) when (HistoryFailure.IsStorage(ex))
            {
                LogUnreadable(ex);
                return (false, 0, (IReadOnlyList<AlertRecord>)[], 0);
            }
        }).ConfigureAwait(true);

        if (token != _loads || _disposed)
        {
            return;
        }

        _readOnce = true;
        DateTimeOffset now = DateTimeOffset.Now;

        if (_visible)
        {
            foreach (AlertRecord each in records.Where(each => !each.IsRead))
            {
                _newThisVisit.Add(each.Id);
            }
        }

        PageWindow window = PageWindow.Of(total, size, page);
        _page = window.Page;

        Rows.Clear();

        foreach (AlertRecord record in records)
        {
            Rows.Add(new AlertRowViewModel(record, now, !record.IsRead || _newThisVisit.Contains(record.Id)));
        }

        HasRows = Rows.Count > 0;
        HasUnread = unread > 0;
        Unread = Math.Max(unread, _newThisVisit.Count);
        Pager.Show(window);
        OnPropertyChanged(nameof(IsEmpty));

        if (!ok)
        {
            Notice = Unreadable;
        }
        else if (Notice == Unreadable)
        {
            Notice = string.Empty;
        }

        Summary = ok ? Describe(total, unread, filter) : string.Empty;
        Explain(ok, filter);

        if (_visible && records.Any(each => !each.IsRead))
        {
            _marker.Stop();
            _marker.Start();
        }
    }

    private string Describe(int total, int unread, AlertFilter filter)
    {
        string kept = _settings.Alerts.KeepDays switch
        {
            7 => " in the last week",
            365 => " in the last year",
            int days => string.Create(CultureInfo.CurrentCulture, $" in the last {days:N0} days"),
        };

        string count = filter switch
        {
            AlertFilter.Unread => total == 1 ? "1 unread alert" : string.Create(CultureInfo.CurrentCulture, $"{total:N0} unread alerts"),
            AlertFilter.Missed => total == 1 ? "1 alert not shown at the time" : string.Create(CultureInfo.CurrentCulture, $"{total:N0} alerts not shown at the time"),
            _ => AlertWords.Count(total),
        };

        string held = _desk.HeldCount > 0
            ? string.Create(CultureInfo.CurrentCulture, $", {_desk.HeldCount:N0} held")
            : string.Empty;

        return filter == AlertFilter.All && unread > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{count}{kept}, {unread:N0} new{held}.")
            : string.Concat(count, kept, held, ".");
    }

    private void Explain(bool ok, AlertFilter filter)
    {
        if (!ok)
        {
            EmptyTitle = "The record of alerts could not be read";
            EmptyNote = "The file may be damaged or in use. Clear starts it afresh; alerts are still shown as they happen.";
            return;
        }

        (EmptyTitle, EmptyNote) = filter switch
        {
            AlertFilter.Unread => ("Nothing unread", "Every alert here has been seen. Choose All alerts to see them again."),
            AlertFilter.Missed => ("Nothing missed", "Every alert was shown when it happened. Alerts held for quiet hours, or not shown because notifications were off, would be listed here."),
            _ => (
                "No alerts yet",
                string.Concat(
                    "Every alert Windsock raises is kept here for ",
                    _settings.Alerts.KeepDays switch { 7 => "a week", 365 => "a year", int days => string.Create(CultureInfo.CurrentCulture, $"{days:N0} days") },
                    ", whether or not you saw the notification. Choose which alerts you want below.")),
        };
    }

    private void OnMarkTick(object? sender, EventArgs e)
    {
        _marker.Stop();

        if (!_visible || _disposed)
        {
            return;
        }

        long[] unread = [.. Rows.Where(row => !row.Record.IsRead).Select(row => row.Record.Id)];

        if (unread.Length > 0)
        {
            _ = Task.Run(() => Try(() => _log.MarkRead(unread)));
        }
    }

    /// <summary>Marks everything read, and takes the new marks off the list at once.</summary>
    [RelayCommand]
    private void MarkAllRead()
    {
        _newThisVisit.Clear();
        Unread = 0;
        _ = Task.Run(() => Try(_log.MarkAllRead));
    }

    [RelayCommand]
    private void AskClear() => IsConfirmingClear = true;

    [RelayCommand]
    private void CancelClear() => IsConfirmingClear = false;

    [RelayCommand]
    private async Task ClearAsync()
    {
        IsConfirmingClear = false;

        int gone = await Task.Run(() =>
        {
            try
            {
                return _log.Clear();
            }
            catch (Exception ex) when (HistoryFailure.IsStorage(ex))
            {
                LogUnreadable(ex);
                return -1;
            }
        }).ConfigureAwait(true);

        Notice = gone switch
        {
            < 0 => "The record of alerts could not be cleared.",
            1 => "Cleared 1 alert.",
            _ => string.Create(CultureInfo.CurrentCulture, $"Cleared {gone:N0} alerts."),
        };

        // Held alerts went with the rest; nothing is left to summarise.
        _desk.Refresh();
        _page = 1;
        await LoadAsync().ConfigureAwait(true);
    }

    private void Try(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogUnreadable(ex);
        }
    }

    private static string Days(int days) => days switch
    {
        1 => "1 day",
        7 => "A week",
        30 => "30 days",
        90 => "90 days",
        365 => "A year",
        _ => string.Create(CultureInfo.CurrentCulture, $"{days:N0} days"),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "The record of alerts could not be read or written")]
    private partial void LogUnreadable(Exception error);
}
