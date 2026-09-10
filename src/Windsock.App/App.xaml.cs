using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
#if !STORE
using Microsoft.Extensions.Options;
#endif
using Velopack;
using Windsock.App.Hosting;
using Windsock.App.Services;
using Windsock.App.Theming;
using Windsock.App.ViewModels;
using Windsock.App.Views;
using Windsock.Core;
using Windsock.Core.Hardware;
using Windsock.Core.History;
using Windsock.Core.Networking;
#if !STORE
using Windsock.Core.Processes;
#endif
using Windsock.Core.Settings;
using Windsock.Core.Startup;
using Serilog;
using Serilog.Events;

namespace Windsock.App;

/// <summary>
/// Composition root. Owns the generic host carrying the collectors, the
/// history store and the machine readings as hosted services.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private ThemeManager? _theme;
    private TaskbarMeter? _meter;
    private static SingleInstance? _only;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        VelopackApp.Build().Run();
        _only = new SingleInstance(
            "Windsock",
            SingleInstance.IsReplacing(e.Args) ? SingleInstance.Handover : TimeSpan.Zero);

        if (!_only.IsOnly)
        {
            _only.Wake();
            Shutdown();
            return;
        }

        _only.WakeRequested += (_, _) => Dispatcher.BeginInvoke(new Action(Reveal));
        _only.Listen();

        WindsockPaths.EnsureCreated();
        ConfigureLogging();
        HookGlobalExceptionHandlers();
        SettingsStore store = new(WindsockPaths.Settings);
        WindsockSettings settings = store.Load();

        _theme = new ThemeManager(this);
        _theme.Start(settings.Theme);

        _host = BuildHost(store, settings, _theme);
        _host.Start();

        Log.Information("Windsock {Version} started", typeof(App).Assembly.GetName().Version);

        MainWindow window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        if (!WindowsStartup.IsQuiet(e.Args) || !settings.Taskbar.Enabled)
        {
            window.Show();
        }

        StartMeter();
        LookForUpdate(settings);
    }

    private void LookForUpdate(WindsockSettings settings)
    {
        if (_host is null || !settings.CheckForUpdates)
        {
            return;
        }

        UpdateService updates = _host.Services.GetRequiredService<UpdateService>();

        if (!updates.CanUpdate)
        {
            return;
        }

        _ = updates.CheckAsync(quietly: true);
    }

    private void StartMeter()
    {
        if (_host is null)
        {
            return;
        }

        _meter = _host.Services.GetRequiredService<TaskbarMeter>();
        _meter.OpenRequested += OnMeterChosen;
        _meter.Apply();
    }

    private void OnMeterChosen(object? sender, EventArgs e) => Reveal();

    private void Reveal()
    {
        if (MainWindow is not { } window)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_meter is not null)
        {
            _meter.OpenRequested -= OnMeterChosen;
            _meter.Dispose();
            _meter = null;
        }

        if (_host is not null)
        {
            // Bounded so a wedged hosted service cannot block shutdown forever.
            _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            _host.Dispose();
            _host = null;
        }

        _theme?.Stop();
        _theme = null;

        _only?.Dispose();
        _only = null;

        Log.Information("Windsock exited with code {ExitCode}", e.ApplicationExitCode);
        Log.CloseAndFlush();

        base.OnExit(e);
    }

    private static IHost BuildHost(SettingsStore store, WindsockSettings settings, ThemeManager theme)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                ContentRootPath = AppContext.BaseDirectory,
                ApplicationName = "Windsock",
            });

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();

        // Registered after the default so it wins resolution.
        builder.Services.AddSingleton<IHostLifetime, WpfHostLifetime>();
        builder.Services.AddSingleton(_ => Current.Dispatcher);

        // Created before the host, and shared with it.
        builder.Services.AddSingleton<ISettingsStore>(store);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(theme);

        builder.Services.AddOptions<ThroughputMonitorOptions>();
        builder.Services.AddSingleton<WindowsStartup>();
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddSingleton(_ => new IpHelperTotalsSource { Selection = [.. settings.Adapters] });
        builder.Services.AddSingleton<INetworkTotalsSource>(sp => sp.GetRequiredService<IpHelperTotalsSource>());
        builder.Services.AddSingleton<INetworkAdapterSource>(sp => sp.GetRequiredService<IpHelperTotalsSource>());
        builder.Services.AddSingleton<ThroughputMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ThroughputMonitor>());

#if !STORE
        builder.Services.AddOptions<ProcessUsageOptions>();
        builder.Services.AddSingleton<EtwProcessTrafficSource>();
        builder.Services.AddSingleton<IProcessTrafficSource>(sp => sp.GetRequiredService<EtwProcessTrafficSource>());
        builder.Services.AddSingleton<IProcessEndpointSource>(
            sp => sp.GetRequiredService<EtwProcessTrafficSource>());
        builder.Services.AddSingleton<IProcessConnectionSource, IpHelperConnectionSource>();
        builder.Services.AddSingleton<IProcessIdentityResolver, ProcessIdentityResolver>();
        builder.Services.AddSingleton<IProcessConnectionDetailSource, IpHelperConnectionDetails>();
        builder.Services.AddSingleton<INetworkPathProbe, PingNetworkPathProbe>();
        builder.Services.AddSingleton<NetworkRouteCache>();

        builder.Services.AddSingleton<HostNameCache>();
        builder.Services.AddSingleton<ProcessIconCache>();
        builder.Services.AddSingleton<ProcessMonitorLauncher>();
        builder.Services.AddSingleton(sp =>
        {
            ProcessUsageOptions options = sp.GetRequiredService<IOptions<ProcessUsageOptions>>().Value;
            return new ProcessAttributionHistory(
                options.AttributionCapacity,
                options.AttributionWidth,
                options.Interval);
        });

        builder.Services.AddSingleton<ProcessUsageMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessUsageMonitor>());
#endif
        builder.Services.AddSingleton<SqliteUsageHistoryStore>(_ => new SqliteUsageHistoryStore(WindsockPaths.Database));
        builder.Services.AddSingleton<IUsageHistoryStore>(sp => sp.GetRequiredService<SqliteUsageHistoryStore>());
        builder.Services.AddSingleton<UsageRecorder>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UsageRecorder>());
        builder.Services.AddOptions<SystemMonitorOptions>();
        builder.Services.AddSingleton<SystemMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SystemMonitor>());

        builder.Services.AddSingleton<ThroughputOverviewViewModel>();
        builder.Services.AddSingleton<SystemPanelViewModel>();
        builder.Services.AddSingleton<SystemPanelView>();
#if !STORE
        builder.Services.AddSingleton<ProcessPanelViewModel>();
        builder.Services.AddSingleton<ProcessPanelView>();
#endif
        builder.Services.AddSingleton<HistoryPanelViewModel>();
        builder.Services.AddSingleton<HistoryPanelView>();
        builder.Services.AddSingleton<TaskbarMeter>();
        builder.Services.AddSingleton<TaskbarPanelViewModel>();
        builder.Services.AddSingleton<TaskbarPanelView>();

        builder.Services.AddSingleton<SettingsPanelViewModel>();
        builder.Services.AddSingleton<SettingsPanelView>();
        builder.Services.AddSingleton<ThroughputOverviewView>();
        builder.Services.AddSingleton<WorkspaceView>();
        builder.Services.AddSingleton<MainWindow>();

        return builder.Build();
    }

    private static void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(
                Path.Combine(WindsockPaths.Logs, "Windsock-.log"),
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 8L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true)
            .CreateLogger();
    }

    private void HookGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
            Log.Fatal(args.Exception, "Unhandled exception on the UI thread");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception on a background thread");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }
}
