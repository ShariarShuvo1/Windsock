using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Windsock.App.ViewModels;

/// <summary>
/// One app on this machine as a node on the map - every process of it - or, in
/// the process view, one process.
/// </summary>
public sealed partial class ProcessNodeViewModel : ObservableObject
{
    public ProcessNodeViewModel(string key, string name, long seen)
    {
        Key = key;
        Name = name;
        Label = name;
        Seen = seen;
    }

    /// <summary>What the node stands for: an app's key, or a process's.</summary>
    public string Key { get; }

    /// <summary>What the program is called.</summary>
    public string Name { get; }

    /// <summary>When it was first seen, so the map keeps its shape.</summary>
    public long Seen { get; }

    /// <summary>What to call it on the node.</summary>
    [ObservableProperty]
    public partial string Label { get; set; }

    /// <summary>The program's own icon, or null where Windows has none.</summary>
    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    /// <summary>Outbound rate for the whole process, in bytes per second.</summary>
    [ObservableProperty]
    public partial double SendRate { get; set; }

    /// <summary>Inbound rate for the whole process.</summary>
    [ObservableProperty]
    public partial double ReceiveRate { get; set; }

    /// <summary>Both rates, written out; the socket count while rates are not measured.</summary>
    [ObservableProperty]
    public partial string RateText { get; set; } = "—";

    /// <summary>Whether <see cref="RateText"/> states rates, measured or Windows' count.</summary>
    [ObservableProperty]
    public partial bool HasRates { get; set; }

    /// <summary>Where the rates come from, when that needs saying; empty when each second is measured.</summary>
    [ObservableProperty]
    public partial string RateNote { get; set; } = string.Empty;

    /// <summary>How many sockets it holds open, written out.</summary>
    [ObservableProperty]
    public partial string Sockets { get; set; } = string.Empty;

    /// <summary>How many places it is talking to, written out.</summary>
    [ObservableProperty]
    public partial string Places { get; set; } = string.Empty;

    /// <summary>Where the program lives, for the popover.</summary>
    [ObservableProperty]
    public partial string FilePath { get; set; } = string.Empty;

    /// <summary>What clicking this node will do.</summary>
    [ObservableProperty]
    public partial string Hint { get; set; } = string.Empty;

    /// <summary>Whether the pointer is over this process, or over its traffic.</summary>
    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    /// <summary>Whether this is the node the map is following.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Whether something else is being followed, and this is not on it.</summary>
    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    /// <summary>Which peers are reached through this process.</summary>
    public IReadOnlyList<string> Reach { get; set; } = [];

    /// <summary>Returns the label, so automation announces it by name.</summary>
    public override string ToString() => Label;
}
