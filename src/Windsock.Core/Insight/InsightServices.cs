using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Windsock.Core.Asn;
using Windsock.Core.Security;
#if !STORE
using Windsock.Core.Geography;
using Windsock.Core.Processes;
using Windsock.Core.Timeline;
#endif

namespace Windsock.Core.Insight;

/// <summary>Registers step 7's engine: insight into the programs using the network, and control over them.</summary>
public static class InsightServices
{
    /// <summary>
    /// Adds the new-app watcher, signatures, service names, blocklists,
    /// reputation lookups and Windows Update's share in both editions, and in
    /// the full edition the timeline, the per-service record, the local/internet
    /// split and the per-app name lookups.
    /// </summary>
    /// <remarks>
    /// Expects the host to have registered what these read: the process and
    /// connection sources, the identity resolver, the history stores, the
    /// usage recorder and the host-name cache; in the full edition the kernel
    /// trace as <c>EtwProcessTrafficSource</c>.
    /// </remarks>
    public static IServiceCollection AddInsight(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The ASN table may be registered by the connection tools too; one
        // shared copy either way, opened only when first asked.
        services.TryAddSingleton(_ => new AsnDirectory());

        services.AddSingleton<ISignatureVerifier, WinTrustVerifier>();
        services.AddSingleton<SignatureBook>();
        services.AddSingleton<IServiceHostSource, WindowsServiceSource>();
        services.AddSingleton<ServiceHosts>();
        services.AddSingleton<SocketOwners>();

        services.AddSingleton(_ => new SeenApps(InsightPaths.SeenApps));
        services.AddSingleton<NewAppWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<NewAppWatcher>());
        services.AddHostedService<NewAppNotices>();

        // Off until switched on; fetches nothing while off.
        services.AddSingleton<Blocklists>();
        services.AddHostedService(sp => sp.GetRequiredService<Blocklists>());

        // Sends nothing until the person adds a key and asks.
        services.AddSingleton(_ => new ApiKeyVault(InsightPaths.VirusTotalKey));
        services.AddSingleton<ReputationService>();

        services.AddSingleton<IDeliveryOptimizationSource, WmiDeliveryOptimizationSource>();

#if STORE
        services.AddSingleton(sp => new WindowsUpdateShare(
            sp.GetRequiredService<IDeliveryOptimizationSource>(),
            sp.GetRequiredService<Apps.IAppUsageStore>()));
#else
        services.AddSingleton(_ => new SqliteTimelineStore(InsightPaths.Timeline));
        services.AddSingleton<ITimelineStore>(sp => sp.GetRequiredService<SqliteTimelineStore>());
        services.AddSingleton<IServiceTrafficStore>(sp => sp.GetRequiredService<SqliteTimelineStore>());
        services.AddSingleton<IErasedWithHistory>(sp => sp.GetRequiredService<SqliteTimelineStore>());
        services.AddSingleton(sp => new WindowsUpdateShare(
            sp.GetRequiredService<IDeliveryOptimizationSource>(),
            sp.GetRequiredService<Apps.IAppUsageStore>(),
            sp.GetRequiredService<IServiceTrafficStore>()));

        services.AddSingleton<IFlowLedger>(sp => sp.GetRequiredService<EtwProcessTrafficSource>());
        services.AddSingleton<ITrafficSplitSource>(sp => sp.GetRequiredService<EtwProcessTrafficSource>());

        services.AddSingleton<LocalTrafficRecorder>();
        services.AddHostedService(sp => sp.GetRequiredService<LocalTrafficRecorder>());

        services.AddSingleton<DnsLookupLog>();
        services.AddSingleton(sp =>
        {
            ServiceHosts hosts = sp.GetRequiredService<ServiceHosts>();
            return new EtwDnsLookupSource(
                sp.GetRequiredService<IProcessTrafficSource>(),
                sp.GetRequiredService<DnsLookupLog>(),
                processId => hosts.Current.Runs(processId, "Dnscache"),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EtwDnsLookupSource>>());
        });
        services.AddHostedService(sp => sp.GetRequiredService<EtwDnsLookupSource>());

        services.AddSingleton(sp =>
        {
            Lazy<IpLocations?> places = new(() => IpLocations.Open());
            AsnDirectory networks = sp.GetRequiredService<AsnDirectory>();
            DnsLookupLog lookups = sp.GetRequiredService<DnsLookupLog>();
            HostNameCache cache = sp.GetRequiredService<HostNameCache>();

            return new FarEndNamer(
                address => places.Value?.Find(address)?.Country,
                address => networks.Find(address)?.Organisation,
                (processId, address) => lookups.NameFor(processId, address) ?? cache.Find(address));
        });

        services.AddSingleton<TimelineRecorder>();
        services.AddHostedService(sp => sp.GetRequiredService<TimelineRecorder>());
#endif

        return services;
    }
}
