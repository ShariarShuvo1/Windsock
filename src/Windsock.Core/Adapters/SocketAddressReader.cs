using System.Buffers.Binary;
using System.Net;

namespace Windsock.Core.Adapters;

/// <summary>Reads a Windows SOCKADDR as an address.</summary>
/// <remarks>
/// SOCKADDR_IN: family (2 bytes, little-endian), port (2, network order),
/// address (4). SOCKADDR_IN6: family, port, flow info (4), address (16), scope
/// id (4, little-endian). AF_INET is 2 and AF_INET6 is 23 on Windows (ws2def.h).
/// </remarks>
public static class SocketAddressReader
{
    /// <summary>Windows' AF_INET.</summary>
    public const ushort InterNetwork = 2;

    /// <summary>Windows' AF_INET6.</summary>
    public const ushort InterNetworkV6 = 23;

    /// <summary>The address in a SOCKADDR's bytes, or nothing for a family it is not.</summary>
    public static IPAddress? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2)
        {
            return null;
        }

        ushort family = BinaryPrimitives.ReadUInt16LittleEndian(bytes);

        if (family == InterNetwork && bytes.Length >= 8)
        {
            return new IPAddress(bytes.Slice(4, 4));
        }

        if (family == InterNetworkV6 && bytes.Length >= 24)
        {
            long scope = bytes.Length >= 28 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(24, 4)) : 0;
            return new IPAddress(bytes.Slice(8, 16), scope);
        }

        return null;
    }
}
