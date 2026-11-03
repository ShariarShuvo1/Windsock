using System.Globalization;
using System.Windows.Media;
using Windsock.Core.Formatting;
using Windsock.Core.Insight;
using Windsock.Core.Processes;

namespace Windsock.App.ViewModels;

/// <summary>One program accepting connections, and every port it waits on.</summary>
public sealed class ListeningProgramViewModel
{
    public ListeningProgramViewModel(int processId, string name, string? path, ImageSource? icon, IReadOnlyList<ListeningSocket> sockets)
    {
        ArgumentNullException.ThrowIfNull(sockets);

        ProcessId = processId;
        Name = name;
        Path = path ?? string.Empty;
        Icon = icon;
        IsOpenToNetwork = sockets.Any(each => each.Reach != ListenReach.ThisPcOnly);

        Detail = string.Format(
            CultureInfo.CurrentCulture,
            "PID {0}{1}",
            processId,
            string.IsNullOrEmpty(path) ? string.Empty : string.Concat(" · ", path));

        Ports = string.Join(
            "; ",
            sockets
                .GroupBy(each => (each.Protocol, each.Reach))
                .OrderBy(group => group.Key.Reach)
                .ThenBy(group => group.Key.Protocol)
                .Select(group => string.Concat(
                    group.Key.Protocol == TransportProtocol.Tcp ? "TCP " : "UDP ",
                    string.Join(", ", group.Select(each => each.PortText).Distinct()),
                    " ",
                    Reach(group.Key.Reach))));

        PortCount = sockets.Select(each => (each.Protocol, each.Port)).Distinct().Count();
    }

    public int ProcessId { get; }

    /// <summary>The program, or a service host's services.</summary>
    public string Name { get; }

    /// <summary>The process id and the file.</summary>
    public string Detail { get; }

    public string Path { get; }

    public ImageSource? Icon { get; }

    /// <summary>"TCP 445 (smb), 139 (netbios) on every network; UDP 5353 on this PC only".</summary>
    public string Ports { get; }

    /// <summary>How many ports it waits on.</summary>
    public int PortCount { get; }

    /// <summary>Whether another machine could reach any of them, as far as the firewall lets it.</summary>
    public bool IsOpenToNetwork { get; }

    /// <summary>What a screen reader says for the tile.</summary>
    public string Spoken => string.Concat(Name, ", ", Ports);

    /// <summary>What a screen reader announces for the item.</summary>
    public override string ToString() => Spoken;

    private static string Reach(ListenReach reach) => reach switch
    {
        ListenReach.ThisPcOnly => "on this PC only",
        ListenReach.AllNetworks => "on every network",
        _ => "on one address",
    };
}

/// <summary>One name a process looked up, as a row of the lookups list.</summary>
public sealed class LookupRowViewModel(string app, int processId, string name, string type, int count, DateTimeOffset last, string status, string addresses)
{
    public string App { get; } = app;

    public int ProcessId { get; } = processId;

    public string Name { get; } = name;

    /// <summary>"A · asked 3 times · 21:04 · Found".</summary>
    public string Detail { get; } = string.Format(
        CultureInfo.CurrentCulture,
        "{0} · {1} · {2} · {3}",
        type,
        count == 1 ? "asked once" : string.Format(CultureInfo.CurrentCulture, "asked {0} times", count),
        ClockFormat.Current.Time(last.ToLocalTime()),
        status);

    /// <summary>The addresses the answer gave, or empty.</summary>
    public string Addresses { get; } = addresses;

    public string Spoken => string.Concat(App, " looked up ", Name, ", ", Detail);

    /// <summary>What a screen reader announces for the item.</summary>
    public override string ToString() => Spoken;
}

/// <summary>One day's traffic with one country or one site, with its share of the day.</summary>
public sealed class PlaceRowViewModel(string name, string detail, long bytes, double share)
{
    public string Name { get; } = name;

    public string Detail { get; } = detail;

    public string Total { get; } = SizeFormatter.Format(bytes);

    /// <summary>Its part of the day's busiest, from 0 to 1, for the bar under it.</summary>
    public double Share { get; } = Math.Clamp(share, 0, 1);

    public string Spoken => string.Concat(Name, ", ", Total, ", ", Detail);

    /// <summary>What a screen reader announces for the item.</summary>
    public override string ToString() => Spoken;
}
