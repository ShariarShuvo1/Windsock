using Windsock.Core.Adapters;

namespace Windsock.Core.Home;

/// <summary>The home links as they stand, or why there are none.</summary>
/// <param name="Links">The adapters on home networks, the busiest route first.</param>
/// <param name="Absence">Why there are none, when there are none.</param>
public sealed record HomeReading(IReadOnlyList<HomeLink> Links, HomeAbsence Absence)
{
    /// <summary>The home link internet traffic leaves by, or nothing.</summary>
    public HomeLink? Primary => Links.Count > 0 ? Links[0] : null;
}

/// <summary>Reads which adapters are on home networks, from Windows' network list and the adapters.</summary>
public sealed class HomeLinkReader(IConnectedNetworkSource networks, IAdapterReader adapters)
{
    private readonly IConnectedNetworkSource _networks = networks ?? throw new ArgumentNullException(nameof(networks));
    private readonly IAdapterReader _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));

    /// <summary>A reader over Windows itself.</summary>
    public HomeLinkReader()
        : this(new NetworkListNetworks(), new IpHelperAdapterReader { ReadDnsSettings = false })
    {
    }

    /// <summary>Reads both and decides. Sends nothing. Never throws.</summary>
    public HomeReading Read()
    {
        IReadOnlyList<ConnectedNetwork> connected = _networks.Read();
        IReadOnlyList<HomeLink> links = HomeScope.Links(connected, _adapters.Read());
        return new HomeReading(links, HomeScope.Absence(connected, links));
    }
}

/// <summary>Names the maker of an adapter from the table shipped with Windsock, loaded the first time it is asked.</summary>
public sealed class MakerNames
{
    private readonly Lazy<OuiTable?> _table;

    /// <summary>Over the table shipped beside Windsock.</summary>
    public MakerNames()
        : this(() => OuiTable.Open())
    {
    }

    /// <summary>Over a table from elsewhere.</summary>
    public MakerNames(Func<OuiTable?> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        _table = new Lazy<OuiTable?>(load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The day the table's listings were retrieved, or nothing when there is no table.</summary>
    public DateOnly? Retrieved => _table.Value?.Retrieved;

    /// <summary>Who made the adapter, or nothing when that cannot be said.</summary>
    public string? Find(MacAddress address) => _table.Value?.Find(address);
}
