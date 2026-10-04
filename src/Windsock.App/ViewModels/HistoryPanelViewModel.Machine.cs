using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Windsock.App.Controls;
using Windsock.Core.Connectivity;
using Windsock.Core.Formatting;
using Windsock.Core.History;

namespace Windsock.App.ViewModels;

/// <summary>The machine and the connection over the period on screen.</summary>
public sealed partial class HistoryPanelViewModel
{
    private readonly IMachineHistoryStore _machine;
    private readonly IOutageStore _outages;
    private Dictionary<DateTimeOffset, SystemBucket> _systemAt = [];
    private Dictionary<DateTimeOffset, ConnectionBucket> _connectionAt = [];

    /// <summary>The machine, a bucket to each bar of the usage chart.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SystemBucket> SystemBuckets { get; private set; } = [];

    /// <summary>The connection checks, a bucket to each bar.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ConnectionBucket> ConnectionBuckets { get; private set; } = [];

    /// <summary>The outages that touch the period.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<OutageSpan> OutageSpans { get; private set; } = [];

    /// <summary>Whether anything of the machine or the connection was kept for the period.</summary>
    [ObservableProperty]
    public partial bool HasMachineHistory { get; private set; }

    /// <summary>Bumped when the lanes have something new to draw.</summary>
    [ObservableProperty]
    public partial int MachineRevision { get; private set; }

    [ObservableProperty]
    public partial string ProcessorText { get; private set; } = "—";

    [ObservableProperty]
    public partial string ProcessorNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string GraphicsText { get; private set; } = "—";

    [ObservableProperty]
    public partial string GraphicsNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string MemoryText { get; private set; } = "—";

    [ObservableProperty]
    public partial string MemoryNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DiskText { get; private set; } = "—";

    [ObservableProperty]
    public partial string DiskNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ConnectionText { get; private set; } = "—";

    [ObservableProperty]
    public partial string ConnectionNote { get; private set; } = string.Empty;

    /// <summary>Whether the pointer is over a bucket of the lanes.</summary>
    [ObservableProperty]
    public partial bool HasMachineHover { get; private set; }

    [ObservableProperty]
    public partial string MachineHoverLabel { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string HoverProcessor { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string HoverGraphics { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string HoverMemory { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string HoverDisk { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string HoverPing { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string HoverOffline { get; private set; } = string.Empty;

    private async Task LoadMachineAsync(DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity, int token)
    {
        IReadOnlyList<SystemBucket> system;
        IReadOnlyList<ConnectionBucket> connection;
        IReadOnlyList<Outage> outages;

        try
        {
            (system, connection, outages) = await Task.Run(() => (
                _machine.ReadSystem(from, until, granularity),
                _machine.ReadConnection(from, until, granularity),
                ReadOutages(from, until))).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            // The usage above still stands; only the machine's part goes without.
            LogUnreadable(ex);
            return;
        }

        if (token != _loads)
        {
            return;
        }

        SystemBuckets = system;
        ConnectionBuckets = connection;
        _systemAt = system.ToDictionary(bucket => bucket.Start);
        _connectionAt = connection.ToDictionary(bucket => bucket.Start);

        DateTimeOffset now = DateTimeOffset.Now;
        OutageSpans = [.. outages.Select(outage => new OutageSpan(outage.Start, outage.End ?? now))];
        HasMachineHistory = system.Count > 0 || connection.Count > 0 || outages.Count > 0;
        MachineRevision++;

        Summarise(system, connection, outages, from, until, now);
    }

    private IReadOnlyList<Outage> ReadOutages(DateTimeOffset from, DateTimeOffset until)
    {
        try
        {
            return _outages.Read(from, until);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            // Outages live in a file of their own; that one being unreadable
            // costs only their marks.
            LogUnreadable(ex);
            return [];
        }
    }

    private void Summarise(
        IReadOnlyList<SystemBucket> system,
        IReadOnlyList<ConnectionBucket> connection,
        IReadOnlyList<Outage> outages,
        DateTimeOffset from,
        DateTimeOffset until,
        DateTimeOffset now)
    {
        double minutes = system.Sum(bucket => bucket.Minutes);

        if (minutes <= 0)
        {
            ProcessorText = GraphicsText = MemoryText = DiskText = "—";
            ProcessorNote = GraphicsNote = MemoryNote = DiskNote = "Nothing recorded";
        }
        else
        {
            double Weighted(Func<SystemBucket, double> value) => system.Sum(bucket => value(bucket) * bucket.Minutes) / minutes;

            ProcessorText = Percent(Weighted(bucket => bucket.ProcessorAverage));
            List<string> processor = [string.Concat("Peaked at ", Percent(system.Max(bucket => bucket.ProcessorPeak)))];

            if (system.Max(bucket => bucket.ProcessorHeatPeak) is { } hottest)
            {
                processor.Add(string.Concat("hottest ", SystemTileViewModel.Temperature(hottest)));
            }

            ProcessorNote = string.Join(", ", processor);

            GraphicsText = Percent(Weighted(bucket => bucket.GraphicsAverage));
            List<string> graphics = [string.Concat("Peaked at ", Percent(system.Max(bucket => bucket.GraphicsPeak)))];

            if (system.Max(bucket => bucket.GraphicsHeatPeak) is { } warmest)
            {
                graphics.Add(string.Concat("hottest ", SystemTileViewModel.Temperature(warmest)));
            }

            GraphicsNote = string.Join(", ", graphics);

            long total = system.Max(bucket => bucket.MemoryTotal);
            MemoryText = SizeFormatter.Format((long)Weighted(bucket => bucket.MemoryAverage));
            MemoryNote = total > 0
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    "On average, of {0}; at most {1}",
                    SizeFormatter.Format(total),
                    SizeFormatter.Format(system.Max(bucket => bucket.MemoryPeak)))
                : string.Empty;

            long read = system.Sum(bucket => bucket.DiskRead);
            long written = system.Sum(bucket => bucket.DiskWrite);
            DiskText = SizeFormatter.Format(read + written);
            DiskNote = string.Format(CultureInfo.CurrentCulture, "{0} read, {1} written", SizeFormatter.Format(read), SizeFormatter.Format(written));
        }

        // Outages count wherever they touch the period, and only the part inside it.
        TimeSpan offline = TimeSpan.Zero;

        foreach (Outage outage in outages)
        {
            DateTimeOffset start = outage.Start > from ? outage.Start : from;
            DateTimeOffset end = outage.End ?? now;
            end = end < until ? end : until;

            if (end > start)
            {
                offline += end - start;
            }
        }

        int answered = connection.Count(bucket => bucket.InternetAverageMs is not null || bucket.RouterAverageMs is not null);
        double? ping = answered == 0
            ? null
            : connection.Where(bucket => bucket.InternetAverageMs is not null || bucket.RouterAverageMs is not null)
                .Sum(bucket => (bucket.InternetAverageMs ?? bucket.RouterAverageMs ?? 0) * bucket.Minutes)
              / connection.Where(bucket => bucket.InternetAverageMs is not null || bucket.RouterAverageMs is not null).Sum(bucket => bucket.Minutes);

        ConnectionText = outages.Count switch
        {
            0 when connection.Count == 0 => "—",
            0 => "No outages",
            1 => "1 outage",
            int many => string.Format(CultureInfo.CurrentCulture, "{0:N0} outages", many),
        };

        List<string> note = [];

        if (outages.Count > 0)
        {
            note.Add(string.Concat(Duration(offline), " offline"));
        }

        if (ping is { } ms)
        {
            note.Add(string.Format(
                CultureInfo.CurrentCulture,
                "{0} answered in {1:0.#} ms on average",
                connection.Any(bucket => bucket.InternetAverageMs is not null) ? "the internet" : "the router",
                ms));
        }

        ConnectionNote = note.Count == 0 && connection.Count == 0
            ? "Nothing checked"
            : CapitalFirst(string.Join(", ", note));
    }

    /// <summary>Describes the bucket of the lanes under the pointer.</summary>
    public void SetMachineHover(bool hasValue, DateTimeOffset start)
    {
        HasMachineHover = hasValue;
        HighlightedStart = hasValue ? start : null;

        if (!hasValue)
        {
            return;
        }

        DateTimeOffset end = start + (_shown switch
        {
            UsageGranularity.Minute => TimeSpan.FromMinutes(1),
            UsageGranularity.Hour => TimeSpan.FromHours(1),
            _ => TimeSpan.FromDays(1),
        });

        MachineHoverLabel = string.Format(
            CultureInfo.CurrentCulture,
            "{0} – {1}",
            start.ToString(_shown == UsageGranularity.Day ? "ddd d MMM yyyy" : "ddd d MMM, h:mm tt", CultureInfo.CurrentCulture),
            end.ToString(_shown == UsageGranularity.Day ? "ddd d MMM yyyy" : "h:mm tt", CultureInfo.CurrentCulture));

        if (_systemAt.TryGetValue(start, out SystemBucket system))
        {
            HoverProcessor = string.Concat(
                Percent(system.ProcessorAverage),
                " average, ",
                Percent(system.ProcessorPeak),
                " peak",
                system.ProcessorHeatPeak is { } heat ? string.Concat(", ", SystemTileViewModel.Temperature(heat)) : string.Empty);
            HoverGraphics = string.Concat(
                Percent(system.GraphicsAverage),
                " average, ",
                Percent(system.GraphicsPeak),
                " peak",
                system.GraphicsHeatPeak is { } gpuHeat ? string.Concat(", ", SystemTileViewModel.Temperature(gpuHeat)) : string.Empty);
            HoverMemory = system.MemoryTotal > 0
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} average, {1} peak of {2}",
                    SizeFormatter.Format(system.MemoryAverage),
                    SizeFormatter.Format(system.MemoryPeak),
                    SizeFormatter.Format(system.MemoryTotal))
                : string.Empty;
            HoverDisk = string.Format(
                CultureInfo.CurrentCulture,
                "{0} read, {1} written",
                SizeFormatter.Format(system.DiskRead),
                SizeFormatter.Format(system.DiskWrite));
        }
        else
        {
            HoverProcessor = HoverGraphics = HoverMemory = HoverDisk = string.Empty;
        }

        if (_connectionAt.TryGetValue(start, out ConnectionBucket connection))
        {
            List<string> ping = [];

            if (connection.RouterAverageMs is { } router)
            {
                ping.Add(string.Format(CultureInfo.CurrentCulture, "router {0:0.#} ms", router));
            }

            if (connection.InternetAverageMs is { } internet)
            {
                ping.Add(string.Format(CultureInfo.CurrentCulture, "internet {0:0.#} ms", internet));
            }

            if (connection.RouterLoss is > 0 and { } routerLoss)
            {
                ping.Add(string.Format(CultureInfo.CurrentCulture, "{0:0.#}% of router checks unanswered", routerLoss * 100));
            }

            HoverPing = CapitalFirst(string.Join(", ", ping));
            HoverOffline = connection.Offline > 0
                ? string.Format(CultureInfo.CurrentCulture, "Offline for {0:N0} of {1:N0} checks", connection.Offline, connection.Checks)
                : string.Empty;
        }
        else
        {
            HoverPing = string.Empty;
            HoverOffline = string.Empty;
        }
    }

    private static string Percent(double value) =>
        string.Format(CultureInfo.CurrentCulture, value >= 10 ? "{0:0}%" : "{0:0.#}%", value);

    private static string Duration(TimeSpan span) => span.TotalMinutes < 1
        ? string.Format(CultureInfo.CurrentCulture, "{0:0}s", span.TotalSeconds)
        : span.TotalHours < 1
            ? string.Format(CultureInfo.CurrentCulture, "{0:0}m", span.TotalMinutes)
            : string.Format(CultureInfo.CurrentCulture, "{0:0}h {1}m", Math.Floor(span.TotalHours), span.Minutes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the machine's or the connection's history for the History tab")]
    private partial void LogUnreadable(Exception exception);

    private static string CapitalFirst(string text) =>
        text.Length == 0 ? text : string.Concat(char.ToUpper(text[0], CultureInfo.CurrentCulture).ToString(), text[1..]);
}
