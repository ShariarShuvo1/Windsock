using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Windsock.Core.Home;

/// <summary>One service a device announced over multicast DNS.</summary>
/// <param name="Instance">The service instance, such as "Living Room._airplay._tcp.local".</param>
/// <param name="ServiceType">Its type, such as "_airplay._tcp.local".</param>
/// <param name="Host">The device's host name, such as "Living-Room.local", when known.</param>
/// <param name="Addresses">The device's addresses, when known.</param>
public sealed record MulticastService(string Instance, string ServiceType, string? Host, IReadOnlyList<IPAddress> Addresses)
{
    /// <summary>The name to give the device, from its instance or host name.</summary>
    public string? DeviceName => MulticastNames.Choose(Instance, ServiceType, Host);
}

/// <summary>Browses the home link for services devices announce over multicast DNS.</summary>
public interface IMulticastBrowser
{
    /// <summary>
    /// The services announced on one interface within a few seconds. Sends
    /// multicast DNS queries to 224.0.0.251 and ff02::fb, which stay on the
    /// link. Empty when Windows cannot browse. Throws only when cancelled.
    /// </summary>
    Task<IReadOnlyList<MulticastService>> BrowseAsync(uint interfaceIndex, CancellationToken cancellationToken);
}

/// <summary>Turns what multicast DNS announces into a name for the device.</summary>
public static class MulticastNames
{
    /// <summary>
    /// The instance's own label ("Living Room TV" from "Living Room
    /// TV._airplay._tcp.local") unless it is a serial or an id, in which case
    /// the host's first label ("HP0A1B2C" from "HP0A1B2C.local").
    /// </summary>
    public static string? Choose(string instance, string serviceType, string? host)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(serviceType);

        string label = instance.EndsWith("." + serviceType, StringComparison.OrdinalIgnoreCase)
            ? instance[..^(serviceType.Length + 1)]
            : instance;

        label = label.Replace("\\.", ".", StringComparison.Ordinal).Replace("\\032", " ", StringComparison.Ordinal);

        if (DeviceNames.Clean(label) is { } named && !LooksLikeAnId(named))
        {
            return named;
        }

        return DeviceNames.FromHostName(host) is { } hosted && !LooksLikeAnId(hosted) ? hosted : null;
    }

    /// <summary>Whether a label is a serial number, a UUID or a hardware address rather than a name.</summary>
    public static bool LooksLikeAnId(string label)
    {
        ArgumentNullException.ThrowIfNull(label);

        if (label.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        int hex = label.Count(char.IsAsciiHexDigit);
        int letters = label.Count(char.IsAsciiLetter);
        int other = label.Count(each => !char.IsAsciiLetterOrDigit(each) && each is not '-' and not '_');

        // "0123abcd-...", "a1b2c3d4e5f6", "Chromecast-0123456789abcdef0123".
        return other == 0 && label.Length >= 12 && hex >= label.Length * 0.6 && letters <= label.Length - (label.Length * 0.25);
    }
}

/// <summary>
/// Browses through Windows' own multicast DNS client (<c>DnsServiceBrowse</c>
/// and <c>DnsServiceResolve</c> in dnsapi.dll), which since Windows 10 shares
/// UDP 5353 with everything else that speaks it, so Windsock opens no
/// listening socket of its own.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than generated: CsWin32 declines these for an AnyCPU
/// project (PInvoke005), because DNS_RECORD's layout follows the pointer size.
/// The offsets below follow windns.h for both sizes and are checked by tests.
/// </para>
/// <para>
/// A browse runs on Windows' thread pool and calls back repeatedly until it is
/// cancelled, after which one last callback says so (status 1223, measured). The
/// session is kept alive until that last callback, so nothing it points to is
/// freed under Windows' feet.
/// </para>
/// </remarks>
public sealed partial class WindowsMulticastBrowser : IMulticastBrowser
{
    /// <summary>The RFC 6763 meta-query that lists the service types on a link.</summary>
    public const string ServiceTypes = "_services._dns-sd._udp.local";

    private const uint Pending = 9506;
    private const uint Cancelled = 1223;
    private const uint RequestVersion1 = 1;
    private const uint FreeRecordList = 1;
    private const int MostTypes = 24;
    private const int MostResolves = 32;

    /// <summary>Types most homes have, browsed even when nothing answers the meta-query.</summary>
    private static readonly string[] Common =
    [
        "_airplay._tcp.local",
        "_googlecast._tcp.local",
        "_ipp._tcp.local",
        "_printer._tcp.local",
        "_raop._tcp.local",
        "_smb._tcp.local",
        "_device-info._tcp.local",
        "_hap._tcp.local",
        "_spotify-connect._tcp.local",
        "_workstation._tcp.local",
    ];

    private static readonly TimeSpan Listen = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ResolveWait = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MulticastService>> BrowseAsync(uint interfaceIndex, CancellationToken cancellationToken)
    {
        try
        {
            BrowseResult types = await RunBrowse(ServiceTypes, interfaceIndex, cancellationToken).ConfigureAwait(false);

            if (!types.Started)
            {
                return [];
            }

            string[] wanted = [.. types.Pointers
                .Select(each => each.Target)
                .Where(each => each.StartsWith('_') && each.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
                .Concat(Common)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MostTypes)];

            BrowseResult[] browsed = await Task.WhenAll(wanted.Select(type => RunBrowse(type, interfaceIndex, cancellationToken))).ConfigureAwait(false);

            List<DnsAnswer> answers = [.. types.Answers, .. browsed.SelectMany(each => each.Answers)];
            List<MulticastService> services = Assemble(answers, wanted);

            for (int index = 0, resolved = 0; index < services.Count && resolved < MostResolves; index++)
            {
                if (services[index].Addresses.Count > 0)
                {
                    continue;
                }

                resolved++;

                if (await Resolve(services[index].Instance, interfaceIndex, cancellationToken).ConfigureAwait(false) is { } found)
                {
                    services[index] = services[index] with { Host = found.Host ?? services[index].Host, Addresses = found.Addresses };
                }
            }

            return services;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    /// <summary>
    /// Puts browse answers together: each PTR under a browsed type is a service
    /// instance, an SRV names its host, and A and AAAA records give the host's
    /// addresses.
    /// </summary>
    internal static List<MulticastService> Assemble(IReadOnlyList<DnsAnswer> answers, IReadOnlyCollection<string> types)
    {
        Dictionary<string, string> hosts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<IPAddress>> addresses = new(StringComparer.OrdinalIgnoreCase);

        foreach (DnsAnswer answer in answers)
        {
            if (answer.Type == DnsAnswer.Service && answer.Target.Length > 0)
            {
                hosts[answer.Name] = answer.Target;
            }
            else if (answer.Address is { } address)
            {
                if (!addresses.TryGetValue(answer.Name, out List<IPAddress>? list))
                {
                    list = [];
                    addresses[answer.Name] = list;
                }

                if (!list.Contains(address))
                {
                    list.Add(address);
                }
            }
        }

        List<MulticastService> services = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (DnsAnswer answer in answers)
        {
            if (answer.Type != DnsAnswer.Pointer
                || !types.Contains(answer.Name, StringComparer.OrdinalIgnoreCase)
                || !seen.Add(answer.Target))
            {
                continue;
            }

            string? host = hosts.GetValueOrDefault(answer.Target);
            IReadOnlyList<IPAddress> found = host is not null && addresses.TryGetValue(host, out List<IPAddress>? list) ? list : [];
            services.Add(new MulticastService(answer.Target, answer.Name, host, found));
        }

        return services;
    }

    private sealed record BrowseResult(bool Started, IReadOnlyList<DnsAnswer> Answers)
    {
        public IEnumerable<DnsAnswer> Pointers => Answers.Where(each => each.Type == DnsAnswer.Pointer);
    }

    private static async Task<BrowseResult> RunBrowse(string query, uint interfaceIndex, CancellationToken cancellationToken)
    {
        using BrowseSession session = new();

        if (!session.Start(query, interfaceIndex))
        {
            return new BrowseResult(false, []);
        }

        try
        {
            await Task.Delay(Listen, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.StopAsync().ConfigureAwait(false);
        }

        return new BrowseResult(true, session.Answers);
    }

    private static async Task<(string? Host, IReadOnlyList<IPAddress> Addresses)?> Resolve(string instance, uint interfaceIndex, CancellationToken cancellationToken)
    {
        using ResolveSession session = new();

        if (!session.Start(instance, interfaceIndex))
        {
            return null;
        }

        try
        {
            return await session.Answer.Task.WaitAsync(ResolveWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            await session.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>One browse in flight: the request Windows was handed and what has come back.</summary>
    private sealed class BrowseSession : IDisposable
    {
        private readonly ConcurrentQueue<DnsAnswer> _answers = new();
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private GCHandle _self;
        private nint _name;
        private nint _cancel;
        private bool _started;

        public IReadOnlyList<DnsAnswer> Answers => [.. _answers];

        public unsafe bool Start(string query, uint interfaceIndex)
        {
            _self = GCHandle.Alloc(this);
            _name = Marshal.StringToHGlobalUni(query);
            _cancel = Marshal.AllocHGlobal(IntPtr.Size);
            *(nint*)_cancel = 0;

            BrowseRequest request = new()
            {
                Version = RequestVersion1,
                InterfaceIndex = interfaceIndex,
                QueryName = _name,
                Callback = (nint)(delegate* unmanaged[Stdcall]<uint, nint, nint, void>)&OnRecords,
                Context = GCHandle.ToIntPtr(_self),
            };

            _started = DnsServiceBrowse(&request, _cancel) == Pending;
            return _started;
        }

        public async Task StopAsync()
        {
            if (!_started)
            {
                return;
            }

            _ = DnsServiceBrowseCancel(_cancel);

            try
            {
                await _ended.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Windows never said it was done, so it may still call back:
                // leave the session's memory where it is rather than free it
                // under the callback. A few bytes, once.
                _started = false;
                _self = default;
                _name = 0;
                _cancel = 0;
                return;
            }

            _started = false;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void OnRecords(uint status, nint context, nint records)
        {
            try
            {
                if (GCHandle.FromIntPtr(context).Target is BrowseSession session)
                {
                    if (status == Cancelled)
                    {
                        session._ended.TrySetResult();
                    }
                    else
                    {
                        foreach (DnsAnswer answer in DnsRecordReader.Read(records))
                        {
                            session._answers.Enqueue(answer);
                        }
                    }
                }
            }
#pragma warning disable CA1031 // An exception may not cross back into Windows' thread pool.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
            finally
            {
                if (records != 0)
                {
                    DnsFree(records, FreeRecordList);
                }
            }
        }

        public void Dispose()
        {
            if (_started)
            {
                return;
            }

            if (_self.IsAllocated)
            {
                _self.Free();
            }

            if (_name != 0)
            {
                Marshal.FreeHGlobal(_name);
                _name = 0;
            }

            if (_cancel != 0)
            {
                Marshal.FreeHGlobal(_cancel);
                _cancel = 0;
            }
        }
    }

    /// <summary>One resolve in flight.</summary>
    private sealed class ResolveSession : IDisposable
    {
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private GCHandle _self;
        private nint _name;
        private nint _cancel;
        private bool _started;

        public TaskCompletionSource<(string? Host, IReadOnlyList<IPAddress> Addresses)?> Answer { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public unsafe bool Start(string instance, uint interfaceIndex)
        {
            _self = GCHandle.Alloc(this);
            _name = Marshal.StringToHGlobalUni(instance);
            _cancel = Marshal.AllocHGlobal(IntPtr.Size);
            *(nint*)_cancel = 0;

            ResolveRequest request = new()
            {
                Version = RequestVersion1,
                InterfaceIndex = interfaceIndex,
                QueryName = _name,
                Callback = (nint)(delegate* unmanaged[Stdcall]<uint, nint, nint, void>)&OnResolved,
                Context = GCHandle.ToIntPtr(_self),
            };

            _started = DnsServiceResolve(&request, _cancel) == Pending;
            return _started;
        }

        public async Task StopAsync()
        {
            if (!_started)
            {
                return;
            }

            if (!Answer.Task.IsCompleted)
            {
                _ = DnsServiceResolveCancel(_cancel);
            }

            try
            {
                await _ended.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _started = false;
                _self = default;
                _name = 0;
                _cancel = 0;
                return;
            }

            _started = false;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void OnResolved(uint status, nint context, nint instance)
        {
            ResolveSession? session = null;

            try
            {
                session = GCHandle.FromIntPtr(context).Target as ResolveSession;

                if (session is not null && status == 0 && instance != 0)
                {
                    session.Answer.TrySetResult(DnsRecordReader.ReadInstance(instance));
                }
            }
#pragma warning disable CA1031 // An exception may not cross back into Windows' thread pool.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
            finally
            {
                if (instance != 0)
                {
                    DnsServiceFreeInstance(instance);
                }

                session?.Answer.TrySetResult(null);
                session?._ended.TrySetResult();
            }
        }

        public void Dispose()
        {
            if (_started)
            {
                return;
            }

            if (_self.IsAllocated)
            {
                _self.Free();
            }

            if (_name != 0)
            {
                Marshal.FreeHGlobal(_name);
                _name = 0;
            }

            if (_cancel != 0)
            {
                Marshal.FreeHGlobal(_cancel);
                _cancel = 0;
            }
        }
    }

    /// <summary>DNS_SERVICE_BROWSE_REQUEST, version 1.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BrowseRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public nint QueryName;
        public nint Callback;
        public nint Context;
    }

    /// <summary>DNS_SERVICE_RESOLVE_REQUEST.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ResolveRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public nint QueryName;
        public nint Callback;
        public nint Context;
    }

    [LibraryImport("dnsapi.dll")]
    private static unsafe partial uint DnsServiceBrowse(BrowseRequest* request, nint cancel);

    [LibraryImport("dnsapi.dll")]
    private static partial uint DnsServiceBrowseCancel(nint cancel);

    [LibraryImport("dnsapi.dll")]
    private static unsafe partial uint DnsServiceResolve(ResolveRequest* request, nint cancel);

    [LibraryImport("dnsapi.dll")]
    private static partial uint DnsServiceResolveCancel(nint cancel);

    [LibraryImport("dnsapi.dll")]
    private static partial void DnsServiceFreeInstance(nint instance);

    [LibraryImport("dnsapi.dll")]
    private static partial void DnsFree(nint data, uint freeType);
}

/// <summary>One answer from a multicast DNS browse, as much of it as Windsock reads.</summary>
/// <param name="Name">The owner name.</param>
/// <param name="Type">The record type.</param>
/// <param name="Target">A PTR's or SRV's target name; empty for other types.</param>
/// <param name="Address">An A or AAAA record's address.</param>
internal readonly record struct DnsAnswer(string Name, ushort Type, string Target, IPAddress? Address)
{
    public const ushort AddressFour = 1;
    public const ushort Pointer = 12;
    public const ushort AddressSix = 28;
    public const ushort Service = 33;
}

/// <summary>
/// Reads Windows' DNS_RECORDW lists and DNS_SERVICE_INSTANCE (windns.h) by
/// offset, for either pointer size.
/// </summary>
/// <remarks>
/// DNS_RECORDW: pNext, pName (pointers), wType and wDataLength (WORD), Flags,
/// dwTtl, dwReserved (DWORD), then the Data union at the next pointer
/// boundary: 32 on x64, 24 on x86. In Data, PTR and SRV start with a pointer to
/// the target name, A holds the four address bytes as they travel, AAAA the
/// sixteen. DNS_SERVICE_INSTANCE: pszInstanceName, pszHostName, ip4Address,
/// ip6Address (pointers), then wPort, wPriority, wWeight.
/// </remarks>
internal static unsafe class DnsRecordReader
{
    private const int MostRecords = 512;

    public static int TypeOffset => 2 * IntPtr.Size;

    public static int DataOffset => (2 * IntPtr.Size) + 16;

    public static IEnumerable<DnsAnswer> Read(nint first)
    {
        List<DnsAnswer> answers = [];
        int count = 0;

        for (nint record = first; record != 0 && count < MostRecords; record = *(nint*)record, count++)
        {
            string name = Marshal.PtrToStringUni(*(nint*)(record + IntPtr.Size)) ?? string.Empty;
            ushort type = *(ushort*)(record + TypeOffset);
            ushort length = *(ushort*)(record + TypeOffset + 2);
            nint data = record + DataOffset;

            switch (type)
            {
                case DnsAnswer.Pointer:
                case DnsAnswer.Service:
                    answers.Add(new DnsAnswer(name, type, Marshal.PtrToStringUni(*(nint*)data) ?? string.Empty, null));
                    break;

                case DnsAnswer.AddressFour when length >= 4:
                    answers.Add(new DnsAnswer(name, type, string.Empty, new IPAddress(new ReadOnlySpan<byte>((void*)data, 4))));
                    break;

                case DnsAnswer.AddressSix when length >= 16:
                    answers.Add(new DnsAnswer(name, type, string.Empty, new IPAddress(new ReadOnlySpan<byte>((void*)data, 16))));
                    break;
            }
        }

        return answers;
    }

    public static (string? Host, IReadOnlyList<IPAddress> Addresses) ReadInstance(nint instance)
    {
        string? host = Marshal.PtrToStringUni(*(nint*)(instance + IntPtr.Size));
        nint four = *(nint*)(instance + (2 * IntPtr.Size));
        nint six = *(nint*)(instance + (3 * IntPtr.Size));
        List<IPAddress> addresses = [];

        if (four != 0)
        {
            addresses.Add(new IPAddress(new ReadOnlySpan<byte>((void*)four, 4)));
        }

        if (six != 0)
        {
            addresses.Add(new IPAddress(new ReadOnlySpan<byte>((void*)six, 16)));
        }

        return (host, addresses);
    }
}
