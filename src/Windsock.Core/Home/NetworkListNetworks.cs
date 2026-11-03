using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Networking.NetworkListManager;
using Windows.Win32.System.Com;

namespace Windsock.Core.Home;

/// <summary>Reads the connected networks and their class through INetworkListManager.</summary>
/// <remarks>
/// Asking sends nothing and needs no elevation (measured unelevated on the
/// development machine: one network, Private). WinRT's ConnectionProfile has
/// no category at all, which is why this is COM. SetCategory exists on the
/// same interface and changes system state; it is never called.
/// </remarks>
public sealed class NetworkListNetworks : IConnectedNetworkSource
{
    /// <inheritdoc />
    public IReadOnlyList<ConnectedNetwork> Read()
    {
        object? made = null;
        List<object> held = [];

        try
        {
            if (PInvoke.CoCreateInstance(typeof(NetworkListManager).GUID, null, CLSCTX.CLSCTX_ALL, out INetworkListManager manager).Failed)
            {
                return [];
            }

            made = manager;
            Dictionary<Guid, (string Name, NetworkCategory Category, List<string> Adapters)> networks = [];
            IEnumNetworkConnections connections = manager.GetNetworkConnections();
            held.Add(connections);
            INetworkConnection[] one = new INetworkConnection[1];

            for (int guard = 0; guard < 64; guard++)
            {
                uint fetched = 0;
                connections.Next(one, ref fetched);

                if (fetched == 0 || one[0] is not { } connection)
                {
                    break;
                }

                held.Add(connection);

                if (!connection.IsConnected)
                {
                    continue;
                }

                INetwork network = connection.GetNetwork();
                held.Add(network);
                Guid id = network.GetNetworkId();

                if (!networks.TryGetValue(id, out (string Name, NetworkCategory Category, List<string> Adapters) entry))
                {
                    entry = (Take(network.GetName()), (NetworkCategory)(int)network.GetCategory(), []);
                    networks[id] = entry;
                }

                entry.Adapters.Add(connection.GetAdapterId().ToString("B", CultureInfo.InvariantCulture));
            }

            return [.. networks.Select(each => new ConnectedNetwork(
                each.Key.ToString("D", CultureInfo.InvariantCulture),
                each.Value.Name,
                each.Value.Category,
                each.Value.Adapters))];
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return [];
        }
        finally
        {
            foreach (object each in held)
            {
                if (Marshal.IsComObject(each))
                {
                    Marshal.ReleaseComObject(each);
                }
            }

            if (made is not null && Marshal.IsComObject(made))
            {
                Marshal.ReleaseComObject(made);
            }
        }
    }

    /// <summary>The text of a BSTR the caller owns, freeing it.</summary>
    private static string Take(Windows.Win32.Foundation.BSTR text)
    {
        try
        {
            return text.ToString() ?? string.Empty;
        }
        finally
        {
            Marshal.FreeBSTR(text);
        }
    }
}
