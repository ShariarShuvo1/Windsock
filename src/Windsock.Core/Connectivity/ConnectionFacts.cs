namespace Windsock.Core.Connectivity;

/// <summary>What Windows itself says about reaching the internet.</summary>
public enum WindowsReach
{
    /// <summary>Windows could not be asked.</summary>
    Unknown,

    /// <summary>No network at all.</summary>
    None,

    /// <summary>A network, but not the internet.</summary>
    LocalOnly,

    /// <summary>A network that wants somebody to sign in first, such as hotel Wi-Fi.</summary>
    SignInRequired,

    /// <summary>The internet.</summary>
    Internet,
}

/// <summary>What kind of link a network is on.</summary>
public enum NetworkKind
{
    Other,

    Ethernet,

    WiFi,

    Cellular,
}

/// <summary>The network this PC is using, as a person would name it.</summary>
/// <param name="Name">The network's name: a Wi-Fi name, or what Windows calls a wired network.</param>
/// <param name="Kind">The kind of link.</param>
public sealed record NetworkIdentity(string Name, NetworkKind Kind)
{
    /// <summary>
    /// The name and the kind together, for a sentence: "Home (Wi-Fi)". A wired
    /// network Windows has named after its adapter is just "Ethernet", not
    /// "Ethernet (Ethernet)".
    /// </summary>
    public string Describe()
    {
        string kind = Kind switch
        {
            NetworkKind.WiFi => "Wi-Fi",
            NetworkKind.Ethernet => "Ethernet",
            NetworkKind.Cellular => "mobile",
            _ => string.Empty,
        };

        return kind.Length == 0 || Name.Contains(kind, StringComparison.OrdinalIgnoreCase)
            ? Name
            : string.Concat(Name, " (", kind, ")");
    }
}

/// <summary>
/// Everything about the connection that can be learned without sending a packet.
/// </summary>
/// <param name="Reach">What Windows says.</param>
/// <param name="Network">The network in use, or nothing when there is none.</param>
/// <param name="Gateway">The router's address on that network, or nothing.</param>
/// <param name="VpnActive">Whether a VPN adapter is up.</param>
public readonly record struct ConnectionFacts(
    WindowsReach Reach,
    NetworkIdentity? Network,
    string? Gateway,
    bool VpnActive)
{
    /// <summary>Facts from a moment Windows could not be asked about anything.</summary>
    public static ConnectionFacts Unknown { get; } = new(WindowsReach.Unknown, null, null, VpnActive: false);

    /// <summary>Whether this PC is on any network at all.</summary>
    public bool HasNetwork => Network is not null || Gateway is not null;
}

/// <summary>Reads the connection facts from the operating system.</summary>
public interface IConnectionFactsSource
{
    /// <summary>The facts as they stand now. Never throws; unknown is a reading too.</summary>
    ConnectionFacts Read();

    /// <summary>
    /// Raised, on any thread, when Windows says something about the network
    /// changed - a cable, a Wi-Fi join, an address - so the next check need not
    /// wait for its turn.
    /// </summary>
    event EventHandler? Changed;
}

/// <summary>
/// One check of the connection: the facts, and how the router and the internet
/// answered.
/// </summary>
/// <param name="At">When it was taken.</param>
/// <param name="Facts">What Windows said.</param>
/// <param name="Router">How long the router took to answer, or nothing when it did not.</param>
/// <param name="Internet">How long the internet target took, or nothing.</param>
/// <param name="InternetChecked">Whether an internet target was pinged at all.</param>
public readonly record struct ConnectionSample(
    DateTimeOffset At,
    ConnectionFacts Facts,
    TimeSpan? Router,
    TimeSpan? Internet,
    bool InternetChecked)
{
    /// <summary>Whether the router was there to be pinged.</summary>
    public bool RouterChecked => Facts.Gateway is not null;
}
