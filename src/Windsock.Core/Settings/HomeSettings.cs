namespace Windsock.Core.Settings;

/// <summary>
/// The home network features: the devices on it, the router check and the
/// router's own traffic counters.
/// </summary>
/// <remarks>
/// Every switch starts off, and each only ever acts on a network Windows
/// classes as private. None of it reaches the internet: what each sends goes to
/// devices on the home network or to the router, and the privacy page lists it.
/// </remarks>
public sealed class HomeSettings
{
    /// <summary>
    /// Whether the devices on the home network are listed: read from Windows'
    /// neighbour table, which sends nothing, and named by asking them - a
    /// multicast DNS browse, a NetBIOS query to each device, a reverse lookup put
    /// to the router - all of which stays on the home network.
    /// </summary>
    public bool FindDevices { get; set; }

    /// <summary>
    /// Whether an alert says when a device joins the home network for the first
    /// time. Off until switched on, on the Alerts page; it also needs
    /// <see cref="FindDevices"/>.
    /// </summary>
    public bool NotifyNewDevices { get; set; }

    /// <summary>
    /// Whether the router may be asked over UPnP for its maker, its model and the
    /// ports it has opened to the internet: when asked, and once a week.
    /// </summary>
    public bool CheckRouter { get; set; }

    /// <summary>
    /// Whether the router's own traffic counters are read every few seconds while
    /// the household's traffic is on screen or in the local feed.
    /// </summary>
    public bool ReadRouterTraffic { get; set; }
}
