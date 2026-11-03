using System.Net;
using Windsock.Core.Adapters;
using Windsock.Core.Settings;

namespace Windsock.Core.Lookups;

/// <summary>The NAT test's whole answer: the router's view, and the STUN server's when that was allowed.</summary>
/// <param name="Router">What the router said its outside address is, by NAT-PMP.</param>
/// <param name="Stun">What the STUN server saw, or nothing when its switch is off.</param>
/// <param name="CarrierNat">The carrier-grade NAT verdict from everything known.</param>
public sealed record NatCheck(RouterOutside Router, NatTestResult? Stun, CarrierNatVerdict CarrierNat);

/// <summary>
/// The on-request probes behind the Connection pages' buttons, each holding to
/// its switch here rather than trusting a page to: DNS timing and the NAT test.
/// </summary>
public sealed class ConnectionProbes(WindsockSettings settings, AdapterMonitor adapters, PublicAddressLookup publicAddress)
{
    /// <summary>The STUN server the NAT test would ask, as host:port.</summary>
    public string StunServer
    {
        get
        {
            (string host, int port) = StunNatTest.ServerFor(settings.ConnectionTools.StunServer, settings.ConnectionTools.StunCustomServer);
            return string.Concat(host, ":", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Times every DNS server the adapters carrying traffic use, or nothing when
    /// the switch is off.
    /// </summary>
    public async Task<IReadOnlyList<DnsTiming>> TimeDnsAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.ConnectionTools.DnsTiming)
        {
            return [];
        }

        AdapterReading reading = await adapters.ReadNowAsync().ConfigureAwait(false);
        IPAddress[] servers = [.. reading.Snapshot.Adapters
            .Where(each => each.IsUp && each.HasGateway)
            .SelectMany(each => each.DnsServers)
            .Select(each => each.Address)
            .Distinct()
            .Take(6)];

        List<DnsTiming> timings = [];

        foreach (IPAddress server in servers)
        {
            timings.Add(await DnsTimer.TimeAsync(server, cancellationToken: cancellationToken).ConfigureAwait(false));
        }

        return timings;
    }

    /// <summary>
    /// Asks the router for its outside address, and - only when the NAT test's
    /// switch is on - the chosen STUN server how the NAT behaves; then weighs it
    /// all for carrier-grade NAT, with the public address if one was looked up.
    /// </summary>
    public async Task<NatCheck> CheckNatAsync(IReadOnlyList<Networking.PathHop>? route = null, CancellationToken cancellationToken = default)
    {
        AdapterReading reading = await adapters.ReadNowAsync().ConfigureAwait(false);
        AdapterDetails? carrier = reading.Snapshot.Physical ?? reading.Snapshot.Carrier;
        IPAddress? gateway = carrier?.Gateways.FirstOrDefault(each => each.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

        RouterOutside router = gateway is null
            ? new RouterOutside(null, -1)
            : await NatPmp.AskAsync(gateway, cancellationToken).ConfigureAwait(false);

        NatTestResult? stun = null;

        if (settings.ConnectionTools.StunTest)
        {
            (string host, int port) = StunNatTest.ServerFor(settings.ConnectionTools.StunServer, settings.ConnectionTools.StunCustomServer);
            stun = await StunNatTest.RunAsync(host, port, cancellationToken).ConfigureAwait(false);
        }

        IPAddress? seen = stun?.Mapped?.Address ?? publicAddress.Latest?.Four;
        return new NatCheck(router, stun, CarrierNatJudge.Judge(reading.Snapshot, router.Address, seen, route));
    }
}
