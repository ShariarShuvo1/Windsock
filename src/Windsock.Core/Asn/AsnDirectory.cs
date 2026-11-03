using System.Net;

namespace Windsock.Core.Asn;

/// <summary>
/// The one shared <see cref="AsnTable"/>, opened the first time anything asks
/// and shared by everything that names networks: the public address lookup
/// now, the Apps page and the map later. A missing table is no names, never an
/// error.
/// </summary>
public sealed class AsnDirectory(string? path = null) : IDisposable
{
    private readonly Lock _gate = new();
    private AsnTable? _table;
    private bool _opened;
    private bool _disposed;

    /// <summary>The table, or nothing when it is not shipped or will not open.</summary>
    public AsnTable? Table
    {
        get
        {
            lock (_gate)
            {
                if (!_opened && !_disposed)
                {
                    _opened = true;
                    _table = AsnTable.Open(path);
                }

                return _table;
            }
        }
    }

    /// <summary>The network an address belongs to, or nothing.</summary>
    public AutonomousSystem? Find(IPAddress? address) => Table?.Find(address);

    /// <summary>The network an address written as text belongs to, or nothing.</summary>
    public AutonomousSystem? Find(string? address) => Table?.Find(address);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _table?.Dispose();
            _table = null;
        }
    }
}
