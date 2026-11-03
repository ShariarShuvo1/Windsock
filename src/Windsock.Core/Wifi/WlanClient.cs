using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.WiFi;

namespace Windsock.Core.Wifi;

/// <summary>How a Native Wi-Fi call went.</summary>
public enum WlanStatus
{
    Ok,

    /// <summary>The WLAN AutoConfig service is not running, or there is no Wi-Fi at all.</summary>
    NoService,

    /// <summary>The adapter is not connected (ERROR_INVALID_STATE).</summary>
    NotConnected,

    /// <summary>Windows refused: location permission, since Windows 11 24H2, for names and scans.</summary>
    Denied,

    /// <summary>This Windows does not know the request (an older build without opcode 19).</summary>
    NotSupported,

    /// <summary>The radio is switched off.</summary>
    RadioOff,

    /// <summary>Anything else.</summary>
    Failed,

    /// <summary>Not asked: something a scan would disturb, a speed test, is running.</summary>
    Busy,
}

/// <summary>Native Wi-Fi's operations, as raw bytes for the decoders.</summary>
public interface IWlanClient
{
    /// <summary>The Wi-Fi adapters.</summary>
    (WlanStatus Status, IReadOnlyList<WlanInterface> Interfaces) Interfaces();

    /// <summary>Asks an adapter one WLAN_INTF_OPCODE and copies the answer.</summary>
    (WlanStatus Status, byte[]? Data) Query(Guid adapter, uint opcode);

    /// <summary>Asks an adapter to scan. Returns at once; the results come within about four seconds.</summary>
    WlanStatus Scan(Guid adapter);

    /// <summary>The access points the adapter last heard, as a WLAN_BSS_LIST.</summary>
    (WlanStatus Status, byte[]? Data) NetworkList(Guid adapter);

    /// <summary>Windows' own sentence for a WLAN_REASON_CODE, or nothing.</summary>
    string? ReasonText(uint code);

#if !STORE
    /// <summary>A saved profile's XML, asking for the key in plain text.</summary>
    (WlanStatus Status, string? Xml) Profile(Guid adapter, string name);
#endif
}

/// <summary>Talks to WLAN AutoConfig through wlanapi.dll.</summary>
/// <remarks>
/// One client handle, opened when first needed and reopened if the service was
/// restarted under it. Every buffer Windows hands back is copied and freed with
/// WlanFreeMemory before the call returns. Measured unelevated on 2026-10-05
/// with the adapter disconnected: opening works, opcode 6 (state) and 20 (QoS)
/// answer, and opcodes 7, 8 and 19 give ERROR_INVALID_STATE.
/// </remarks>
public sealed class WlanClient : IWlanClient, IDisposable
{
    /// <summary>wlan_intf_opcode_interface_state.</summary>
    public const uint InterfaceStateOpcode = 6;

    /// <summary>wlan_intf_opcode_current_connection, which needs location permission on Windows 11 24H2 and later.</summary>
    public const uint CurrentConnectionOpcode = 7;

    /// <summary>wlan_intf_opcode_realtime_connection_quality, which Microsoft documents as needing no location permission.</summary>
    public const uint RealtimeQualityOpcode = 19;

    private const uint NoError = 0;
    private const uint AccessDenied = 5;
    private const uint InvalidHandle = 6;
    private const uint NotSupportedError = 50;
    private const uint InvalidParameter = 87;
    private const uint ServiceNotActive = 1062;
    private const uint InvalidState = 5023;
    private const uint RadioPowerInvalid = 0x80342002;

    private readonly Lock _gate = new();
    private WlanCloseHandleSafeHandle? _handle;
    private bool _disposed;

    /// <inheritdoc />
    public unsafe (WlanStatus Status, IReadOnlyList<WlanInterface> Interfaces) Interfaces()
    {
        lock (_gate)
        {
            uint result = WithHandle(handle =>
            {
                uint code = PInvoke.WlanEnumInterfaces(handle, out WLAN_INTERFACE_INFO_LIST* found);
                return (code, (nint)found);
            }, out nint pointer);

            WLAN_INTERFACE_INFO_LIST* list = (WLAN_INTERFACE_INFO_LIST*)pointer;

            if (result != NoError || list is null)
            {
                return (Status(result), []);
            }

            try
            {
                uint count = list->dwNumberOfItems;
                int size = 8 + (int)Math.Min(count, 64u) * WifiDecoders.InterfaceInfo;
                return (WlanStatus.Ok, WifiDecoders.Interfaces(new ReadOnlySpan<byte>(list, size)));
            }
            finally
            {
                PInvoke.WlanFreeMemory(list);
            }
        }
    }

    /// <inheritdoc />
    public unsafe (WlanStatus Status, byte[]? Data) Query(Guid adapter, uint opcode)
    {
        lock (_gate)
        {
            uint size = 0;
            uint result = WithHandle(handle =>
            {
                uint code = PInvoke.WlanQueryInterface(handle, adapter, (WLAN_INTF_OPCODE)opcode, out uint length, out void* found);
                size = length;
                return (code, (nint)found);
            }, out nint pointer);

            void* data = (void*)pointer;

            if (result != NoError || data is null)
            {
                return (Status(result), null);
            }

            try
            {
                return (WlanStatus.Ok, new ReadOnlySpan<byte>(data, (int)Math.Min(size, 1u << 20)).ToArray());
            }
            finally
            {
                PInvoke.WlanFreeMemory(data);
            }
        }
    }

    /// <inheritdoc />
    public unsafe WlanStatus Scan(Guid adapter)
    {
        lock (_gate)
        {
            uint result = WithHandle(handle => (PInvoke.WlanScan(handle, adapter, null, null), 0), out _);
            return Status(result);
        }
    }

    /// <inheritdoc />
    public unsafe (WlanStatus Status, byte[]? Data) NetworkList(Guid adapter)
    {
        lock (_gate)
        {
            uint result = WithHandle(handle =>
            {
                uint code = PInvoke.WlanGetNetworkBssList(handle, adapter, null, DOT11_BSS_TYPE.dot11_BSS_type_any, false, out WLAN_BSS_LIST* found);
                return (code, (nint)found);
            }, out nint pointer);

            WLAN_BSS_LIST* list = (WLAN_BSS_LIST*)pointer;

            if (result != NoError || list is null)
            {
                return (Status(result), null);
            }

            try
            {
                return (WlanStatus.Ok, new ReadOnlySpan<byte>(list, (int)Math.Min(list->dwTotalSize, 4u << 20)).ToArray());
            }
            finally
            {
                PInvoke.WlanFreeMemory(list);
            }
        }
    }

    /// <inheritdoc />
    public unsafe string? ReasonText(uint code)
    {
        char* buffer = stackalloc char[512];

        return PInvoke.WlanReasonCodeToString(code, 512, new PWSTR(buffer)) == NoError
            ? new PWSTR(buffer).ToString()
            : null;
    }

#if !STORE
    /// <inheritdoc />
    /// <remarks>
    /// Always asks for the plain-text key (WLAN_PROFILE_GET_PLAINTEXT_KEY);
    /// Windows answers either way, marking the key protected when it keeps it
    /// encrypted. The XML is handed straight back and kept nowhere.
    /// </remarks>
    public unsafe (WlanStatus Status, string? Xml) Profile(Guid adapter, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        const uint PlainTextKey = 0x4;

        lock (_gate)
        {
            uint result = WithHandle(handle =>
            {
                uint flags = PlainTextKey;
                uint code = PInvoke.WlanGetProfile(handle, adapter, name, out PWSTR found, ref flags, out _);
                return (code, (nint)found.Value);
            }, out nint pointer);

            PWSTR xml = new((char*)pointer);

            if (result != NoError || xml.Value is null)
            {
                return (Status(result), null);
            }

            try
            {
                return (WlanStatus.Ok, xml.ToString());
            }
            finally
            {
                PInvoke.WlanFreeMemory(xml.Value);
            }
        }
    }
#endif

    /// <summary>Reads a Native Wi-Fi error as a <see cref="WlanStatus"/>.</summary>
    public static WlanStatus Status(uint code) => code switch
    {
        NoError => WlanStatus.Ok,
        AccessDenied => WlanStatus.Denied,
        NotSupportedError or InvalidParameter => WlanStatus.NotSupported,
        ServiceNotActive => WlanStatus.NoService,
        InvalidState => WlanStatus.NotConnected,
        RadioPowerInvalid => WlanStatus.RadioOff,
        _ => WlanStatus.Failed,
    };

    private uint WithHandle(Func<WlanCloseHandleSafeHandle, (uint Code, nint Value)> call, out nint value)
    {
        value = 0;
        ObjectDisposedException.ThrowIf(_disposed, this);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (_handle is null || _handle.IsInvalid || _handle.IsClosed)
            {
                uint opened = PInvoke.WlanOpenHandle(2, out _, out WlanCloseHandleSafeHandle handle);

                if (opened != NoError)
                {
                    handle.Dispose();
                    return opened;
                }

                _handle = handle;
            }

            (uint code, nint result) = call(_handle);

            if (code == InvalidHandle)
            {
                // The service restarted and took the handle with it.
                _handle.Dispose();
                _handle = null;
                continue;
            }

            value = result;
            return code;
        }

        return InvalidHandle;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _handle?.Dispose();
            _handle = null;
        }
    }
}
