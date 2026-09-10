using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Windsock.App.ViewModels;

/// <summary>
/// One far end a process is talking to, as a node on the graph.
/// </summary>
public sealed partial class PeerNodeViewModel : ObservableObject
{
    public PeerNodeViewModel(string key, long seen)
    {
        Key = key;
        Label = key;
        Seen = seen;
    }

    /// <summary>What identifies this peer across rebuilds.</summary>
    public string Key { get; }

    /// <summary>
    /// When this peer was first seen, counting from the window opening.
    /// </summary>
    public long Seen { get; }

    /// <summary>What to call it: a site name, or an address with no name.</summary>
    [ObservableProperty]
    public partial string Label { get; set; }

    /// <summary>
    /// The far ends folded into this one, when several share a dot.
    /// </summary>
    [ObservableProperty]
    public partial string Sites { get; set; } = string.Empty;

    /// <summary>Outbound rate to this peer, in bytes per second.</summary>
    [ObservableProperty]
    public partial double SendRate { get; set; }

    /// <summary>Inbound rate from this peer, in bytes per second.</summary>
    [ObservableProperty]
    public partial double ReceiveRate { get; set; }

    /// <summary>Both rates, written out, or a dash when they are unknown.</summary>
    [ObservableProperty]
    public partial string RateText { get; set; } = "—";

    /// <summary>The addresses behind the name.</summary>
    [ObservableProperty]
    public partial string Addresses { get; set; } = string.Empty;

    /// <summary>Which ports are in use, named where they are well known.</summary>
    [ObservableProperty]
    public partial string Ports { get; set; } = string.Empty;

    /// <summary>How many sockets are open to this peer, written out.</summary>
    [ObservableProperty]
    public partial string Sockets { get; set; } = string.Empty;

    /// <summary>What those sockets are doing.</summary>
    [ObservableProperty]
    public partial string States { get; set; } = string.Empty;

    /// <summary>Everything moved either way since the window opened.</summary>
    [ObservableProperty]
    public partial string Totals { get; set; } = string.Empty;

    /// <summary>What clicking this node will do.</summary>
    [ObservableProperty]
    public partial string Hint { get; set; } = string.Empty;

    /// <summary>How far away it is, and how long it takes to answer.</summary>
    [ObservableProperty]
    public partial string Distance { get; set; } = string.Empty;

    /// <summary>Where on the earth it is, while the map is being drawn.</summary>
    [ObservableProperty]
    public partial string Where { get; set; } = string.Empty;

    /// <summary>
    /// The programs reaching it, with their icons.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<GraphReacher> Reachers { get; set; } = [];

    /// <summary>How long the far end takes to answer, once it is known.</summary>
    [ObservableProperty]
    public partial string Latency { get; set; } = string.Empty;

    /// <summary>Whether rates for this peer are being measured at all.</summary>
    [ObservableProperty]
    public partial bool HasRates { get; set; }

    /// <summary>
    /// Whether this far end is on this machine rather than out on the network.
    /// </summary>
    [ObservableProperty]
    public partial bool IsLocal { get; set; }

    /// <summary>Whether the pointer is over this peer, or over one of its rows.</summary>
    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    /// <summary>Whether this is the node the graph is following.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Whether something else is being followed, and this is not on it.</summary>
    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    /// <summary>How busy this peer is, for ordering the table.</summary>
    public double Weight { get; set; }

    /// <summary>
    /// Every address this name has been seen at.
    /// </summary>
    public IReadOnlyList<string> Probes { get; set; } = [];

    /// <summary>
    /// Which peers are reached at or beyond this node, which is normally itself.
    /// </summary>
    public IReadOnlyList<string> Reach { get; set; } = [];

    /// <summary>Returns the label, so automation announces it by name.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// One point on the way to somewhere, as a node on the graph.
/// </summary>
public sealed partial class HopNodeViewModel : ObservableObject
{
    private readonly string _plain;
    private string? _named;

    public HopNodeViewModel(string id, string? address, int distance, int silences)
    {
        Id = id;
        Address = address ?? string.Empty;
        Distance = distance;
        Silences = silences;
        _plain = silences > 0 ? "no reply" : Address;
        Label = _plain;

        Title = silences switch
        {
            0 => string.Format(CultureInfo.CurrentCulture, "Hop {0}", distance),
            1 => string.Format(CultureInfo.CurrentCulture, "Hop {0}, unanswered", distance),
            _ => string.Format(
                CultureInfo.CurrentCulture,
                "Hops {0} to {1}, unanswered",
                distance - silences + 1,
                distance),
        };
    }

    /// <summary>Where this hop sits, which is what identifies it.</summary>
    public string Id { get; }

    /// <summary>What this hop is, in words.</summary>
    public string Title { get; }

    /// <summary>How many hops out from the process this one is.</summary>
    public int Distance { get; }

    /// <summary>How many unanswered hops this node stands in for.</summary>
    public int Silences { get; }

    /// <summary>The router's address, or empty when nothing answered.</summary>
    public string Address { get; }

    /// <summary>Whether anything answered here.</summary>
    public bool Answered => Silences == 0;

    /// <summary>The router's name once it resolves, otherwise its address.</summary>
    [ObservableProperty]
    public partial string Label { get; set; }

    /// <summary>How long this hop takes to answer.</summary>
    [ObservableProperty]
    public partial string Latency { get; set; } = string.Empty;

    /// <summary>Where on the earth it is, while the map is being drawn.</summary>
    [ObservableProperty]
    public partial string Where { get; set; } = string.Empty;

    /// <summary>How many of the process's peers are reached through here.</summary>
    [ObservableProperty]
    public partial string Carries { get; set; } = string.Empty;

    /// <summary>What clicking this node will do.</summary>
    [ObservableProperty]
    public partial string Hint { get; set; } = string.Empty;

    /// <summary>
    /// The address, once a name has taken its place on the drawing.
    /// </summary>
    [ObservableProperty]
    public partial string AddressLine { get; set; } = string.Empty;

    /// <summary>Whether the pointer is over this hop, or over a row beyond it.</summary>
    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    /// <summary>Whether this is the node the graph is following.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Whether something else is being followed, and this is not on it.</summary>
    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    /// <summary>Which peers are reached through this hop.</summary>
    public IReadOnlyList<string> Reach { get; set; } = [];

    /// <summary>Gives the hop a name once the resolver has one.</summary>
    public void Name(string? name, bool show)
    {
        if (!string.IsNullOrEmpty(name))
        {
            _named = name;
        }

        bool named = show && _named is not null;

        Label = named ? _named! : _plain;
        AddressLine = named ? Address : string.Empty;
    }

    /// <summary>Returns the label, so automation announces it by name.</summary>
    public override string ToString() => Label;
}

/// <summary>One program reaching a place, as it appears on the map.</summary>
public sealed record GraphReacher(string Name, ImageSource? Icon);
