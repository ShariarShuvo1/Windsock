using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Windsock.Core.Lookups;

/// <summary>What a STUN server answered.</summary>
/// <param name="Mapped">The address and port the server saw the request come from.</param>
/// <param name="Other">The server's alternate address (RFC 5780 OTHER-ADDRESS), when it has one.</param>
/// <param name="Origin">The address the answer was sent from (RESPONSE-ORIGIN), when given.</param>
/// <param name="ErrorCode">The error code of an error answer.</param>
/// <param name="Software">The server's SOFTWARE attribute.</param>
public sealed record StunAnswer(IPEndPoint? Mapped, IPEndPoint? Other, IPEndPoint? Origin, int? ErrorCode, string? Software);

/// <summary>Writes Binding requests and reads their answers (RFC 8489, RFC 5780).</summary>
/// <remarks>
/// A message is a 20-byte header - two zero bits and a 14-bit type, a 16-bit
/// length of what follows, the magic cookie 0x2112A442 and a 96-bit
/// transaction id - then attributes as type, length, value padded to four
/// bytes. A Binding request is type 0x0001, its success answer 0x0101 and its
/// error 0x0111. XOR-MAPPED-ADDRESS (0x0020) is the address XORed with the
/// cookie (and, for IPv6, the transaction id), and the port with the cookie's
/// top 16 bits.
/// </remarks>
public static class StunMessage
{
    /// <summary>The magic cookie.</summary>
    public const uint Cookie = 0x2112A442;

    /// <summary>Binding request.</summary>
    public const ushort BindingRequest = 0x0001;

    /// <summary>Binding success answer.</summary>
    public const ushort BindingSuccess = 0x0101;

    /// <summary>Binding error answer.</summary>
    public const ushort BindingError = 0x0111;

    /// <summary>MAPPED-ADDRESS.</summary>
    public const ushort MappedAddress = 0x0001;

    /// <summary>CHANGE-REQUEST (RFC 5780).</summary>
    public const ushort ChangeRequest = 0x0003;

    /// <summary>ERROR-CODE.</summary>
    public const ushort ErrorCode = 0x0009;

    /// <summary>XOR-MAPPED-ADDRESS.</summary>
    public const ushort XorMappedAddress = 0x0020;

    /// <summary>SOFTWARE.</summary>
    public const ushort Software = 0x8022;

    /// <summary>RESPONSE-ORIGIN (RFC 5780).</summary>
    public const ushort ResponseOrigin = 0x802B;

    /// <summary>OTHER-ADDRESS (RFC 5780).</summary>
    public const ushort OtherAddress = 0x802C;

    /// <summary>A fresh 96-bit transaction id.</summary>
    public static byte[] NewTransaction() => RandomNumberGenerator.GetBytes(12);

    /// <summary>A Binding request, asking the answer to come from another address or port when told.</summary>
    public static byte[] Binding(ReadOnlySpan<byte> transaction, bool changeAddress = false, bool changePort = false)
    {
        if (transaction.Length != 12)
        {
            throw new ArgumentException("A transaction id is 12 bytes.", nameof(transaction));
        }

        bool change = changeAddress || changePort;
        byte[] message = new byte[20 + (change ? 8 : 0)];

        BinaryPrimitives.WriteUInt16BigEndian(message, BindingRequest);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)(message.Length - 20));
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), Cookie);
        transaction.CopyTo(message.AsSpan(8));

        if (change)
        {
            BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(20), ChangeRequest);
            BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(22), 4);
            BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(24), (changeAddress ? 0x4u : 0) | (changePort ? 0x2u : 0));
        }

        return message;
    }

    /// <summary>Reads an answer to the given transaction, or nothing when it is not one.</summary>
    public static StunAnswer? Read(ReadOnlySpan<byte> message, ReadOnlySpan<byte> transaction)
    {
        if (message.Length < 20 || transaction.Length != 12)
        {
            return null;
        }

        ushort type = BinaryPrimitives.ReadUInt16BigEndian(message);
        int length = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);

        if ((type & 0xC000) != 0
            || type is not (BindingSuccess or BindingError)
            || BinaryPrimitives.ReadUInt32BigEndian(message[4..]) != Cookie
            || !message.Slice(8, 12).SequenceEqual(transaction)
            || 20 + length > message.Length)
        {
            return null;
        }

        IPEndPoint? mapped = null;
        IPEndPoint? xorMapped = null;
        IPEndPoint? other = null;
        IPEndPoint? origin = null;
        int? error = null;
        string? software = null;

        ReadOnlySpan<byte> attributes = message.Slice(20, length);
        ReadOnlySpan<byte> header = message[..20];
        int at = 0;

        while (at + 4 <= attributes.Length)
        {
            ushort kind = BinaryPrimitives.ReadUInt16BigEndian(attributes[at..]);
            int size = BinaryPrimitives.ReadUInt16BigEndian(attributes[(at + 2)..]);

            if (at + 4 + size > attributes.Length)
            {
                break;
            }

            ReadOnlySpan<byte> value = attributes.Slice(at + 4, size);

            switch (kind)
            {
                case MappedAddress:
                    mapped = Address(value, null);
                    break;

                case XorMappedAddress:
                    xorMapped = Address(value, header);
                    break;

                case OtherAddress:
                    other = Address(value, null);
                    break;

                case ResponseOrigin:
                    origin = Address(value, null);
                    break;

                case ErrorCode when size >= 4:
                    error = ((value[2] & 0x07) * 100) + value[3];
                    break;

                case Software:
                    software = Encoding.UTF8.GetString(value).TrimEnd();
                    break;
            }

            at += 4 + ((size + 3) & ~3);
        }

        return new StunAnswer(xorMapped ?? mapped, other, origin, type == BindingError ? error ?? 0 : null, software);
    }

    /// <summary>
    /// Reads an address attribute: a zero byte, the family (1 IPv4, 2 IPv6),
    /// the port, the address; XORed with the header when <paramref name="xorWith"/> is given.
    /// </summary>
    public static IPEndPoint? Address(ReadOnlySpan<byte> value, ReadOnlySpan<byte> xorWith)
    {
        if (value.Length < 8)
        {
            return null;
        }

        int family = value[1];
        int addressLength = family switch
        {
            1 => 4,
            2 => 16,
            _ => 0,
        };

        if (addressLength == 0 || value.Length < 4 + addressLength)
        {
            return null;
        }

        int port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
        Span<byte> address = stackalloc byte[addressLength];
        value.Slice(4, addressLength).CopyTo(address);

        if (!xorWith.IsEmpty)
        {
            // The cookie's top 16 bits for the port; the cookie and then the
            // transaction id, byte by byte, for the address.
            port ^= (int)(Cookie >> 16);

            for (int each = 0; each < addressLength; each++)
            {
                address[each] ^= xorWith[4 + each];
            }
        }

        return new IPEndPoint(new IPAddress(address), port);
    }
}

/// <summary>How a NAT gives out outside addresses (RFC 4787, as RFC 5780 tests it).</summary>
public enum NatMapping
{
    /// <summary>Not determined.</summary>
    Unknown,

    /// <summary>No NAT at all: the server saw this PC's own address and port.</summary>
    None,

    /// <summary>The same outside address and port whoever this PC talks to. Best for games and calls.</summary>
    EndpointIndependent,

    /// <summary>A new outside port for each address talked to.</summary>
    AddressDependent,

    /// <summary>A new outside port for each address and port talked to: what older guides call symmetric NAT.</summary>
    AddressAndPortDependent,
}

/// <summary>Which outside senders a NAT lets back in.</summary>
public enum NatFiltering
{
    /// <summary>Not determined.</summary>
    Unknown,

    /// <summary>Anyone, once a mapping exists.</summary>
    EndpointIndependent,

    /// <summary>Only addresses this PC has sent to.</summary>
    AddressDependent,

    /// <summary>Only the exact addresses and ports this PC has sent to.</summary>
    AddressAndPortDependent,
}

/// <summary>What the NAT test found.</summary>
/// <param name="Server">The server asked, host:port.</param>
/// <param name="Local">The local address and port used.</param>
/// <param name="Mapped">The outside address and port the server saw.</param>
/// <param name="Mapping">How the NAT maps.</param>
/// <param name="Filtering">How it filters.</param>
/// <param name="Problem">What went wrong, when the test could not finish.</param>
public sealed record NatTestResult(string Server, IPEndPoint? Local, IPEndPoint? Mapped, NatMapping Mapping, NatFiltering Filtering, string? Problem)
{
    /// <summary>A plain summary: "Open", "Moderate", "Strict" in the sense consoles use, or nothing when unknown.</summary>
    /// <remarks>
    /// Console makers define their own labels; this is the common reading, not
    /// any one console's rule: endpoint-independent mapping with
    /// endpoint-independent filtering (or no NAT) is open, endpoint-independent
    /// mapping with stricter filtering moderate, anything else strict.
    /// </remarks>
    public string? Summary => Mapping switch
    {
        NatMapping.None => "Open (no NAT)",
        NatMapping.EndpointIndependent when Filtering == NatFiltering.EndpointIndependent => "Open",
        NatMapping.EndpointIndependent => "Moderate",
        NatMapping.AddressDependent or NatMapping.AddressAndPortDependent => "Strict",
        _ => null,
    };
}

/// <summary>Runs the RFC 5780 NAT behaviour tests against one STUN server, on request.</summary>
/// <remarks>
/// <para>
/// Every test goes from one local UDP socket, so the NAT keeps one mapping for
/// it. Test I asks the server's main address. A server that names an alternate
/// address (OTHER-ADDRESS) is then asked to answer from its alternate address
/// and port, then from its alternate port, to learn what the NAT lets in; only
/// after that is it asked at its alternate address, to learn how the NAT maps.
/// </para>
/// <para>
/// The order matters. Sending to the alternate address opens the NAT to it, so
/// a filtering test run after the mapping tests is answered through that
/// opening and calls an address-dependent filter endpoint-independent - which
/// read "Open" for a NAT that is not.
/// </para>
/// <para>
/// A server without an alternate address gives the outside address only. Each
/// request is tried three times, half a second, one and two seconds apart. A
/// socket that cannot bind or send - no IPv4 route, a VPN's kill switch, Wi-Fi
/// gone mid-test - ends the test with a problem, never an exception. The
/// server learns this PC's public address and port; nothing else.
/// </para>
/// </remarks>
public static class StunNatTest
{
    /// <summary>What the test says when this PC could not send at all.</summary>
    public const string Unreachable = "This PC could not reach the server: no route to it, or the connection dropped.";

    /// <summary>The servers offered, as host and port.</summary>
    public static (string Host, int Port) ServerFor(Settings.StunServerChoice choice, string custom) => choice switch
    {
        Settings.StunServerChoice.Cloudflare => ("stun.cloudflare.com", 3478),
        Settings.StunServerChoice.Google => ("stun.l.google.com", 19302),
        Settings.StunServerChoice.Custom => ParseServer(custom),
        _ => ("stun.nextcloud.com", 3478),
    };

    /// <summary>Reads "host" or "host:port", 3478 when no port is given.</summary>
    public static (string Host, int Port) ParseServer(string? text)
    {
        string trimmed = (text ?? string.Empty).Trim();
        int colon = trimmed.LastIndexOf(':');

        if (colon > 0 && trimmed.IndexOf(':', StringComparison.Ordinal) == colon
            && int.TryParse(trimmed.AsSpan(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port)
            && port is > 0 and <= 65535)
        {
            return (trimmed[..colon], port);
        }

        return (trimmed, 3478);
    }

    /// <summary>Runs the tests.</summary>
    public static async Task<NatTestResult> RunAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        string server = string.Concat(host, ":", port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (!Settings.ConnectionSettings.IsTarget(host))
        {
            return new NatTestResult(server, null, null, NatMapping.Unknown, NatFiltering.Unknown, "That is not a server name.");
        }

        IPAddress? address;

        try
        {
            IPAddress[] found = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
            address = found.Length > 0 ? found[0] : null;
        }
        catch (SocketException)
        {
            address = null;
        }

        if (address is null)
        {
            return new NatTestResult(server, null, null, NatMapping.Unknown, NatFiltering.Unknown, "The server's name could not be found.");
        }

        try
        {
            using UdpClient client = new(AddressFamily.InterNetwork);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            return await RunAsync(new UdpStunChannel(client), server, address, port, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return new NatTestResult(server, null, null, NatMapping.Unknown, NatFiltering.Unknown, Unreachable);
        }
    }

    /// <summary>The tests in their order, over any channel; sending and receiving are the channel's.</summary>
    internal static async Task<NatTestResult> RunAsync(IStunChannel channel, string server, IPAddress address, int port, CancellationToken cancellationToken)
    {
        IPEndPoint primary = new(address, port);
        StunAnswer? first = await AskAsync(channel, primary, false, false, cancellationToken).ConfigureAwait(false);

        if (first?.Mapped is not { } mapped)
        {
            return new NatTestResult(server, null, null, NatMapping.Unknown, NatFiltering.Unknown, channel.Failed ? Unreachable : "The server did not answer.");
        }

        IPEndPoint? local = channel.LocalTowards(address);

        if (local is not null && mapped.Equals(local))
        {
            return new NatTestResult(server, local, mapped, NatMapping.None, NatFiltering.Unknown, null);
        }

        if (first.Other is not { } other || other.Address.Equals(address))
        {
            return new NatTestResult(server, local, mapped, NatMapping.Unknown, NatFiltering.Unknown, null);
        }

        // Filtering first, while this socket has sent to the primary address alone.
        NatFiltering filtering;

        if (await AskAsync(channel, primary, true, true, cancellationToken).ConfigureAwait(false) is not null)
        {
            filtering = NatFiltering.EndpointIndependent;
        }
        else if (await AskAsync(channel, primary, false, true, cancellationToken).ConfigureAwait(false) is not null)
        {
            filtering = NatFiltering.AddressDependent;
        }
        else
        {
            filtering = channel.Failed ? NatFiltering.Unknown : NatFiltering.AddressAndPortDependent;
        }

        NatMapping mapping = NatMapping.Unknown;
        StunAnswer? second = await AskAsync(channel, new IPEndPoint(other.Address, port), false, false, cancellationToken).ConfigureAwait(false);

        if (second?.Mapped is { } mappedAgain)
        {
            if (mappedAgain.Equals(mapped))
            {
                mapping = NatMapping.EndpointIndependent;
            }
            else
            {
                StunAnswer? third = await AskAsync(channel, other, false, false, cancellationToken).ConfigureAwait(false);
                mapping = third?.Mapped is { } mappedThird && mappedThird.Equals(mappedAgain)
                    ? NatMapping.AddressDependent
                    : third?.Mapped is null ? NatMapping.Unknown : NatMapping.AddressAndPortDependent;
            }
        }

        return new NatTestResult(server, local, mapped, mapping, filtering, channel.Failed ? Unreachable : null);
    }

    private static async Task<StunAnswer?> AskAsync(IStunChannel channel, IPEndPoint to, bool changeAddress, bool changePort, CancellationToken cancellationToken)
    {
        byte[] transaction = StunMessage.NewTransaction();
        byte[] request = StunMessage.Binding(transaction, changeAddress, changePort);
        TimeSpan wait = TimeSpan.FromMilliseconds(500);

        for (int attempt = 0; attempt < 3 && !channel.Failed; attempt++, wait += wait)
        {
            byte[]? received = await channel.ExchangeAsync(request, to, transaction, wait, cancellationToken).ConfigureAwait(false);

            if (received is not null && StunMessage.Read(received, transaction) is { } answer)
            {
                return answer.ErrorCode is null ? answer : null;
            }

            if (channel.Refused)
            {
                return null;
            }
        }

        return null;
    }
}

/// <summary>Where the NAT test's requests go: a real UDP socket, or a script in tests.</summary>
internal interface IStunChannel
{
    /// <summary>Whether sending failed outright, so nothing more can be learned.</summary>
    bool Failed { get; }

    /// <summary>Whether the last request was refused with an ICMP error.</summary>
    bool Refused { get; }

    /// <summary>
    /// Sends a request and waits up to <paramref name="wait"/> for the answer
    /// to its transaction; nothing when none came or sending failed.
    /// </summary>
    Task<byte[]?> ExchangeAsync(byte[] request, IPEndPoint to, byte[] transaction, TimeSpan wait, CancellationToken cancellationToken);

    /// <summary>This PC's own address and port towards the server, or nothing when that cannot be told.</summary>
    IPEndPoint? LocalTowards(IPAddress server);
}

/// <summary>The NAT test's channel over one bound UDP socket.</summary>
internal sealed class UdpStunChannel(UdpClient client) : IStunChannel
{
    public bool Failed { get; private set; }

    public bool Refused { get; private set; }

    public async Task<byte[]?> ExchangeAsync(byte[] request, IPEndPoint to, byte[] transaction, TimeSpan wait, CancellationToken cancellationToken)
    {
        Refused = false;

        try
        {
            await client.SendAsync(request, to, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // No route, a kill switch, the adapter gone: nothing further can be sent.
            Failed = true;
            return null;
        }

        using CancellationTokenSource patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        patience.CancelAfter(wait);

        try
        {
            while (true)
            {
                UdpReceiveResult received = await client.ReceiveAsync(patience.Token).ConfigureAwait(false);

                if (StunMessage.Read(received.Buffer, transaction) is not null)
                {
                    return received.Buffer;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            // An ICMP "port unreachable" from the server's side surfaces here on Windows.
            Refused = true;
            return null;
        }
    }

    public IPEndPoint? LocalTowards(IPAddress server)
    {
        try
        {
            int port = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

            // The socket is bound to any address; which one carries the traffic is
            // what a connected probe socket would pick.
            using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(server, 9);
            return new IPEndPoint(((IPEndPoint)probe.LocalEndPoint!).Address, port);
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
