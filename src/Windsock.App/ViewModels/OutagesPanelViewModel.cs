using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windsock.Core.Connectivity;
using Windsock.Core.Formatting;
using Windsock.Core.History;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>The spans of time the outage log can show.</summary>
public enum OutageRange
{
    Day,
    Week,
    Month,
    Everything,
}

/// <summary>One entry in the outage log's period picker.</summary>
public sealed record OutageRangeOption(OutageRange Range, string Label, string Period)
{
    public override string ToString() => Label;
}

/// <summary>One outage in the log.</summary>
public sealed class OutageRowViewModel(Outage outage, DateTimeOffset now)
{
    public Outage Outage { get; } = outage;

    /// <summary>When it happened, in words.</summary>
    public string When { get; } = OutageWords.When(outage, now, CultureInfo.CurrentCulture);

    /// <summary>How long it lasted, or has lasted so far.</summary>
    public string Duration { get; } = OutageWords.Brief((outage.End ?? now) - outage.Start);

    /// <summary>What was probably at fault.</summary>
    public string Cause { get; } = OutageWords.Blame(outage.Cause);

    /// <summary>The network it happened on, or nothing when that was not known.</summary>
    public string Network { get; } = outage.Network;

    /// <summary>Whether it is still going.</summary>
    public bool IsOngoing { get; } = outage.End is null;

    /// <summary>A word about how far to trust the end, or empty.</summary>
    public string Remark { get; } = outage.End is null
        ? "Still going"
        : outage.Interrupted
            ? "Windsock stopped watching before it ended"
            : string.Empty;

    /// <summary>Why, where, and the remark, as one line.</summary>
    public string Details => string.Join(
        " · ",
        new[] { Cause, Network, Remark }.Where(part => part.Length > 0));

    /// <summary>
    /// What a screen reader says for the row: what the tile shows, in its
    /// order. Without it the list item was read by its type's name.
    /// </summary>
    public override string ToString() =>
        string.Join(", ", new[] { When, Duration, Details }.Where(part => part.Length > 0));
}

/// <summary>
/// Backs Connection › Outages: every time the internet went away, a period at
/// a time, with its export and its pager.
/// </summary>
/// <remarks>
/// The log shared the Connection page with the live status until 2026-10-05,
/// where its period picker, export and pager competed with the status for the
/// reader's attention. On a page of its own it is one subject. While it is on
/// screen it watches the connection too, so an outage that starts while
/// somebody reads the log is logged even with background watching off.
/// </remarks>
public sealed partial class OutagesPanelViewModel : ObservableObject, IDisposable
{
    // The log a page at a time, so years of a flaky connection never become
    // thousands of rows on the page at once.
    private readonly PagedView<Outage> _outagePages;
    private OutageRange? _pagedRange;

    private readonly ConnectionMonitor _monitor;
    private readonly IOutageStore _store;
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<OutagesPanelViewModel> _logger;
    private readonly bool _loaded;

    private IDisposable? _watch;
    private bool _logPending;
    private bool _disposed;

    public OutagesPanelViewModel(
        ConnectionMonitor monitor,
        IOutageStore store,
        WindsockSettings settings,
        ISettingsStore settingsStore,
        Dispatcher dispatcher,
        ILogger<OutagesPanelViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _monitor = monitor;
        _store = store;
        _settings = settings;
        _settingsStore = settingsStore;
        _dispatcher = dispatcher;
        _logger = logger;

        int pageSize = PagerViewModel.Normalise(settings.OutageRowsPerPage);
        _outagePages = new PagedView<Outage>(pageSize);
        _outagePages.Changed += (_, _) => FillLogPage();
        LogPager = new PagerViewModel(
            pageSize,
            page => _outagePages.GoTo(page),
            size =>
            {
                _outagePages.PageSize = size;
                _settings.OutageRowsPerPage = size;
                _settingsStore.Save(_settings);
            },
            "outage",
            "outages");

        Ranges =
        [
            new(OutageRange.Day, "Last 24 hours", "the last 24 hours"),
            new(OutageRange.Week, "Last 7 days", "the last 7 days"),
            new(OutageRange.Month, "Last 30 days", "the last 30 days"),
            new(OutageRange.Everything, "Everything", "everything Windsock has kept"),
        ];

        SelectedRange = OutageRange.Week;
        _loaded = true;

        _store.Changed += OnLogChanged;

        // A change of Windows' clock rewrites the times in the log, like a change to it.
        ClockFormat.Changed += OnLogChanged;
    }

    public IReadOnlyList<OutageRangeOption> Ranges { get; }

    [ObservableProperty]
    public partial OutageRange SelectedRange { get; set; }

    public ObservableCollection<OutageRowViewModel> Rows { get; } = [];

    /// <summary>How many outages the period holds and how long they came to, under the title.</summary>
    [ObservableProperty]
    public partial string LogSummary { get; private set; } = string.Empty;

    /// <summary>Said after an export or a clear, or when the log could not be read.</summary>
    [ObservableProperty]
    public partial string LogNotice { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasRows { get; private set; }

    /// <summary>Whether the log has been read and holds nothing for the period.</summary>
    public bool IsEmpty => !HasRows && _read;

    private bool _read;

    /// <summary>What the empty log says it will hold.</summary>
    [ObservableProperty]
    public partial string EmptyTitle { get; private set; } = string.Empty;

    /// <summary>Why it is empty, and what to do.</summary>
    [ObservableProperty]
    public partial string EmptyNote { get; private set; } = string.Empty;

    /// <summary>Whether Clear has been pressed once and is waiting to be confirmed.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingClear { get; private set; }

    /// <summary>The bar under the outage log.</summary>
    public PagerViewModel LogPager { get; }

    /// <summary>Reads the log and watches the connection, for as long as the page is on screen.</summary>
    public void Show()
    {
        if (_disposed)
        {
            return;
        }

        _watch ??= _monitor.Watch();
        _ = LoadLogAsync();
    }

    /// <summary>Stops watching on the page's behalf. Background watching carries on if it is on.</summary>
    public void Hide()
    {
        _watch?.Dispose();
        _watch = null;
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
        _store.Changed -= OnLogChanged;
        ClockFormat.Changed -= OnLogChanged;
    }

    private void OnLogChanged(object? sender, EventArgs e)
    {
        if (_logPending)
        {
            return;
        }

        _logPending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _logPending = false;

            if (_watch is not null)
            {
                _ = LoadLogAsync();
            }
        }));
    }

    partial void OnSelectedRangeChanged(OutageRange value)
    {
        if (_loaded && _watch is not null)
        {
            _ = LoadLogAsync();
        }
    }

    private (DateTimeOffset From, DateTimeOffset Until, string Period) Bounds()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        OutageRangeOption option = Ranges.First(each => each.Range == SelectedRange);

        DateTimeOffset from = SelectedRange switch
        {
            OutageRange.Day => now.AddDays(-1),
            OutageRange.Week => now.AddDays(-7),
            OutageRange.Month => now.AddDays(-30),
            _ => DateTimeOffset.FromUnixTimeMilliseconds(0),
        };

        return (from, now.AddMinutes(1), option.Period);
    }

    private async Task LoadLogAsync()
    {
        (DateTimeOffset from, DateTimeOffset until, string period) = Bounds();
        IReadOnlyList<Outage> outages;

        try
        {
            outages = await Task.Run(() => _store.Read(from, until)).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogReadFailed(ex);
            _pagedRange = null;
            _outagePages.SetSource([], toFirstPage: true);
            LogSummary = string.Empty;
            LogNotice = "The outage log could not be read. Outages are still watched for, and the connection's status is on the Status page.";
            Explain(period, unreadable: true);
            return;
        }

        if (_disposed)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;

        // Newest first, as the store reads them. A reload of the same period
        // keeps the page on screen; another period starts at its first.
        bool otherPeriod = _pagedRange != SelectedRange;
        _pagedRange = SelectedRange;
        _outagePages.SetSource(outages, toFirstPage: otherPeriod);

        LogSummary = OutageWords.Summary([.. outages], period, now);

        if (!_settings.Connection.WatchInBackground)
        {
            LogSummary = string.Concat(LogSummary, " Outages are logged only while a Connection page is open, because watching in the background is off.");
        }

        Explain(period, unreadable: false);
    }

    /// <summary>What the log says while it holds nothing: what will appear, why nothing has, and what to do.</summary>
    private void Explain(string period, bool unreadable)
    {
        _read = true;
        OnPropertyChanged(nameof(IsEmpty));

        if (unreadable)
        {
            EmptyTitle = "The outage log could not be read";
            EmptyNote = "The file may be damaged or in use. Clear log starts it afresh; outages from now on are still logged.";
            return;
        }

        int seconds = (int)_settings.Connection.OutageAfter.TotalSeconds;

        EmptyTitle = string.Concat("No outages in ", period);
        EmptyNote = string.Concat(
            "An outage is logged when the internet is gone for longer than ",
            OutageWords.Spoken(TimeSpan.FromSeconds(seconds)),
            ". ",
            _settings.Connection.WatchInBackground
                ? "Choose a longer period to look further back."
                : "Watching in the background is off, so outages are only caught while a Connection page is open. Turn it on under Connection › Status.");
    }

    /// <summary>Puts the log's page on screen into the list.</summary>
    private void FillLogPage()
    {
        DateTimeOffset now = DateTimeOffset.Now;

        Rows.Clear();

        foreach (Outage outage in _outagePages.Current)
        {
            Rows.Add(new OutageRowViewModel(outage, now));
        }

        HasRows = Rows.Count > 0;
        LogPager.Show(_outagePages.Window);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        (DateTimeOffset from, DateTimeOffset until, _) = Bounds();

        SaveFileDialog dialog = new()
        {
            Title = "Export the outage log",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = string.Format(CultureInfo.InvariantCulture, "Windsock-outages-{0:yyyy-MM-dd}.csv", DateTimeOffset.Now),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string path = dialog.FileName;
        IReadOnlyList<Outage> outages;

        try
        {
            outages = await Task.Run(() => _store.Read(from, until)).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogReadFailed(ex);
            LogNotice = "Nothing was exported, because the outage log could not be read.";
            return;
        }

        try
        {
            // Oldest first in a file, which is how a spreadsheet expects time to run.
            Outage[] ordered = [.. outages.OrderBy(outage => outage.Start)];

            await Task.Run(() =>
            {
                using StreamWriter writer = new(path, false, new UTF8Encoding(true));
                OutageCsv.Write(writer, ordered);
            }).ConfigureAwait(true);

            LogNotice = string.Format(
                CultureInfo.CurrentCulture,
                "Exported {0:N0} {1} to {2}",
                ordered.Length,
                ordered.Length == 1 ? "outage" : "outages",
                Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotice = "Could not write that file: " + ex.Message;
        }
    }

    [RelayCommand]
    private void AskClear() => IsConfirmingClear = true;

    [RelayCommand]
    private void CancelClear() => IsConfirmingClear = false;

    [RelayCommand]
    private async Task ClearAsync()
    {
        IsConfirmingClear = false;

        try
        {
            int gone = await Task.Run(_store.Clear).ConfigureAwait(true);
            LogNotice = gone == 1 ? "Cleared 1 outage." : string.Create(CultureInfo.CurrentCulture, $"Cleared {gone:N0} outages.");
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogReadFailed(ex);
            LogNotice = "The outage log could not be cleared.";
        }

        await LoadLogAsync().ConfigureAwait(true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The outage log could not be read or written")]
    private partial void LogReadFailed(Exception error);
}
