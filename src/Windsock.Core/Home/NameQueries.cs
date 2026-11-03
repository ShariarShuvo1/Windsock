using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Windsock.Core.Home;

/// <summary>
/// A NetBIOS node status query (RFC 1002, 4.2.17 and 4.2.18): asks one device,
/// on UDP 137, for the names it has registered. Windows PCs and Samba servers
/// answer; little else does.
/// </summary>
public static class NetBiosStatus
{
    /// <summary>The NetBIOS name service port.</summary>
    public const int Port = 137;

    private const ushort StatusType = 0x0021;
    private const ushort InternetClass = 0x0001;
    private const ushort GroupFlag = 0x8000;

    /// <summary>
    /// The 50-byte request: a 12-byte header with one question, the name "*"
    /// first-level encoded ("CKAAAA...", 32 characters behind a length of 32,
    /// then the empty root), NBSTAT and class IN.
    /// </summary>
    public static byte[] Request(ushort id)
    {
        byte[] packet = new byte[50];
        BinaryPrimitives.WriteUInt16BigEndian(packet, id);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 1);
        packet[12] = 32;

        Span<byte> name = stackalloc byte[16];
        name[0] = (byte)'*';

        for (int index = 0; index < 16; index++)
        {
            packet[13 + (2 * index)] = (byte)('A' + (name[index] >> 4));
            packet[14 + (2 * index)] = (byte)('A' + (name[index] & 0xF));
        }

        packet[45] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(46), StatusType);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(48), InternetClass);
        return packet;
    }

    /// <summary>
    /// The device's own name from an answer: the first unique name with the
    /// workstation suffix (0x00), spaces trimmed. Nothing for an answer that is
    /// not one, or that names no workstation.
    /// </summary>
    public static string? Parse(ReadOnlySpan<byte> answer, ushort id)
    {
        if (answer.Length < 12
            || BinaryPrimitives.ReadUInt16BigEndian(answer) != id
            || (answer[2] & 0x80) == 0
            || BinaryPrimitives.ReadUInt16BigEndian(answer[6..]) == 0)
        {
            return null;
        }

        int at = DnsWire.SkipName(answer, 12);

        if (at < 0 || at + 10 > answer.Length || BinaryPrimitives.ReadUInt16BigEndian(answer[at..]) != StatusType)
        {
            return null;
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(answer[(at + 8)..]);
        at += 10;

        if (length < 1 || at + length > answer.Length)
        {
            return null;
        }

        int names = answer[at++];

        for (int index = 0; index < names && at + 18 <= answer.Length; index++, at += 18)
        {
            ReadOnlySpan<byte> entry = answer.Slice(at, 18);
            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(entry[16..]);

            if (entry[15] != 0x00 || (flags & GroupFlag) != 0)
            {
                continue;
            }

            string name = Encoding.ASCII.GetString(entry[..15]).TrimEnd(' ', '\0');

            if (DeviceNames.Clean(name) is { } clean)
            {
                return clean;
            }
        }

        return null;
    }
}

/// <summary>
/// A reverse lookup (a PTR query for <c>d.c.b.a.in-addr.arpa</c>) put to the
/// router's own DNS server, which often knows the names devices gave it when
/// it handed them their addresses. Only ever sent to the router itself, and with
/// recursion switched off (RD clear), so the router answers from what it holds
/// rather than passing the question on to the provider's DNS servers, as a
/// dnsmasq-style router would for an in-addr.arpa name it does not know.
/// </summary>
public static class ReversePointer
{
    /// <summary>The DNS port.</summary>
    public const int Port = 53;

    private const ushort PointerType = 12;

    /// <summary>The query for an IPv4 address: a standard query with every flag clear, recursion not desired.</summary>
    public static byte[] Request(ushort id, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Only IPv4 addresses are looked up this way.", nameof(address));
        }

        byte[] octets = address.GetAddressBytes();
        List<string> labels = [.. octets.Reverse().Select(each => each.ToString(CultureInfo.InvariantCulture)), "in-addr", "arpa"];

        using MemoryStream packet = new();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0000);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        packet.Write(header);

        foreach (string label in labels)
        {
            packet.WriteByte((byte)label.Length);
            packet.Write(Encoding.ASCII.GetBytes(label));
        }

        packet.WriteByte(0);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail, PointerType);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1);
        packet.Write(tail);

        return packet.ToArray();
    }

    /// <summary>The first name an answer gives, or nothing.</summary>
    public static string? Parse(ReadOnlySpan<byte> answer, ushort id)
    {
        if (answer.Length < 12
            || BinaryPrimitives.ReadUInt16BigEndian(answer) != id
            || (answer[2] & 0x80) == 0
            || (answer[3] & 0x0F) != 0)
        {
            return null;
        }

        int questions = BinaryPrimitives.ReadUInt16BigEndian(answer[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(answer[6..]);
        int at = 12;

        for (int index = 0; index < questions; index++)
        {
            at = DnsWire.SkipName(answer, at);

            if (at < 0 || at + 4 > answer.Length)
            {
                return null;
            }

            at += 4;
        }

        for (int index = 0; index < answers; index++)
        {
            at = DnsWire.SkipName(answer, at);

            if (at < 0 || at + 10 > answer.Length)
            {
                return null;
            }

            ushort type = BinaryPrimitives.ReadUInt16BigEndian(answer[at..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(answer[(at + 8)..]);
            at += 10;

            if (at + length > answer.Length)
            {
                return null;
            }

            if (type == PointerType && DnsWire.ReadName(answer, at) is { } name)
            {
                return DeviceNames.FromHostName(name);
            }

            at += length;
        }

        return null;
    }
}

/// <summary>Reading names in DNS's wire format (RFC 1035, 4.1.4), compression included.</summary>
public static class DnsWire
{
    private const int MostJumps = 16;

    /// <summary>Where the field after a name starts, or -1 for a name that runs off the end.</summary>
    public static int SkipName(ReadOnlySpan<byte> packet, int at)
    {
        while (at < packet.Length)
        {
            byte length = packet[at];

            if (length == 0)
            {
                return at + 1;
            }

            if ((length & 0xC0) == 0xC0)
            {
                return at + 2 <= packet.Length ? at + 2 : -1;
            }

            if ((length & 0xC0) != 0)
            {
                return -1;
            }

            at += 1 + length;
        }

        return -1;
    }

    /// <summary>The dotted name starting at an offset, following pointers, or nothing for a broken one.</summary>
    public static string? ReadName(ReadOnlySpan<byte> packet, int at)
    {
        StringBuilder name = new();
        int jumps = 0;

        while (at >= 0 && at < packet.Length)
        {
            byte length = packet[at];

            if (length == 0)
            {
                return name.ToString();
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (at + 1 >= packet.Length || ++jumps > MostJumps)
                {
                    return null;
                }

                at = ((length & 0x3F) << 8) | packet[at + 1];
                continue;
            }

            if ((length & 0xC0) != 0 || at + 1 + length > packet.Length)
            {
                return null;
            }

            if (name.Length > 0)
            {
                name.Append('.');
            }

            name.Append(Encoding.UTF8.GetString(packet.Slice(at + 1, length)));
            at += 1 + length;
        }

        return null;
    }
}

/// <summary>What is kept of a name a device gave, which is text from the network and trusted for nothing.</summary>
public static class DeviceNames
{
    private const int Longest = 63;

    /// <summary>
    /// A name with control characters and surrounding space removed, cut to 63
    /// characters, or nothing when nothing printable is left.
    /// </summary>
    public static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        StringBuilder kept = new(Math.Min(name.Length, Longest));

        foreach (char each in name.Trim())
        {
            if (kept.Length == Longest)
            {
                break;
            }

            if (!char.IsControl(each) && each != '�')
            {
                kept.Append(each);
            }
        }

        string clean = kept.ToString().Trim();
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>
    /// The device part of a host name: "Johns-iPhone" from "Johns-iPhone.lan."
    /// or "Johns-iPhone.local". Nothing for a name that is only an address
    /// written backwards, as some routers answer.
    /// </summary>
    public static string? FromHostName(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        string trimmed = host.Trim().TrimEnd('.');

        if (trimmed.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(trimmed, out _))
        {
            return null;
        }

        int dot = trimmed.IndexOf('.', StringComparison.Ordinal);
        return Clean(dot > 0 ? trimmed[..dot] : trimmed);
    }
}
