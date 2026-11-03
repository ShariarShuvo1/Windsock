using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Networking.NetworkListManager;
using Windows.Win32.System.Com;

namespace Windsock.Core.Adapters;

/// <summary>
/// Windows' own verdict on reaching things, per address family, from the
/// Network List Manager's connectivity flags (NLM_CONNECTIVITY in netlistmgr.h).
/// </summary>
/// <param name="Flags">The flags as Windows gave them, or nothing when it could not be asked.</param>
public readonly record struct WindowsConnectivity(uint? Flags)
{
    /// <summary>NLM_CONNECTIVITY_IPV4_NOTRAFFIC.</summary>
    public const uint FourNoTraffic = 0x1;

    /// <summary>NLM_CONNECTIVITY_IPV6_NOTRAFFIC.</summary>
    public const uint SixNoTraffic = 0x2;

    /// <summary>NLM_CONNECTIVITY_IPV4_SUBNET.</summary>
    public const uint FourSubnet = 0x10;

    /// <summary>NLM_CONNECTIVITY_IPV4_LOCALNETWORK.</summary>
    public const uint FourLocal = 0x20;

    /// <summary>NLM_CONNECTIVITY_IPV4_INTERNET.</summary>
    public const uint FourInternet = 0x40;

    /// <summary>NLM_CONNECTIVITY_IPV6_SUBNET.</summary>
    public const uint SixSubnet = 0x100;

    /// <summary>NLM_CONNECTIVITY_IPV6_LOCALNETWORK.</summary>
    public const uint SixLocal = 0x200;

    /// <summary>NLM_CONNECTIVITY_IPV6_INTERNET.</summary>
    public const uint SixInternet = 0x400;

    /// <summary>Whether Windows says IPv4 reaches the internet.</summary>
    public bool FourReachesInternet => Flags is { } flags && (flags & FourInternet) != 0;

    /// <summary>Whether Windows says IPv6 reaches the internet.</summary>
    public bool SixReachesInternet => Flags is { } flags && (flags & SixInternet) != 0;

    /// <summary>Whether Windows says IPv6 reaches anything beyond this PC.</summary>
    public bool SixReachesAnything => Flags is { } flags && (flags & (SixSubnet | SixLocal | SixInternet)) != 0;
}

/// <summary>What can honestly be said about IPv6 on this connection.</summary>
public enum Ipv6State
{
    /// <summary>Windows could not be asked.</summary>
    Unknown,

    /// <summary>No IPv6 address the internet could reach, and Windows sees no IPv6 traffic.</summary>
    NotAvailable,

    /// <summary>An IPv6 address and router are in place, but Windows has not confirmed the internet over it.</summary>
    ConfiguredNotConfirmed,

    /// <summary>Windows reports the internet reachable over IPv6.</summary>
    Working,
}

/// <summary>Decides <see cref="Ipv6State"/> from what Windows reports, with no traffic of Windsock's own.</summary>
/// <remarks>
/// Windows probes IPv4 and IPv6 separately when it checks for the internet
/// (the NCSI probes to msftconnecttest.com), and reports each family on its
/// own. That verdict is the proof; an address and a router only show IPv6 is
/// set up. Measured on the development machine, an IPv4-only network:
/// <c>0x42</c>, IPv4 internet and IPv6 no traffic.
/// </remarks>
public static class Ipv6Judge
{
    /// <summary>The verdict for a reading of Windows and the adapters.</summary>
    public static Ipv6State Judge(WindowsConnectivity windows, AdapterSnapshot adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        if (windows.SixReachesInternet)
        {
            return Ipv6State.Working;
        }

        bool configured = adapters.Adapters.Any(each => each.IsUp && each.HasPublicSix && each.HasSixGateway);

        if (configured)
        {
            return Ipv6State.ConfiguredNotConfirmed;
        }

        return windows.Flags is null && adapters.Adapters.Count == 0 ? Ipv6State.Unknown : Ipv6State.NotAvailable;
    }

    /// <summary>The verdict in a few plain words.</summary>
    public static string Describe(Ipv6State state) => state switch
    {
        Ipv6State.Working => "Working: Windows reaches the internet over IPv6",
        Ipv6State.ConfiguredNotConfirmed => "Set up, but Windows has not confirmed the internet over it",
        Ipv6State.NotAvailable => "Not available on this connection",
        _ => "Not known",
    };
}

/// <summary>Asks the Network List Manager for its connectivity flags.</summary>
public interface IWindowsConnectivitySource
{
    /// <summary>The flags now. Never throws.</summary>
    WindowsConnectivity Read();
}

/// <summary>Reads <see cref="WindowsConnectivity"/> through INetworkListManager.</summary>
/// <remarks>
/// Asking sends nothing: Windows answers from the verdict its own probes
/// already reached. A fresh COM object each time, released at once; the call
/// is made when the page refreshes, not on a timer.
/// </remarks>
public sealed class NetworkListConnectivity : IWindowsConnectivitySource
{
    /// <inheritdoc />
    public WindowsConnectivity Read()
    {
        object? made = null;

        try
        {
            if (PInvoke.CoCreateInstance(typeof(NetworkListManager).GUID, null, CLSCTX.CLSCTX_ALL, out INetworkListManager manager).Failed)
            {
                return new WindowsConnectivity(null);
            }

            made = manager;
            return new WindowsConnectivity((uint)manager.GetConnectivity());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return new WindowsConnectivity(null);
        }
        finally
        {
            if (made is not null && Marshal.IsComObject(made))
            {
                Marshal.ReleaseComObject(made);
            }
        }
    }
}
