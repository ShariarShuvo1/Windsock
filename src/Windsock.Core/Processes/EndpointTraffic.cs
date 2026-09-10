using System.Buffers.Binary;
using System.Net;

namespace Windsock.Core.Processes;

/// <summary>
/// One far end of a conversation: an address and a port, as a value.
/// </summary>
public readonly record struct EndpointKey(ulong High, ulong Low, int Port, bool IsIpv6)
{
    /// <summary>Builds a key from an address, without allocating.</summary>
    public static EndpointKey From(IPAddress? address, int port)
    {
        if (address is null)
        {
            return default;
        }

        Span<byte> bytes = stackalloc byte[16];

        if (!address.TryWriteBytes(bytes, out int written))
        {
            return default;
        }

        return written == 4
            ? new EndpointKey(0, BinaryPrimitives.ReadUInt32BigEndian(bytes), port, IsIpv6: false)
            : new EndpointKey(
                BinaryPrimitives.ReadUInt64BigEndian(bytes),
                BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]),
                port,
                IsIpv6: true);
    }

    /// <summary>Whether the key names an address at all.</summary>
    public bool HasAddress => High != 0 || Low != 0;

    /// <summary>Rebuilds the address this key was made from.</summary>
    public IPAddress ToAddress()
    {
        Span<byte> bytes = stackalloc byte[16];

        if (!IsIpv6)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)Low);
            return new IPAddress(bytes[..4]);
        }

        BinaryPrimitives.WriteUInt64BigEndian(bytes, High);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], Low);
        return new IPAddress(bytes);
    }

    /// <summary>The address as text, in its usual notation.</summary>
    public string Address() => ToAddress().ToString();
}

/// <summary>
/// What one process has moved to and from one far end.
/// </summary>
public readonly record struct EndpointTraffic(
    long BytesSent,
    long BytesReceived,
    long Packets,
    DateTimeOffset LastSeen);

/// <summary>
/// Breaks a watched process's traffic down by the far end it is talking to.
/// </summary>
public interface IProcessEndpointSource
{
    bool CanAttribute { get; }

    void Watch(int processId);

    void Unwatch(int processId);

    void CopyEndpoints(int processId, Dictionary<EndpointKey, EndpointTraffic> destination);
}
