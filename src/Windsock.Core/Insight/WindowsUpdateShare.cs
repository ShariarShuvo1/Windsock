using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Windsock.Core.Apps;

namespace Windsock.Core.Insight;

/// <summary>
/// Delivery Optimization's own count of this calendar month, the figures
/// Settings shows under Windows Update, Delivery Optimization, Activity monitor.
/// </summary>
/// <param name="FromMicrosoft">Downloaded from Microsoft's servers and content networks.</param>
/// <param name="FromCacheServers">Downloaded from a cache server on the network (Connected Cache).</param>
/// <param name="FromLocalPeers">Downloaded from other PCs on the same network.</param>
/// <param name="FromInternetPeers">Downloaded from other PCs over the internet.</param>
/// <param name="ToLocalPeers">Uploaded to other PCs on the same network.</param>
/// <param name="ToInternetPeers">Uploaded to other PCs over the internet.</param>
public sealed record DeliveryOptimizationMonth(
    long FromMicrosoft,
    long FromCacheServers,
    long FromLocalPeers,
    long FromInternetPeers,
    long ToLocalPeers,
    long ToInternetPeers)
{
    /// <summary>Everything downloaded this month.</summary>
    public long Downloaded => FromMicrosoft + FromCacheServers + FromLocalPeers + FromInternetPeers;

    /// <summary>Everything uploaded to other PCs this month.</summary>
    public long Uploaded => ToLocalPeers + ToInternetPeers;

    /// <summary>
    /// What crossed the internet connection: downloads from Microsoft and
    /// internet peers, uploads to internet peers. Peers and cache servers on
    /// the same network never reach a data plan.
    /// </summary>
    public long OverInternet => FromMicrosoft + FromInternetPeers + ToInternetPeers;
}

/// <summary>Reads Delivery Optimization's monthly figures.</summary>
public interface IDeliveryOptimizationSource
{
    /// <summary>This month's figures, or null when Delivery Optimization does not answer.</summary>
    DeliveryOptimizationMonth? ReadThisMonth();
}

/// <summary>
/// Reads Delivery Optimization's monthly figures from its WMI provider, which
/// answers a standard user.
/// </summary>
/// <remarks>
/// <para>
/// Windows' per-app data usage cannot answer "how much did updates use":
/// Windows Update and Delivery Optimization run inside service hosts, and
/// Windows files service-host traffic under no app at all. Measured on
/// 2026-10-05 over the month's first five days: no <c>svchost.exe</c> id in
/// 116, 530 MB in the unattributed bucket, while Delivery Optimization's own
/// count was 283 MB. The only update-stack programs Windows names are small
/// helpers (<see cref="WindowsUpdateParts"/>).
/// </para>
/// <para>
/// Delivery Optimization keeps its own count, which is what its Activity
/// monitor shows and what the <c>Get-DeliveryOptimizationPerfSnapThisMonth</c>
/// cmdlet reads: the <c>MSFT_DODownloadUsage</c> and <c>MSFT_DOUploadUsage</c>
/// classes in <c>root\Microsoft\Windows\DeliveryOptimization</c>. It covers
/// Windows Update, Store app updates and other Microsoft downloads it carries,
/// split by where the bytes came from. It does not cover downloads that go
/// around it.
/// </para>
/// </remarks>
public sealed class WmiDeliveryOptimizationSource : IDeliveryOptimizationSource
{
    private const string Namespace = @"\\.\root\Microsoft\Windows\DeliveryOptimization";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public DeliveryOptimizationMonth? ReadThisMonth()
    {
        try
        {
            ManagementScope scope = new(Namespace);
            Dictionary<string, object?> down = ReadOne(scope, "MSFT_DODownloadUsage");
            Dictionary<string, object?> up = ReadOne(scope, "MSFT_DOUploadUsage");

            if (down.Count == 0 && up.Count == 0)
            {
                return null;
            }

            return new DeliveryOptimizationMonth(
                FromMicrosoft: Bytes(down, "MonthlyCdnBytes"),
                FromCacheServers: Bytes(down, "MonthlyCacheHostBytes"),
                FromLocalPeers: Bytes(down, "MonthlyLanBytes") + Bytes(down, "MonthlyLinkLocalBytes") + Bytes(down, "MonthlyGroupBytes"),
                FromInternetPeers: Bytes(down, "MonthlyInternetBytes"),
                ToLocalPeers: Bytes(up, "MonthlyLanBytes") + Bytes(up, "MonthlyLinkLocalBytes") + Bytes(up, "MonthlyGroupBytes"),
                ToInternetPeers: Bytes(up, "MonthlyInternetBytes"));
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> ReadOne(ManagementScope scope, string className)
    {
        Dictionary<string, object?> values = new(StringComparer.OrdinalIgnoreCase);
        System.Management.EnumerationOptions options = new() { Timeout = Patience, ReturnImmediately = true, Rewindable = false };
        using ManagementObjectSearcher searcher = new(scope, new ObjectQuery("SELECT * FROM " + className), options);
        using ManagementObjectCollection results = searcher.Get();

        foreach (ManagementBaseObject result in results)
        {
            using (result)
            {
                foreach (PropertyData property in result.Properties)
                {
                    values[property.Name] = property.Value;
                }
            }
        }

        return values;
    }

    private static long Bytes(Dictionary<string, object?> values, string name) =>
        values.TryGetValue(name, out object? value) && value is not null
            ? (long)Math.Min(long.MaxValue, Convert.ToUInt64(value, CultureInfo.InvariantCulture))
            : 0;
}

/// <summary>What Windows Update is made of, as Windows names its parts.</summary>
public static class WindowsUpdateParts
{
    /// <summary>
    /// The services that fetch and install updates. Each runs in a service host
    /// of its own on Windows 11, so the kernel trace can measure it.
    /// </summary>
    public static IReadOnlyList<string> Services { get; } =
    [
        "wuauserv",      // Windows Update
        "DoSvc",         // Delivery Optimization
        "BITS",          // Background Intelligent Transfer Service
        "UsoSvc",        // Update Orchestrator Service
        "WaaSMedicSvc",  // Windows Update Medic Service
    ];

    /// <summary>
    /// The update programs Windows' data usage names by file. Measured on
    /// 2026-10-05: <c>\windows\uus\amd64\mousocoreworker.exe</c> (2.2 MB over
    /// a month), <c>waasmedicagent.exe</c> and <c>\windows\system32\sihclient.exe</c>.
    /// The others are the same stack's programs on other builds.
    /// </summary>
    private static readonly HashSet<string> Programs = new(StringComparer.OrdinalIgnoreCase)
    {
        "mousocoreworker.exe",
        "waasmedicagent.exe",
        "sihclient.exe",
        "usoclient.exe",
        "wuauclt.exe",
        "wuaucltcore.exe",
        "musnotification.exe",
        "musnotificationux.exe",
        "tiworker.exe",
    };

    /// <summary>Whether a service's key name is one of the update services.</summary>
    public static bool IsUpdateService(string? serviceName) =>
        serviceName is not null && Services.Contains(serviceName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether one of Windows' data usage ids is an update program: a Windows
    /// file (under <c>\windows\</c>) of one of the update stack's names.
    /// </summary>
    public static bool IsUpdateProgram(string? attributionId)
    {
        if (string.IsNullOrWhiteSpace(attributionId))
        {
            return false;
        }

        AppAttribution app = AppAttribution.Classify(attributionId);

        if (app.Kind != AppKind.Program
            || !app.Id.Contains(@"\windows\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int slash = app.Id.LastIndexOf('\\');
        return Programs.Contains(slash < 0 ? app.Id : app.Id[(slash + 1)..]);
    }
}

/// <summary>One service host's bytes over a period, as the kernel trace measured them.</summary>
/// <param name="ServiceKey">The service's key name, or a shared host's key names joined by commas.</param>
/// <param name="BytesReceived">Bytes received.</param>
/// <param name="BytesSent">Bytes sent.</param>
public readonly record struct ServiceBytes(string ServiceKey, long BytesReceived, long BytesSent)
{
    /// <summary>Both directions.</summary>
    public long BytesTotal => BytesReceived + BytesSent;
}

/// <summary>Each service host's traffic, kept a day at a time while the kernel trace runs.</summary>
public interface IServiceTrafficStore
{
    /// <summary>Each service host's bytes over the whole local days the period touches.</summary>
    IReadOnlyList<ServiceBytes> ReadServiceTotals(DateTimeOffset from, DateTimeOffset until);
}

/// <summary>How much Windows Update used this month, from every source that can say.</summary>
/// <param name="MonthStart">The first moment of the month, local time.</param>
/// <param name="DeliveryOptimization">Delivery Optimization's own count, or null when it did not answer.</param>
/// <param name="ProgramsReceived">What the update programs Windows names received this month, by Windows' count.</param>
/// <param name="ProgramsSent">What they sent.</param>
/// <param name="MeasuredServices">
/// The update services' own traffic as the kernel trace measured it, for the
/// days Windsock ran as administrator; null in the Store edition or when the
/// trace never ran this month. Not the month's whole: the trace measures only
/// while it runs.
/// </param>
public sealed record UpdateShareReport(
    DateTimeOffset MonthStart,
    DeliveryOptimizationMonth? DeliveryOptimization,
    long ProgramsReceived,
    long ProgramsSent,
    IReadOnlyList<ServiceBytes>? MeasuredServices);

/// <summary>Puts the month's Windows Update figures together.</summary>
public sealed class WindowsUpdateShare
{
    private readonly IDeliveryOptimizationSource _deliveryOptimization;
    private readonly Func<DateTimeOffset, DateTimeOffset, IReadOnlyList<AppUsageTotal>> _appTotals;
    private readonly IServiceTrafficStore? _services;
    private readonly TimeProvider _clock;

    public WindowsUpdateShare(IDeliveryOptimizationSource deliveryOptimization, IAppUsageStore appUsage)
        : this(deliveryOptimization, appUsage, services: null)
    {
    }

    /// <summary>With the kernel trace's per-service record, which only the full edition keeps.</summary>
    public WindowsUpdateShare(IDeliveryOptimizationSource deliveryOptimization, IAppUsageStore appUsage, IServiceTrafficStore? services)
        : this(deliveryOptimization, (appUsage ?? throw new ArgumentNullException(nameof(appUsage))).ReadAppTotals, services, TimeProvider.System)
    {
    }

    internal WindowsUpdateShare(
        IDeliveryOptimizationSource deliveryOptimization,
        Func<DateTimeOffset, DateTimeOffset, IReadOnlyList<AppUsageTotal>> appTotals,
        IServiceTrafficStore? services,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(deliveryOptimization);
        ArgumentNullException.ThrowIfNull(appTotals);
        ArgumentNullException.ThrowIfNull(clock);

        _deliveryOptimization = deliveryOptimization;
        _appTotals = appTotals;
        _services = services;
        _clock = clock;
    }

    /// <summary>
    /// This month so far. Reads WMI and the history file, so call it off the UI
    /// thread. A history file that cannot be read leaves the programs' figures
    /// at zero rather than failing the rest.
    /// </summary>
    public UpdateShareReport Read()
    {
        DateTimeOffset now = _clock.GetUtcNow().ToLocalTime();
        DateTimeOffset monthStart = new(now.Year, now.Month, 1, 0, 0, 0, now.Offset);

        long received = 0;
        long sent = 0;

        try
        {
            foreach (AppUsageTotal total in _appTotals(monthStart, now))
            {
                if (WindowsUpdateParts.IsUpdateProgram(total.AttributionId))
                {
                    received += total.BytesReceived;
                    sent += total.BytesSent;
                }
            }
        }
        catch (Exception ex) when (History.HistoryFailure.IsStorage(ex))
        {
        }

        List<ServiceBytes>? measured = null;

        if (_services is not null)
        {
            try
            {
                measured = [.. _services.ReadServiceTotals(monthStart, now)
                    .Where(each => each.ServiceKey.Split(',').Any(WindowsUpdateParts.IsUpdateService))];
            }
            catch (Exception ex) when (History.HistoryFailure.IsStorage(ex))
            {
            }
        }

        return new UpdateShareReport(
            monthStart,
            _deliveryOptimization.ReadThisMonth(),
            received,
            sent,
            measured is { Count: > 0 } ? measured : null);
    }
}
