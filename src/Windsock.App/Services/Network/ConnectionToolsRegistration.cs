using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windsock.App.ViewModels;
using Windsock.App.Views;
using Windsock.Core.Adapters;
using Windsock.Core.Asn;
using Windsock.Core.Connectivity;
using Windsock.Core.Lookups;
using Windsock.Core.Networking;
using Windsock.Core.Settings;
using Windsock.Core.SpeedTests;
using Windsock.Core.Wifi;

namespace Windsock.App.Services.Network;

/// <summary>
/// Registers the engines behind the Adapters and Wi-Fi page and the deeper
/// Connection pages, in one place so the composition root gains one line.
/// </summary>
/// <remarks>
/// Nothing registered here reads or sends anything until something asks: the
/// monitors wait for a watcher, the lookups for a click, and the scheduler for
/// its switch. The quality recorder only adds up checks the connection monitor
/// already makes.
/// </remarks>
public static class ConnectionToolsRegistration
{
    /// <summary>Adds the services. Expects the connection monitor, the outage store and the path probe to be registered already.</summary>
    public static IServiceCollection AddConnectionTools(this IServiceCollection services)
    {
        services.AddSingleton<IAdapterReader, IpHelperAdapterReader>();
        services.AddSingleton<IWindowsConnectivitySource, NetworkListConnectivity>();
        services.AddSingleton<IAdapterProfileSource, WindowsAdapterProfiles>();
        services.AddSingleton(sp => new AdapterMonitor(
            sp.GetRequiredService<IAdapterReader>(),
            sp.GetRequiredService<IWindowsConnectivitySource>(),
            sp.GetRequiredService<IAdapterProfileSource>(),
            sp.GetRequiredService<ILogger<AdapterMonitor>>()));

        services.AddSingleton<WlanClient>();
        services.AddSingleton<IWlanClient>(sp => sp.GetRequiredService<WlanClient>());
        services.AddSingleton<WifiMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<WifiMonitor>());
        services.AddSingleton<WifiScanner>();
        services.AddSingleton<WifiDropLog>();
#if !STORE
        services.AddSingleton<WifiKeyReader>();
        services.AddSingleton<NetworkFixes>();
#endif

        services.AddSingleton(_ => new AsnDirectory());
        services.AddSingleton(sp =>
        {
            PublicAddressLookup lookup = new(
                sp.GetRequiredService<WindsockSettings>(),
                sp.GetRequiredService<AsnDirectory>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<PublicAddressLookup>>());

            // A new network may mean a new public address.
            sp.GetRequiredService<IConnectionFactsSource>().Changed += (_, _) => lookup.Forget();
            return lookup;
        });

        services.AddSingleton<ConnectionProbes>();

        services.AddSingleton<ISpeedTestStore>(sp => sp.GetRequiredService<SqliteOutageStore>());
        services.AddSingleton<IQualityStore>(sp => sp.GetRequiredService<SqliteOutageStore>());
        services.AddSingleton(sp => new SpeedTestService(
            sp.GetRequiredService<WindsockSettings>(),
            sp.GetRequiredService<ISpeedTestStore>(),
            sp.GetRequiredService<ConnectionMonitor>(),
            sp.GetRequiredService<INetworkPathProbe>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<SpeedTestService>>(),
            handler: null,
            scanner: sp.GetRequiredService<WifiScanner>()));
        services.AddSingleton<SpeedTestScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<SpeedTestScheduler>());
        services.AddSingleton<QualityRecorder>();
        services.AddHostedService(sp => sp.GetRequiredService<QualityRecorder>());

        services.AddSingleton<IHopProbe>(sp => sp.GetRequiredService<INetworkPathProbe>() as IHopProbe ?? new PingNetworkPathProbe());
        services.AddSingleton<HopWatch>();

        // The pages, one scope per opening of the window like every other page.
        services.AddScoped<AdaptersPanelViewModel>();
        services.AddScoped<AdaptersPanelView>();
        services.AddScoped<SpeedTestPanelViewModel>();
        services.AddScoped<SpeedTestPanelView>();
        services.AddScoped<ConnectionDeeperViewModel>();
        services.AddScoped<ConnectionDeeperView>();
        services.AddScoped<SignalStripViewModel>();
        services.AddScoped<ConnectionToolsPrivacyViewModel>();
        services.AddScoped<ConnectionToolsPrivacyRows>();

        return services;
    }
}
