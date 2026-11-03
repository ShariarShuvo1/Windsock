using System.Net;
using System.Net.Sockets;
using Windsock.Core.Adapters;
using Windsock.Core.Networking;

namespace Windsock.Core.Lookups;

/// <summary>What can be said about the provider sharing one public address among customers.</summary>
public enum CarrierNatState
{
    /// <summary>Nothing seen either way.</summary>
    Unknown,

    /// <summary>This PC or its router holds a public address: no carrier-grade NAT.</summary>
    No,

    /// <summary>An address in the shared range 100.64.0.0/10 shows it.</summary>
    Yes,

    /// <summary>The router's own outside address is private: a second router or the provider translates again.</summary>
    DoubleNat,

    /// <summary>The address the internet sees differs from the router's outside address: another NAT sits upstream.</summary>
    Upstream,

    /// <summary>A hop just past the router is in the shared range: the usual sign, though not proof.</summary>
    Likely,
}

/// <summary>The verdict and what it rests on.</summary>
/// <param name="State">The verdict.</param>
/// <param name="Evidence">The address that showed it, when one did.</param>
/// <param name="Reason">One sentence saying how it was told.</param>
public sealed record CarrierNatVerdict(CarrierNatState State, IPAddress? Evidence, string Reason);

/// <summary>
/// Tells whether the provider uses carrier-grade NAT, from evidence gathered
/// most privately first: this PC's own addresses and router (nothing sent),
/// the routes the map already walked (nothing new sent), the router's answer
/// to NAT-PMP (stays on the local network), and the public address, only if
/// the person looked it up.
/// </summary>
/// <remarks>
/// RFC 6598 sets aside 100.64.0.0/10 for the link between a provider's NAT and
/// its customers' routers; an address from it anywhere on this side means the
/// provider shares its public addresses. Games and calls that need incoming
/// connections suffer behind it, and port forwarding cannot help.
/// </remarks>
public static class CarrierNatJudge
{
    /// <summary>Weighs the evidence.</summary>
    /// <param name="adapters">This PC's adapters.</param>
    /// <param name="routerOutside">The router's NAT-PMP answer, when it gave one.</param>
    /// <param name="publicAddress">The public IPv4 address, when the person looked it up.</param>
    /// <param name="route">A walked route to anywhere on the internet, when the map has one.</param>
    public static CarrierNatVerdict Judge(
        AdapterSnapshot adapters,
        IPAddress? routerOutside = null,
        IPAddress? publicAddress = null,
        IReadOnlyList<PathHop>? route = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        AdapterDetails? carrier = adapters.Physical ?? adapters.Carrier;

        if (carrier is not null)
        {
            foreach (AdapterAddress address in carrier.FourAddresses)
            {
                if (address.Scope == AddressScope.SharedCarrier)
                {
                    return new CarrierNatVerdict(CarrierNatState.Yes, address.Address, "This PC's own address is in the range providers use behind carrier-grade NAT.");
                }

                if (address.Scope == AddressScope.Public)
                {
                    return new CarrierNatVerdict(CarrierNatState.No, address.Address, "This PC holds a public address itself.");
                }
            }

            foreach (IPAddress gateway in carrier.Gateways)
            {
                if (AddressScopes.Of(gateway) == AddressScope.SharedCarrier)
                {
                    return new CarrierNatVerdict(CarrierNatState.Yes, gateway, "The router's address is in the range providers use behind carrier-grade NAT.");
                }
            }
        }

        if (routerOutside is { AddressFamily: AddressFamily.InterNetwork } outside)
        {
            switch (AddressScopes.Of(outside))
            {
                case AddressScope.SharedCarrier:
                    return new CarrierNatVerdict(CarrierNatState.Yes, outside, "The router's outside address is in the range providers use behind carrier-grade NAT.");

                case AddressScope.Private:
                    return new CarrierNatVerdict(CarrierNatState.DoubleNat, outside, "The router's outside address is private, so something beyond it translates addresses again.");

                case AddressScope.Public when publicAddress is not null && !publicAddress.Equals(outside):
                    return new CarrierNatVerdict(CarrierNatState.Upstream, publicAddress, "The internet sees a different address from the router's outside one, so another NAT sits beyond it.");

                case AddressScope.Public:
                    return new CarrierNatVerdict(CarrierNatState.No, outside, "The router holds a public address.");
            }
        }

        if (route is not null)
        {
            foreach (PathHop hop in route.Take(4))
            {
                if (hop.Address is { } text && IPAddress.TryParse(text, out IPAddress? hopAddress)
                    && AddressScopes.Of(hopAddress) == AddressScope.SharedCarrier)
                {
                    return new CarrierNatVerdict(CarrierNatState.Likely, hopAddress, "A router just past this one is in the carrier-grade NAT range.");
                }
            }
        }

        return new CarrierNatVerdict(CarrierNatState.Unknown, null, "Nothing on this side shows it. The router's outside address would tell.");
    }
}
