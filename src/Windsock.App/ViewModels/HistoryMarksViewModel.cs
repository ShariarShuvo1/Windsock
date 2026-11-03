using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Windsock.App.Controls;
using Windsock.Core.Alerts;
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Reports;
using Windsock.Core.Formatting;

namespace Windsock.App.ViewModels;

/// <summary>A network in the History chart's key: its name, its hue, and what it carried.</summary>
public sealed record NetworkKeyRow(string Name, int Hue, string Amount)
{
    /// <summary>What a screen reader says for the key's entry: the name, and what it carried, said on hover.</summary>
    public override string ToString() => Amount.Length == 0 ? Name : string.Concat(Name, ", ", Amount);
}

/// <summary>A note in the History page's list.</summary>
public sealed record NoteRow(long Id, DateTimeOffset From, DateTimeOffset Until, string Text, string When)
{
    /// <summary>What a screen reader says for the note's place in the list; a record's own would list its fields.</summary>
    public override string ToString() => string.Concat("Note: ", Text, ", ", When);
}

/// <summary>Something that happened, in the History page's list of events.</summary>
public sealed record EventRow(DateTimeOffset At, string When, string Title, string Detail, int Hue)
{
    /// <summary>When, then the detail: "Mon 5 Oct, 2:08 AM to 2:12 AM · 4 minutes, on Ethernet".</summary>
    public string Line => Detail.Length == 0 ? When : string.Concat(When, " · ", Detail);

    /// <summary>
    /// What a screen reader says for the event's place in the list: how much
    /// it mattered, which the tile shows by its mark, then the tile's words.
    /// </summary>
    public override string ToString()
    {
        string level = Hue switch
        {
            1 => "Warning",
            2 => "Problem",
            _ => string.Empty,
        };

        return string.Join(". ", new[] { level, Title, When, Detail }.Where(part => part.Length > 0));
    }
}

/// <summary>
/// What the History page marks on its period beside the usage itself: the
/// network each stretch was on, outages and alerts, notes, a stretch measured
/// by dragging, a download timed by hand, and whether the file can be written
/// at all.
/// </summary>
/// <remarks>
/// The page tells it the period with <see cref="Follow"/> after every load;
/// it reads its own marks off the interface thread, drops a reading a newer
/// one overtook, and hands the table the network of each row through
/// <see cref="NetworksRead"/>.
/// </remarks>
public sealed partial class HistoryMarksViewModel : ObservableObject, IDisposable
{
    /// <summary>The hues a network can take; the fifth is everything after the busiest four.</summary>
    public const int NetworkHues = 5;

    private const int ListedEvents = 60;
    private static readonly TimeSpan LongestMinuteRead = TimeSpan.FromDays(8);

    private readonly IHistoryNotes _notes;
    private readonly IUsageAggregates _aggregates;
    private readonly IHistoryFile _file;
    private readonly EventTimeline _timeline;
    private readonly CurrentNetwork _network;
    private readonly DownloadStopwatch _stopwatch;
    private readonly HistoryJson _json;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<HistoryMarksViewModel> _logger;
    private readonly DispatcherTimer _ticker;

    private DateTimeOffset _from;
    private DateTimeOffset _until;
    private UsageGranularity _granularity;
    private IReadOnlyList<UsageBucket> _buckets = [];
    private Dictionary<DateTimeOffset, string> _networkOf = [];
    private int _networkLoads;
    private int _eventLoads;
    private int _noteLoads;
    private bool _following;
    private bool _syncing;

    public HistoryMarksViewModel(
        IHistoryNotes notes,
        IUsageAggregates aggregates,
        IHistoryFile file,
        EventTimeline timeline,
        CurrentNetwork network,
        ThroughputMonitor monitor,
        HistoryJson json,
        Dispatcher dispatcher,
        ILogger<HistoryMarksViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _notes = notes;
        _aggregates = aggregates;
        _file = file;
        _timeline = timeline;
        _network = network;
        _dispatcher = dispatcher;
        _logger = logger;
        _json = json;
        _stopwatch = new DownloadStopwatch(monitor);

        _ticker = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => ReadStopwatch();

        NetworkKeys = [];
        NoteRows = [];
        EventRows = [];

        // A file a newer version wrote: read but never written, or - when this
        // version cannot even read it - left alone altogether.
        bool unreadable = file.State == HistoryFileState.Unavailable && file.FileSchema > 0;
        IsReadOnly = file.State == HistoryFileState.NewerVersion || unreadable;
        ReadOnlyText = !IsReadOnly
            ? string.Empty
            : unreadable
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    "This history file was written by a newer version of Windsock (format {0}) in a way this version cannot read, so there is nothing to show. It is left exactly as it is: nothing is recorded or changed until that version, or a later one, runs again.",
                    file.FileSchema)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    "This history file was written by a newer version of Windsock (format {0}), so this version only reads it. Nothing new is recorded, and deleting, importing and notes are off, until that version runs again. Nothing in the file is damaged.",
                    file.FileSchema);

        _notes.NotesChanged += OnNotesChanged;
        _timeline.Changed += OnTimelineChanged;
        ClockFormat.Changed += OnClockChanged;
    }

    /// <summary>Raised on the interface thread when each row's network has been read.</summary>
    public event EventHandler? NetworksRead;

    /// <summary>Whether the file belongs to a newer version, so nothing can be written to it.</summary>
    public bool IsReadOnly { get; }

    /// <summary>What the page says while <see cref="IsReadOnly"/>.</summary>
    public string ReadOnlyText { get; }

    /// <summary>Notes can be added, changed and removed.</summary>
    public bool CanWriteNotes => !IsReadOnly;

    // ---- The lanes ----------------------------------------------------------

    /// <summary>The network each stretch was on, as the lane draws it.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<LaneSpan> NetworkSpans { get; private set; } = [];

    /// <summary>Outages as spans, everything else as moments.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<LaneSpan> EventSpans { get; private set; } = [];

    /// <summary>Notes, each across the time it is about.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<LaneSpan> NoteSpans { get; private set; } = [];

    /// <summary>The networks in the period, busiest first, with the hue each is drawn in.</summary>
    public ObservableCollection<NetworkKeyRow> NetworkKeys { get; }

    [ObservableProperty]
    public partial bool HasNetworkKeys { get; private set; }

    /// <summary>What the network lane says when it is empty, or nothing.</summary>
    [ObservableProperty]
    public partial string NetworkNote { get; private set; } = string.Empty;

    // ---- Notes --------------------------------------------------------------

    public ObservableCollection<NoteRow> NoteRows { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuiet))]
    public partial bool HasNotes { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoteTitle), nameof(IsQuiet))]
    public partial bool IsEditingNote { get; private set; }

    /// <summary>Whether there is nothing at all to list: no notes, no events, no note being written.</summary>
    public bool IsQuiet => !HasNotes && !HasEvents && !IsEditingNote;

    /// <summary>The note being written or changed; zero for a new one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoteTitle))]
    public partial long EditingNoteId { get; private set; }

    public string NoteTitle => EditingNoteId == 0 ? "A new note" : "Change the note";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveNote))]
    public partial string NoteText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveNote), nameof(NoteProblem))]
    public partial DateTime NoteFrom { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveNote), nameof(NoteProblem))]
    public partial DateTime NoteUntil { get; set; }

    public string NoteProblem => NoteUntil < NoteFrom ? "The end is before the start." : string.Empty;

    public bool CanSaveNote => !IsReadOnly && NoteText.Trim().Length > 0 && NoteUntil >= NoteFrom;

    // ---- Events -------------------------------------------------------------

    /// <summary>The newest of what happened in the period, newest first.</summary>
    public ObservableCollection<EventRow> EventRows { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuiet))]
    public partial bool HasEvents { get; private set; }

    /// <summary>How many there were in all, when more than are listed.</summary>
    [ObservableProperty]
    public partial string EventsNote { get; private set; } = string.Empty;

    // ---- Measuring ----------------------------------------------------------

    /// <summary>Whether a drag across the chart measures rather than pans.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeasureButtonText))]
    public partial bool IsMeasuring { get; set; }

    public string MeasureButtonText => IsMeasuring ? "Stop measuring" : "Measure a stretch";

    [ObservableProperty]
    public partial DateTime MeasureFrom { get; set; }

    [ObservableProperty]
    public partial DateTime MeasureUntil { get; set; }

    /// <summary>What the stretch held, in a sentence.</summary>
    [ObservableProperty]
    public partial string MeasureText { get; private set; } = string.Empty;

    /// <summary>The stretch as the buckets it took in, for the overlay; equal ends while nothing is chosen.</summary>
    [ObservableProperty]
    public partial DateTime MeasuredFrom { get; private set; }

    [ObservableProperty]
    public partial DateTime MeasuredUntil { get; private set; }

    // ---- The stopwatch ------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StopwatchButtonText))]
    public partial bool IsTiming { get; private set; }

    public string StopwatchButtonText => IsTiming ? "Stop" : "Time a download";

    [ObservableProperty]
    public partial string StopwatchText { get; private set; } = "Times what every app moves from when you press it until you press it again. Nothing is kept.";

    // ---- Following the page --------------------------------------------------

    /// <summary>The page has a new period on screen: read its marks.</summary>
    public void Follow(DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity, IReadOnlyList<UsageBucket> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);

        bool moved = from != _from || until != _until || granularity != _granularity;
        _from = from;
        _until = until;
        _granularity = granularity;
        _buckets = buckets;
        _following = true;

        _ = ReadNetworksAsync();
        _ = ReadEventsAsync();
        _ = ReadNotesAsync();

        if (moved)
        {
            ClearMeasure();
        }
        else
        {
            Measure();
        }
    }

    /// <summary>The network a row of the table was on: its name, "and N more", or nothing when not known.</summary>
    public string NetworkOf(DateTimeOffset bucketStart) =>
        _networkOf.TryGetValue(bucketStart, out string? name) ? name : string.Empty;

    /// <summary>
    /// Writes a stretch as JSON - everything History holds for it - and says
    /// what happened. Read whole before the file is touched, so a history that
    /// cannot be read leaves no file behind.
    /// </summary>
    public async Task<string> ExportJsonAsync(string path, DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity)
    {
        byte[] document;
        HistoryExportCounts counts;

        try
        {
            (document, counts) = await Task.Run(() =>
            {
                using MemoryStream buffer = new();
                HistoryExportCounts written = _json.Write(buffer, from, until, granularity, DateTimeOffset.UtcNow);
                return (buffer.ToArray(), written);
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogMarksFailed(ex);
            return "Nothing was exported, because the history file could not be read.";
        }

        try
        {
            await File.WriteAllBytesAsync(path, document).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Could not write that file: " + ex.Message;
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            "Exported {0:N0} {1} of usage, {2:N0} of apps, {3:N0} of the machine and {4:N0} {5} to {6}",
            counts.Usage,
            counts.Usage == 1 ? "row" : "rows",
            counts.Apps,
            counts.Machine,
            counts.Notes,
            counts.Notes == 1 ? "note" : "notes",
            Path.GetFileName(path));
    }

    /// <summary>Opens the note editor on a row's stretch, from the table's row menu.</summary>
    public void NoteFor(DateTimeOffset from, DateTimeOffset until)
    {
        if (IsReadOnly)
        {
            return;
        }

        EditingNoteId = 0;
        NoteText = string.Empty;
        NoteFrom = from.LocalDateTime;
        NoteUntil = until.AddMinutes(-1).LocalDateTime;
        IsEditingNote = true;
    }

    public void Dispose()
    {
        _notes.NotesChanged -= OnNotesChanged;
        _timeline.Changed -= OnTimelineChanged;
        ClockFormat.Changed -= OnClockChanged;
        _ticker.Stop();
        _stopwatch.Dispose();
    }

    // ---- Notes --------------------------------------------------------------

    [RelayCommand]
    private void AddNote()
    {
        if (IsReadOnly)
        {
            return;
        }

        // The stretch being measured, if there is one; else the newest bucket.
        (DateTime from, DateTime until) = MeasuredUntil > MeasuredFrom
            ? (MeasuredFrom, MeasuredUntil.AddMinutes(-1))
            : _buckets.Count > 0
                ? (_buckets[^1].Start.LocalDateTime, (_buckets[^1].Start + Width(_granularity)).AddMinutes(-1).LocalDateTime)
                : (_from.LocalDateTime, _from.AddHours(1).AddMinutes(-1).LocalDateTime);

        EditingNoteId = 0;
        NoteText = string.Empty;
        NoteFrom = from;
        NoteUntil = until;
        IsEditingNote = true;
    }

    /// <summary>Opens the note editor on a table row's stretch.</summary>
    [RelayCommand]
    private void NoteForRow(HistoryRowViewModel? row)
    {
        if (row is not null)
        {
            NoteFor(row.Start, row.End);
        }
    }

    [RelayCommand]
    private void EditNote(NoteRow? note)
    {
        if (note is null || IsReadOnly)
        {
            return;
        }

        EditingNoteId = note.Id;
        NoteText = note.Text;
        NoteFrom = note.From.LocalDateTime;
        NoteUntil = note.Until.AddMinutes(-1).LocalDateTime;
        IsEditingNote = true;
    }

    /// <summary>A note clicked on the chart's lane opens for changing.</summary>
    public void EditNote(long id)
    {
        if (NoteRows.FirstOrDefault(row => row.Id == id) is { } row)
        {
            EditNote(row);
        }
    }

    [RelayCommand]
    private void CancelNote() => IsEditingNote = false;

    [RelayCommand]
    private async Task SaveNoteAsync()
    {
        if (!CanSaveNote)
        {
            return;
        }

        long id = EditingNoteId;
        string text = NoteText.Trim();

        // The editor names whole minutes, the last one included.
        DateTimeOffset from = new(Minute(NoteFrom));
        DateTimeOffset until = new(Minute(NoteUntil).AddMinutes(1));

        try
        {
            await Task.Run(() =>
            {
                if (id == 0)
                {
                    _notes.AddNote(from, until, text);
                }
                else
                {
                    _notes.UpdateNote(id, from, until, text);
                }
            }).ConfigureAwait(true);

            IsEditingNote = false;
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogNoteFailed(ex);
            NoteFailure = HistoryFailure.IsReadOnly(ex)
                ? "The note was not kept: this history file belongs to a newer version of Windsock."
                : "The note could not be kept, because the history file could not be written.";
        }
    }

    [RelayCommand]
    private async Task DeleteNoteAsync(NoteRow? note)
    {
        if (note is null || IsReadOnly)
        {
            return;
        }

        try
        {
            await Task.Run(() => _notes.RemoveNote(note.Id)).ConfigureAwait(true);

            if (EditingNoteId == note.Id)
            {
                IsEditingNote = false;
            }
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogNoteFailed(ex);
            NoteFailure = "The note could not be removed, because the history file could not be written.";
        }
    }

    /// <summary>Why the last note could not be kept, or nothing.</summary>
    [ObservableProperty]
    public partial string NoteFailure { get; private set; } = string.Empty;

    partial void OnIsEditingNoteChanged(bool value) => NoteFailure = string.Empty;

    private void OnNotesChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_following)
            {
                _ = ReadNotesAsync();
            }
        }));

    private async Task ReadNotesAsync()
    {
        int token = ++_noteLoads;
        DateTimeOffset from = _from;
        DateTimeOffset until = _until;
        IReadOnlyList<HistoryNote> notes;

        try
        {
            notes = await Task.Run(() => _notes.ReadNotes(from, until)).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogMarksFailed(ex);
            notes = [];
        }

        if (token != _noteLoads)
        {
            return;
        }

        NoteRows.Clear();

        foreach (HistoryNote note in notes.OrderBy(note => note.From))
        {
            NoteRows.Add(new NoteRow(note.Id, note.From, note.Until, note.Text, Stretch(note.From, note.Until)));
        }

        HasNotes = NoteRows.Count > 0;
        NoteSpans = [.. notes.Select(note => new LaneSpan(note.From, note.Until, 0, string.Concat(note.Text, "\n", Stretch(note.From, note.Until)), note.Id))];
    }

    // ---- Events -------------------------------------------------------------

    // Windows' clock went from 12 to 24-hour or back: the notes and events
    // carry their times as text, so they are read and written again.
    private void OnClockChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_following)
            {
                _ = ReadNotesAsync();
                _ = ReadEventsAsync();
            }
        }));

    private void OnTimelineChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            // Only a period running up to now can gain a mark.
            if (_following && _until > DateTimeOffset.Now.AddMinutes(-5))
            {
                _ = ReadEventsAsync();
            }
        }));

    private async Task ReadEventsAsync()
    {
        int token = ++_eventLoads;
        DateTimeOffset from = _from;
        DateTimeOffset until = _until;
        IReadOnlyList<TimelineMarker> marks;

        try
        {
            marks = await Task.Run(() => _timeline.Read(from, until)).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogMarksFailed(ex);
            marks = [];
        }

        if (token != _eventLoads)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        List<LaneSpan> spans = new(marks.Count);

        foreach (TimelineMarker mark in marks)
        {
            int hue = HueOf(mark.Level);

            if (mark.IsSpan)
            {
                DateTimeOffset end = mark.Until ?? (now < until ? now : until);
                string ended = mark.Until is null ? "still going" : UsageText.Time(end);
                spans.Add(new LaneSpan(mark.At, end, hue, string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}, {1} to {2}\n{3}",
                    mark.Title,
                    UsageText.When(mark.At),
                    ended,
                    mark.Detail).TrimEnd()));
                continue;
            }

            spans.Add(new LaneSpan(mark.At, mark.At, hue, string.Concat(mark.Title, ", ", UsageText.When(mark.At), "\n", mark.Detail).TrimEnd()));
        }

        EventSpans = spans;

        EventRows.Clear();

        foreach (TimelineMarker mark in marks.Reverse().Take(ListedEvents))
        {
            string when = mark.IsSpan && mark.Until is { } end
                ? string.Concat(UsageText.When(mark.At), " to ", UsageText.Time(end))
                : UsageText.When(mark.At);

            EventRows.Add(new EventRow(mark.At, when, mark.Title, mark.Detail, HueOf(mark.Level)));
        }

        HasEvents = EventRows.Count > 0;
        EventsNote = marks.Count > ListedEvents
            ? string.Format(CultureInfo.CurrentCulture, "The newest {0} of {1}. Choose a shorter period to see the rest.", ListedEvents, marks.Count)
            : string.Empty;
    }

    private static int HueOf(AlertLevel level) => level switch
    {
        AlertLevel.Problem => 2,
        AlertLevel.Warning => 1,
        _ => 0,
    };

    // ---- Networks -----------------------------------------------------------

    /// <summary>
    /// Reads which network carried each stretch: minute by minute for a short
    /// period at minute resolution, a quarter hour at a time otherwise.
    /// </summary>
    private async Task ReadNetworksAsync()
    {
        int token = ++_networkLoads;
        DateTimeOffset from = _from;
        DateTimeOffset until = _until;
        UsageGranularity granularity = _granularity;
        IReadOnlyList<UsageBucket> buckets = _buckets;

        List<Carried> carried;

        try
        {
            carried = await Task.Run(() => ReadCarried(from, until, granularity)).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogMarksFailed(ex);
            carried = [];
        }

        if (token != _networkLoads)
        {
            return;
        }

        ShowNetworks(carried, buckets);
    }

    private List<Carried> ReadCarried(DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity)
    {
        long first = EpochMinute.From(from);
        long last = EpochMinute.From(until);
        List<Carried> carried = [];

        if (granularity == UsageGranularity.Minute && until - from <= LongestMinuteRead)
        {
            foreach (UsageMinute minute in _aggregates.ReadMinuteRows(first, last))
            {
                if (minute.Network is { } tag)
                {
                    carried.Add(new Carried(minute.Minute, 1, tag, minute.BytesTotal));
                }
            }

            return carried;
        }

        IReadOnlyDictionary<long, NetworkTag> tags = _aggregates.ReadNetworkTags();

        foreach (UsageQuarter quarter in _aggregates.ReadQuarters(first, last))
        {
            if (quarter.NetworkId != 0 && tags.TryGetValue(quarter.NetworkId, out NetworkTag? tag))
            {
                // A quarter hour's row, or a single minute at the period's edge.
                int length = quarter.StartMinute % 15 == 0 && quarter.StartMinute + 15 <= last ? 15 : 1;
                carried.Add(new Carried(quarter.StartMinute, length, tag, quarter.BytesTotal));
            }
        }

        return carried;
    }

    private void ShowNetworks(List<Carried> carried, IReadOnlyList<UsageBucket> buckets)
    {
        // Busiest first; the busiest four keep their own hue.
        List<(string Key, string Name, long Bytes)> ranked = [.. carried
            .GroupBy(each => each.Network.Key, StringComparer.Ordinal)
            .Select(group => (group.Key, _network.NameOf(group.First().Network), group.Sum(each => each.Bytes)))
            .OrderByDescending(each => each.Item3)];

        Dictionary<string, int> hues = new(StringComparer.Ordinal);

        for (int i = 0; i < ranked.Count; i++)
        {
            hues[ranked[i].Key] = Math.Min(i, NetworkHues - 1);
        }

        NetworkKeys.Clear();

        for (int i = 0; i < ranked.Count && i < NetworkHues; i++)
        {
            bool rest = i == NetworkHues - 1 && ranked.Count > NetworkHues;
            string name = rest
                ? string.Format(CultureInfo.CurrentCulture, "{0} other networks", ranked.Count - (NetworkHues - 1))
                : ranked[i].Name;
            long bytes = rest ? ranked.Skip(i).Sum(each => each.Bytes) : ranked[i].Bytes;
            NetworkKeys.Add(new NetworkKeyRow(name, i, UsageText.Bytes(bytes)));
        }

        HasNetworkKeys = NetworkKeys.Count > 0;
        NetworkNote = carried.Count == 0 && buckets.Count > 0
            ? "No network is known for this period. Windsock notes the network from this version on, while it records."
            : string.Empty;

        Dictionary<string, string> names = ranked.ToDictionary(each => each.Key, each => each.Name, StringComparer.Ordinal);
        NetworkSpans = Lane(carried, hues, names);
        Dictionary<DateTimeOffset, string> networkOf = ByBucket(carried, buckets, names);

        // Every new minute reloads the page; the table is only rebuilt when a row's network changed.
        if (Same(networkOf, _networkOf))
        {
            return;
        }

        _networkOf = networkOf;
        NetworksRead?.Invoke(this, EventArgs.Empty);
    }

    private static bool Same(Dictionary<DateTimeOffset, string> left, Dictionary<DateTimeOffset, string> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out string? other) && string.Equals(pair.Value, other, StringComparison.Ordinal));

    /// <summary>The lane: each stretch's busiest network, with touching stretches on one network joined.</summary>
    private static List<LaneSpan> Lane(List<Carried> carried, Dictionary<string, int> hues, Dictionary<string, string> names)
    {
        List<LaneSpan> spans = [];
        long runStart = 0;
        long runEnd = 0;
        string? runKey = null;

        void Close()
        {
            if (runKey is not null)
            {
                spans.Add(new LaneSpan(EpochMinute.ToLocal(runStart), EpochMinute.ToLocal(runEnd), hues[runKey], names[runKey]));
            }
        }

        foreach (IGrouping<long, Carried> stretch in carried.GroupBy(each => each.Minute).OrderBy(group => group.Key))
        {
            Carried busiest = stretch.MaxBy(each => each.Bytes);
            long end = stretch.Key + busiest.Length;

            if (string.Equals(runKey, busiest.Network.Key, StringComparison.Ordinal) && stretch.Key <= runEnd)
            {
                runEnd = Math.Max(runEnd, end);
                continue;
            }

            Close();
            runKey = busiest.Network.Key;
            runStart = stretch.Key;
            runEnd = end;
        }

        Close();
        return spans;
    }

    /// <summary>Each bucket's network for the table: the one that carried most of it, and how many more there were.</summary>
    private static Dictionary<DateTimeOffset, string> ByBucket(List<Carried> carried, IReadOnlyList<UsageBucket> buckets, Dictionary<string, string> names)
    {
        Dictionary<DateTimeOffset, string> result = [];

        if (buckets.Count == 0 || carried.Count == 0)
        {
            return result;
        }

        long[] starts = [.. buckets.Select(bucket => EpochMinute.From(bucket.Start))];
        Dictionary<int, Dictionary<string, long>> tally = [];

        foreach (Carried each in carried)
        {
            int index = Array.BinarySearch(starts, each.Minute);

            if (index < 0)
            {
                index = ~index - 1;
            }

            if (index < 0)
            {
                continue;
            }

            if (!tally.TryGetValue(index, out Dictionary<string, long>? networks))
            {
                networks = new Dictionary<string, long>(StringComparer.Ordinal);
                tally[index] = networks;
            }

            networks[each.Network.Key] = networks.GetValueOrDefault(each.Network.Key) + each.Bytes;
        }

        foreach ((int index, Dictionary<string, long> networks) in tally)
        {
            string busiest = networks.MaxBy(pair => pair.Value).Key;
            string name = names.GetValueOrDefault(busiest, busiest);
            result[buckets[index].Start] = networks.Count == 1
                ? name
                : string.Format(CultureInfo.CurrentCulture, "{0} and {1} more", name, networks.Count - 1);
        }

        return result;
    }

    // ---- Measuring ----------------------------------------------------------

    [RelayCommand]
    private void ToggleMeasure()
    {
        IsMeasuring = !IsMeasuring;

        if (IsMeasuring && MeasuredUntil <= MeasuredFrom && _buckets.Count > 0)
        {
            // Something to start from that the boxes can show: the newest bucket.
            UsageBucket newest = _buckets[^1];
            SetMeasure(newest.Start.LocalDateTime, (newest.Start + Width(_granularity)).AddMinutes(-1).LocalDateTime);
        }
    }

    [RelayCommand]
    private void ClearMeasure()
    {
        _syncing = true;
        MeasureFrom = default;
        MeasureUntil = default;
        _syncing = false;
        MeasuredFrom = default;
        MeasuredUntil = default;
        MeasureText = string.Empty;
    }

    partial void OnMeasureFromChanged(DateTime value) => Measure();

    partial void OnMeasureUntilChanged(DateTime value) => Measure();

    private void SetMeasure(DateTime from, DateTime until)
    {
        _syncing = true;
        MeasureFrom = from;
        MeasureUntil = until;
        _syncing = false;
        Measure();
    }

    private void Measure()
    {
        if (_syncing)
        {
            return;
        }

        if (MeasureFrom == default || MeasureUntil == default)
        {
            MeasuredFrom = default;
            MeasuredUntil = default;
            MeasureText = IsMeasuring ? "Drag across the chart, or type the two ends." : string.Empty;
            return;
        }

        TimeSpan? width = _granularity == UsageGranularity.Day ? null : Width(_granularity);
        // The boxes and the overlay name whole minutes, the last one included.
        MeasuredSpan span = UsageMeasure.Of(_buckets, new DateTimeOffset(MeasureFrom), new DateTimeOffset(MeasureUntil.AddMinutes(1)), width);

        MeasuredFrom = span.From.LocalDateTime;
        MeasuredUntil = span.Until.LocalDateTime;

        if (span.Buckets == 0)
        {
            MeasureText = "Nothing was recorded or counted in that stretch.";
            return;
        }

        string text = string.Format(
            CultureInfo.CurrentCulture,
            "{0}: {1} together, {2} down and {3} up",
            Stretch(span.From, span.Until),
            UsageText.Bytes(span.All),
            UsageText.Bytes(span.Down + span.EstimatedDown),
            UsageText.Bytes(span.Up + span.EstimatedUp));

        if (span.RecordedMinutes > 0)
        {
            text += string.Format(
                CultureInfo.CurrentCulture,
                ", {0}/s on average while recorded",
                UsageText.Bytes((long)span.AverageRate));
        }

        if (span.EstimatedDown + span.EstimatedUp > 0)
        {
            text += string.Format(CultureInfo.CurrentCulture, ". {0} of it Windows counted while Windsock was closed", UsageText.Bytes(span.EstimatedDown + span.EstimatedUp));
        }

        MeasureText = text + ".";
    }

    // ---- The stopwatch ------------------------------------------------------

    [RelayCommand]
    private void ToggleStopwatch()
    {
        DateTimeOffset now = DateTimeOffset.Now;

        if (_stopwatch.IsRunning)
        {
            _stopwatch.Stop(now);
            _ticker.Stop();
        }
        else
        {
            _stopwatch.Start(now);
            _ticker.Start();
        }

        IsTiming = _stopwatch.IsRunning;
        ReadStopwatch();
    }

    private void ReadStopwatch()
    {
        StopwatchReading reading = _stopwatch.Read(DateTimeOffset.Now);

        StopwatchText = string.Format(
            CultureInfo.CurrentCulture,
            "{0} {1}: {2} down, {3} up. {4}/s on average, {5}/s at the fastest.",
            reading.Running ? "Timing for" : "Timed",
            reading.Elapsed.ToString(reading.Elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.CurrentCulture),
            UsageText.Bytes(reading.Down),
            UsageText.Bytes(reading.Up),
            UsageText.Bytes((long)reading.AverageDown),
            UsageText.Bytes((long)reading.PeakDown));
    }

    // ---- Words --------------------------------------------------------------

    /// <summary>"Mon 5 Oct, 14:00 to 15:00", or across days "Mon 5 Oct, 23:00 to Tue 6 Oct, 01:00".</summary>
    private static string Stretch(DateTimeOffset from, DateTimeOffset until)
    {
        DateTimeOffset localFrom = from.ToLocalTime();
        DateTimeOffset localUntil = until.ToLocalTime();

        return localFrom.Date == localUntil.Date || (localUntil.TimeOfDay == TimeSpan.Zero && localUntil.Date == localFrom.Date.AddDays(1))
            ? string.Concat(UsageText.When(localFrom), " to ", UsageText.Time(localUntil))
            : string.Concat(UsageText.When(localFrom), " to ", UsageText.When(localUntil));
    }

    private static TimeSpan Width(UsageGranularity granularity) => granularity switch
    {
        UsageGranularity.Minute => TimeSpan.FromMinutes(1),
        UsageGranularity.Hour => TimeSpan.FromHours(1),
        _ => TimeSpan.FromDays(1),
    };

    private static DateTime Minute(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, DateTimeKind.Local);

    /// <summary>Some of a network's traffic: from a minute, for so many minutes, so many bytes.</summary>
    private readonly record struct Carried(long Minute, int Length, NetworkTag Network, long Bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the History page's networks, events or notes")]
    private partial void LogMarksFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not keep a change to a note")]
    private partial void LogNoteFailed(Exception exception);
}
