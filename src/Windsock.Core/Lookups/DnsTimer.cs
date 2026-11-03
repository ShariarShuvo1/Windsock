using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Windsock.Core.Lookups;

/// <summary>The parts of a DNS reply Windsock reads.</summary>
/// <param name="Id">The query id it answers.</param>
/// <param name="ResponseCode">RCODE: 0 no error, 2 server failure, 3 no such name, 5 refused.</param>
/// <param name="Answers">How many answer records it carries.</param>
public readonly record struct DnsReply(ushort Id, int ResponseCode, int Answers);

/// <summary>Writes and reads the few bytes of DNS a timing query needs (RFC 1035 section 4).</summary>
public static class DnsPacket
{
    /// <summary>QTYPE NS.</summary>
    public const ushort NameServers = 2;

    /// <summary>QTYPE A.</summary>
    public const ushort FourAddress = 1;

    /// <summary>
    /// A standard query with recursion desired: a 12-byte header (id, flags
    /// 0x0100, one question), the name as length-prefixed labels ending in a
    /// zero, the type and class IN.
    /// </summary>
    public static byte[] Query(ushort id, string name, ushort type)
    {
        ArgumentNullException.ThrowIfNull(name);

        string[] labels = name.Trim('.').Length == 0 ? [] : name.Trim('.').Split('.');
        int length = 12 + labels.Sum(label => 1 + label.Length) + 1 + 4;
        byte[] packet = new byte[length];

        BinaryPrimitives.WriteUInt16BigEndian(packet, id);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 1);

        int at = 12;

        foreach (string label in labels)
        {
            if (label.Length is 0 or > 63 || !label.All(char.IsAscii))
            {
                throw new ArgumentException("Not a name DNS can carry.", nameof(name));
            }

            packet[at++] = (byte)label.Length;

            foreach (char each in label)
            {
                packet[at++] = (byte)each;
            }
        }

        packet[at++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at), type);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at + 2), 1);
        return packet;
    }

    /// <summary>Reads a reply's header, or nothing when it is not a reply.</summary>
    public static DnsReply? Read(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 12 || (reply[2] & 0x80) == 0)
        {
            return null;
        }

        return new DnsReply(
            BinaryPrimitives.ReadUInt16BigEndian(reply),
            reply[3] & 0x0F,
            BinaryPrimitives.ReadUInt16BigEndian(reply[6..]));
    }
}

/// <summary>How long one DNS server took to answer.</summary>
/// <param name="Server">The server asked.</param>
/// <param name="Sent">Queries sent.</param>
/// <param name="Answered">Queries answered.</param>
/// <param name="FirstMs">The first answer's time: the one most likely to have needed the server to ask further.</param>
/// <param name="MedianMs">The median of the answers after the first, in milliseconds.</param>
/// <param name="BestMs">The quickest answer.</param>
/// <param name="Problem">What went wrong, when nothing answered.</param>
public sealed record DnsTiming(IPAddress Server, int Sent, int Answered, double? FirstMs, double? MedianMs, double? BestMs, string? Problem);

/// <summary>Times DNS servers directly, on request.</summary>
/// <remarks>
/// <para>
/// Each query asks for the root zone's name servers (". NS"), which names no
/// site at all, so the server learns nothing about what this PC visits. It goes
/// straight to the server over UDP port 53, past Windows' own cache, so the time
/// is the server's and not the cache's.
/// </para>
/// <para>
/// Measured on 2026-10-05 against a home router's DNS: 43 to 62 ms for the root
/// query (125 ms the first time) while an ICMP echo to the same router took
/// under a millisecond. A router's DNS forwards to the provider; the difference
/// is that trip, which is why the page shows both.
/// </para>
/// </remarks>
public static class DnsTimer
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

    /// <summary>Times one server with a few queries, a quarter of a second apart.</summary>
    public static async Task<DnsTiming> TimeAsync(IPAddress server, int queries = 5, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        queries = Math.Clamp(queries, 2, 10);
        List<double> times = [];
        double? first = null;
        int sent = 0;

        try
        {
            using UdpClient client = new(server.AddressFamily);
            client.Connect(server, 53);

            for (int each = 0; each < queries; each++)
            {
                if (each > 0)
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }

                ushort id = (ushort)RandomNumberGenerator.GetInt32(0, 65536);
                byte[] query = DnsPacket.Query(id, ".", DnsPacket.NameServers);
                long started = Stopwatch.GetTimestamp();

                await client.SendAsync(query, cancellationToken).ConfigureAwait(false);
                sent++;

                if (await ReceiveAsync(client, id, cancellationToken).ConfigureAwait(false))
                {
                    double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                    if (each == 0)
                    {
                        first = ms;
                    }
                    else
                    {
                        times.Add(ms);
                    }
                }
            }
        }
        catch (SocketException ex)
        {
            return new DnsTiming(server, sent, times.Count + (first is null ? 0 : 1), first, Median(times), Best(first, times), ex.Message);
        }

        int answered = times.Count + (first is null ? 0 : 1);

        return new DnsTiming(
            server,
            sent,
            answered,
            first,
            Median(times),
            Best(first, times),
            answered == 0 ? "The server did not answer." : null);
    }

    private static async Task<bool> ReceiveAsync(UdpClient client, ushort id, CancellationToken cancellationToken)
    {
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(Patience);

        try
        {
            while (true)
            {
                UdpReceiveResult received = await client.ReceiveAsync(wait.Token).ConfigureAwait(false);

                // Anything not answering this query - a late reply to the last - is ignored.
                if (DnsPacket.Read(received.Buffer) is { } reply && reply.Id == id)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>The median of some times, or nothing when there are none.</summary>
    public static double? Median(IReadOnlyList<double> times)
    {
        ArgumentNullException.ThrowIfNull(times);

        if (times.Count == 0)
        {
            return null;
        }

        double[] sorted = [.. times.Order()];
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static double? Best(double? first, List<double> times)
    {
        IEnumerable<double> all = first is { } value ? times.Append(value) : times;
        return all.Any() ? all.Min() : null;
    }
}
