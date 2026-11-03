namespace Windsock.Core.Settings;

/// <summary>How much room the window gives each row, figure and control.</summary>
public enum Density
{
    /// <summary>The usual spacing.</summary>
    Comfortable,

    /// <summary>Tighter rows and smaller plates, for more on screen.</summary>
    Compact,
}

/// <summary>
/// How the window is drawn beyond light and dark: the accent colour and the
/// density.
/// </summary>
public sealed class AppearanceSettings
{
    /// <summary>
    /// Whether the accent - selection, the page's main action, links, focus -
    /// takes the colour chosen in Windows instead of Windsock's own cyan.
    /// Download, upload and the colour of each part of the PC never change.
    /// </summary>
    public bool UseWindowsAccent { get; set; }

    /// <summary>How much room rows, plates and controls are given.</summary>
    public Density Density { get; set; } = Density.Comfortable;
}
