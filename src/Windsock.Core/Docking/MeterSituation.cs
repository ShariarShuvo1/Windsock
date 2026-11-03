using System.Globalization;
using System.Text;

namespace Windsock.Core.Docking;

/// <summary>What the meter managed to do with what it was asked for.</summary>
public enum MeterState
{
    /// <summary>Turned off.</summary>
    Off,

    /// <summary>In one or more taskbars.</summary>
    Docked,

    /// <summary>In windows of its own, above the taskbar.</summary>
    Floating,

    /// <summary>Asked to dock, and no taskbar could be found or entered.</summary>
    Unavailable,

    /// <summary>Not shown in this remote session, as asked.</summary>
    RemoteHidden,
}

/// <summary>How one meter is doing, on one taskbar or monitor.</summary>
/// <param name="Display">The monitor's name, as the page shows it.</param>
/// <param name="Edge">The edge of the taskbar it sits on, or nothing when there is no taskbar under it.</param>
/// <param name="Arrangement">How its readings are laid out on that bar.</param>
/// <param name="FullScreenHidden">Whether it is out of the way of a full-screen app.</param>
/// <param name="Crowded">Whether it had to cover some of the bar's buttons.</param>
/// <param name="MadeRoom">Whether a classic taskbar's task buttons were narrowed for it.</param>
/// <param name="Floated">
/// Whether it was asked to dock and floats instead, because the bar's shape
/// was not one it knows how to sit in.
/// </param>
public readonly record struct StationNote(
    string Display,
    TaskbarEdge? Edge,
    MeterArrangement Arrangement,
    bool FullScreenHidden = false,
    bool Crowded = false,
    bool MadeRoom = false,
    bool Floated = false);

/// <summary>
/// Everything the Taskbar page says about where the meter is and why.
/// </summary>
public sealed record MeterSituation
{
    /// <summary>A meter that is switched off.</summary>
    public static MeterSituation Off { get; } = new();

    /// <summary>What the meter is doing overall.</summary>
    public MeterState State { get; init; }

    /// <summary>Each meter on show, one per taskbar or monitor.</summary>
    public IReadOnlyList<StationNote> Stations { get; init; } = [];

    /// <summary>
    /// The computer whose readings these are, when Windsock runs in a remote
    /// session; nothing otherwise.
    /// </summary>
    public string? RemoteMachine { get; init; }

    /// <summary>
    /// The monitor that was picked and is not connected now, so the meter went
    /// to the main taskbar instead.
    /// </summary>
    public string? MissingDisplay { get; init; }

    /// <summary>
    /// The monitor that was picked and has no taskbar to dock into, so the
    /// meter went to the main taskbar instead.
    /// </summary>
    public string? BarlessDisplay { get; init; }

    /// <summary>The product drawing a replaced taskbar, such as StartAllBack, when one is known.</summary>
    public string? Replacement { get; init; }
}

/// <summary>Puts the meter's situation into words for the Taskbar page.</summary>
public static class MeterWords
{
    /// <summary>
    /// What the page's status line says, and whether it is a warning.
    /// </summary>
    public static (string Text, bool IsWarning) Describe(MeterSituation situation)
    {
        ArgumentNullException.ThrowIfNull(situation);

        switch (situation.State)
        {
            case MeterState.Off:
                return ("The meter is off. Nothing is added to the taskbar.", false);

            case MeterState.RemoteHidden:
                return (
                    string.Create(
                        CultureInfo.CurrentCulture,
                        $"Hidden in this remote session: the readings would be {Machine(situation)}'s, not this computer's."),
                    false);

            case MeterState.Unavailable:
                return (
                    "The taskbar could not be found, so there is nowhere to dock. "
                    + "Switch the position to \"Above the taskbar\" to show the meter anyway.",
                    true);
        }

        var text = new StringBuilder(Lead(situation));
        IReadOnlyList<StationNote> stations = situation.Stations;

        Add(text, Shape(stations));
        Add(text, FullScreen(stations));

        if (stations.Any(each => each.Floated))
        {
            Add(text, "The taskbar's shape was not recognised, so the meter floats above it instead.");
        }

        if (stations.Any(each => each.Crowded))
        {
            Add(text, "The taskbar is crowded, so the meter covers some of its buttons.");
        }

        if (stations.Any(each => each.MadeRoom))
        {
            Add(text, situation.Replacement is { Length: > 0 } product
                ? string.Create(CultureInfo.CurrentCulture, $"On {product}'s taskbar the task buttons move aside to make room.")
                : "The task buttons move aside to make room.");
        }

        if (situation.MissingDisplay is { Length: > 0 } missing)
        {
            Add(text, string.Create(
                CultureInfo.CurrentCulture,
                $"{missing} is not connected, so the meter is on the main taskbar."));
        }

        if (situation.BarlessDisplay is { Length: > 0 } barless)
        {
            Add(text, string.Create(
                CultureInfo.CurrentCulture,
                $"{barless} has no taskbar to dock into, so the meter is on the main one."));
        }

        if (situation.RemoteMachine is { Length: > 0 })
        {
            Add(text, string.Create(
                CultureInfo.CurrentCulture,
                $"This is a remote session, so these are {Machine(situation)}'s readings."));
        }

        return (text.ToString(), false);
    }

    /// <summary>
    /// What the meter's tooltip says first in a remote session, so nobody
    /// takes the remote computer's traffic for their own.
    /// </summary>
    public static string RemotePreface(string machine) =>
        string.Create(CultureInfo.CurrentCulture, $"Readings from {machine}, the computer this remote session runs on");

    /// <summary>
    /// How a monitor is named on the page: its number, with its size, and
    /// whether it is the main one.
    /// </summary>
    public static string DisplayLabel(string deviceName, int width, int height, bool primary)
    {
        string named = DisplayName(deviceName);
        string size = string.Create(CultureInfo.CurrentCulture, $"{width} × {height}");

        return primary
            ? string.Create(CultureInfo.CurrentCulture, $"{named} (main, {size})")
            : string.Create(CultureInfo.CurrentCulture, $"{named} ({size})");
    }

    /// <summary>
    /// "Display 2" for <c>\\.\DISPLAY2</c>, the number Windows' own display
    /// settings use; the device name itself when it carries no number.
    /// </summary>
    public static string DisplayName(string? deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            return "Display";
        }

        int start = deviceName.Length;

        while (start > 0 && char.IsAsciiDigit(deviceName[start - 1]))
        {
            start--;
        }

        return start < deviceName.Length
            ? string.Concat("Display ", deviceName.AsSpan(start))
            : deviceName.TrimStart('\\', '.');
    }

    private static string Lead(MeterSituation situation)
    {
        int count = situation.Stations.Count;
        bool docked = situation.State == MeterState.Docked;

        if (count > 1)
        {
            return docked
                ? string.Create(CultureInfo.CurrentCulture, $"Docked on {count} taskbars.")
                : string.Create(CultureInfo.CurrentCulture, $"Floating on {count} displays.");
        }

        return docked
            ? "Showing in the taskbar, beside the notification area."
            : "Showing in its own window, above the taskbar.";
    }

    private static string? Shape(IReadOnlyList<StationNote> stations)
    {
        foreach (StationNote station in stations)
        {
            if (station.Arrangement == MeterArrangement.Stacked && station.Edge is { } edge)
            {
                return string.Create(
                    CultureInfo.CurrentCulture,
                    $"The taskbar is on the {Side(edge)}, so readings are stacked.");
            }
        }

        foreach (StationNote station in stations)
        {
            if (station.Arrangement == MeterArrangement.OneLine)
            {
                return "The taskbar is too thin for more than one row, so readings sit on one line.";
            }
        }

        return null;
    }

    private static string? FullScreen(IReadOnlyList<StationNote> stations)
    {
        int hidden = stations.Count(each => each.FullScreenHidden);

        if (hidden == 0)
        {
            return null;
        }

        if (hidden == stations.Count)
        {
            return "Hidden while a full-screen app is in front.";
        }

        string where = string.Join(
            " and ",
            stations.Where(each => each.FullScreenHidden).Select(each => each.Display));

        return string.Create(
            CultureInfo.CurrentCulture,
            $"Hidden on {where} while a full-screen app is in front.");
    }

    private static string Side(TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Left => "left",
        TaskbarEdge.Right => "right",
        TaskbarEdge.Top => "top",
        _ => "bottom",
    };

    private static string Machine(MeterSituation situation) =>
        situation.RemoteMachine is { Length: > 0 } machine ? machine : "the remote computer";

    private static void Add(StringBuilder text, string? sentence)
    {
        if (sentence is not { Length: > 0 })
        {
            return;
        }

        if (text.Length > 0)
        {
            text.Append(' ');
        }

        text.Append(sentence);
    }
}
