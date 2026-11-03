using Microsoft.Extensions.DependencyInjection;
using Windsock.Core.Connectivity;
using Windsock.Core.Hardware;
using Windsock.Core.History;
using Windsock.Core.Home;
using Windsock.Core.Networking;
using Windsock.Core.Plans;

namespace Windsock.Core.Feed;

/// <summary>Registers the home network features and the local feed.</summary>
public static class HomeAndFeedServices
{
    /// <summary>
    /// Adds the home device list, the router check, the household's traffic, the
    /// new-device notices and the local feed. Each does nothing until its switch
    /// is on. Needs the app's settings, time provider, notice sink, history store
    /// and monitors registered beside it.
    /// </summary>
    public static IServiceCollection AddHomeNetworkAndFeed(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(_ => new SqliteHomeStore(SqliteHomeStore.DefaultPath));
        services.AddSingleton<IHomeStore>(sp => sp.GetRequiredService<SqliteHomeStore>());
        services.AddSingleton(_ => new HomeLinkReader());
        services.AddSingleton<INeighbourSource, IpHelperNeighbours>();
        services.AddSingleton<IMulticastBrowser, WindowsMulticastBrowser>();
        services.AddSingleton<IDeviceNamer, DeviceNamer>();
        services.AddSingleton(_ => new MakerNames());
        services.AddSingleton<RouterClient>();
        services.AddSingleton<IRouterClient>(sp => sp.GetRequiredService<RouterClient>());

        services.AddSingleton<HomeDevices>();
        services.AddHostedService(sp => sp.GetRequiredService<HomeDevices>());
        services.AddSingleton<HouseholdTraffic>();
        services.AddHostedService(sp => sp.GetRequiredService<HouseholdTraffic>());
        services.AddSingleton<RouterWatch>();
        services.AddHostedService(sp => sp.GetRequiredService<RouterWatch>());
        services.AddHostedService<DeviceJoinedNotices>();
        services.AddSingleton<HomeEraseFollower>();
        services.AddHostedService(sp => sp.GetRequiredService<HomeEraseFollower>());
        services.AddSingleton<Insight.IErasedWithHistory>(sp => sp.GetRequiredService<HomeEraseFollower>());

        // The plans feature hands over its status through a function, so the
        // feed reads whatever plan is in force without knowing how it is kept.
        services.AddSingleton<IFeedSource>(sp => new FeedStatusSource(
            sp.GetRequiredService<ThroughputMonitor>(),
            sp.GetRequiredService<ConnectionMonitor>(),
            sp.GetRequiredService<IUsageHistoryStore>(),
            sp.GetRequiredService<SystemMonitor>(),
            sp.GetRequiredService<HouseholdTraffic>(),
            () => FeedPlans.From(sp.GetRequiredService<PlanMonitor>().Active),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<LocalFeed>();
        services.AddHostedService(sp => sp.GetRequiredService<LocalFeed>());

        return services;
    }
}
