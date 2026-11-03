using System.ComponentModel;
using Windows.Win32;
using Windows.Win32.System.Services;

namespace Windsock.Core.Insight;

/// <summary>
/// Reads the running services from the service control manager, which answers
/// a standard user.
/// </summary>
/// <remarks>
/// The manager leaves out any service the caller may not query, rather than
/// failing; on the development machine none was left out. The process id comes
/// with each service, so no service host has to be opened - which matters,
/// because a standard user can open only the few that run as themselves.
/// </remarks>
public sealed unsafe class WindowsServiceSource : IServiceHostSource
{
    private const uint EnumerateServices = 0x0004;   // SC_MANAGER_ENUMERATE_SERVICE
    private const int MoreData = 234;                // ERROR_MORE_DATA

    /// <summary>The documented ceiling of one call's buffer.</summary>
    private const int Ceiling = 256 * 1024;

    /// <inheritdoc />
    public IReadOnlyList<HostedService> Read()
    {
        List<HostedService> services = [];

        try
        {
            using CloseServiceHandleSafeHandle manager = PInvoke.OpenSCManager(null, null, EnumerateServices);

            if (manager.IsInvalid)
            {
                return services;
            }

            byte[] buffer = new byte[64 * 1024];
            uint resume = 0;

            for (int round = 0; round < 16; round++)
            {
                bool done = PInvoke.EnumServicesStatusEx(
                    manager,
                    SC_ENUM_TYPE.SC_ENUM_PROCESS_INFO,
                    ENUM_SERVICE_TYPE.SERVICE_WIN32,
                    ENUM_SERVICE_STATE.SERVICE_ACTIVE,
                    buffer,
                    out uint needed,
                    out uint returned,
                    ref resume,
                    null);

                int error = done ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError();

                if (!done && error != MoreData)
                {
                    break;
                }

                Collect(buffer, returned, services);

                if (done)
                {
                    break;
                }

                // More to come: what fitted was returned and the resume handle
                // moved on. Grow for the rest, up to the documented ceiling.
                if (needed > buffer.Length && buffer.Length < Ceiling)
                {
                    buffer = new byte[Math.Min(Ceiling, (int)needed + 4096)];
                }
            }
        }
        catch (Win32Exception)
        {
        }

        return services;
    }

    private static void Collect(byte[] buffer, uint count, List<HostedService> into)
    {
        fixed (byte* start = buffer)
        {
            ENUM_SERVICE_STATUS_PROCESSW* rows = (ENUM_SERVICE_STATUS_PROCESSW*)start;

            for (int at = 0; at < count; at++)
            {
                ENUM_SERVICE_STATUS_PROCESSW row = rows[at];
                int processId = (int)row.ServiceStatusProcess.dwProcessId;

                if (processId <= 0)
                {
                    continue;
                }

                string name = row.lpServiceName.ToString();
                string display = row.lpDisplayName.ToString();

                into.Add(new HostedService(name, string.IsNullOrWhiteSpace(display) ? name : display, processId));
            }
        }
    }
}
