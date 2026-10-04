using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Velopack;
using Windsock.App.Hosting;
using Windsock.App.Services;
using Windsock.App.Services.Connectivity;
using Windsock.App.Services.Notifications;
using Windsock.App.Services.Usage;
using Windsock.App.Theming;
using Windsock.App.ViewModels;
using Windsock.App.Views;
using Windsock.Core;
using Windsock.Core.Apps;
using Windsock.Core.Connectivity;
using Windsock.Core.Formatting;
using Windsock.Core.Hardware;
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Processes;
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
    private const string StillRunning = "Windsock is still running";

    private IHost? _host;
    private ThemeManager? _theme;
    private TaskbarMeter? _meter;
    private TrayIcon? _tray;
    private NotificationService? _notices;
    private ConnectionMonitor? _connection;
    private ConnectionAlerts? _alerts;
    private WindsockSettings? _settings;
    private IServiceScope? _windowScope;
    private MainWindow? _window;
    private static SingleInstance? _only;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Uninstalling takes the notification registration with it; it names
        // this program, which is about to be gone.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => ToastChannel.Unregister())
            .Run();
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

        _settings = settings;
        _tray = _host.Services.GetRequiredService<TrayIcon>();
        _tray.Opened += OnTrayOpened;
        _tray.MenuRequested += OnTrayMenuRequested;

        // Before anything opens a window: a click on an older notification can be
        // what started this process, and Windows delivers that click only once
        // Windsock is listening for it.
        _notices = _host.Services.GetRequiredService<NotificationService>();
        _notices.Opened += OnNoticeOpened;
        _notices.Start();

        // Watching the connection in the background is what the alerts, the
        // outage log and the meter's offline dash all hang off.
        _connection = _host.Services.GetRequiredService<ConnectionMonitor>();
        _connection.StateChanged += OnConnectionChanged;
        _alerts = _host.Services.GetRequiredService<ConnectionAlerts>();
        _alerts.Apply();

        // A quiet start builds no window at all. The meter and the recorder
        // live in the host, so neither needs one, and the icon is the way in.
        if (QuietStart.Wanted(e.Args) && settings.Taskbar.Enabled)
        {
            _tray.Show(TrayTip());
        }
        else
        {
            Reveal();
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

    private void OnTrayOpened(object? sender, EventArgs e) => Reveal();

    private void OnNoticeOpened(object? sender, NoticeOpenedEventArgs e) => Reveal(e.Section);

    private void OnConnectionChanged(object? sender, ConnectionState state) =>
        Dispatcher.BeginInvoke(new Action(() => _tray?.SetTooltip(TrayTip())));

    /// <summary>
    /// What the notification-area icon says on hover: that Windsock is still
    /// running, or, while the internet is gone, that it is gone and since when.
    /// </summary>
    private string TrayTip() =>
        _connection is { } connection
        && MeterReadout.Offline(new MeterReading(0, 0, SystemSnapshot.Empty) { Connection = connection.State }) is { } offline
            ? string.Concat("Windsock · ", offline)
            : StillRunning;

    private void Reveal() => Reveal(null);

    /// <summary>
    /// Brings the main window to the front, building it first if it is not
    /// open, and turns to a section when one is named.
    /// </summary>
    private void Reveal(string? section)
    {
        if (_host is null || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        MainWindow window = _window ?? Open(_host);

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        if (section is not null)
        {
            window.ShowSection(section);
        }

        window.Show();
        _ = window.Activate();

        // Activation is refused to a process that is not already in front,
        // which is the usual case for a wake from a second launch.
        window.Topmost = true;
        window.Topmost = false;

        _tray?.Hide();
    }

    /// <summary>
    /// Builds the main window and every panel in it, in a scope of their own so
    /// that closing it can let go of all of them at once.
    /// </summary>
    private MainWindow Open(IHost host)
    {
        _windowScope = host.Services.CreateScope();

        MainWindow window = _windowScope.ServiceProvider.GetRequiredService<MainWindow>();
        window.Closed += OnWindowClosed;

        _window = window;
        MainWindow = window;

        return window;
    }

    /// <summary>
    /// Discards the window that just closed, then either quits or carries on
    /// in the background, as the setting says.
    /// </summary>
    /// <remarks>
    /// The window used to be hidden rather than closed. Hidden, it stayed
    /// loaded, and a panel that followed loading rather than visibility went on
    /// collecting for a window nobody could see; all of it stayed in memory
    /// besides. Closed and disposed, nothing is left to do that.
    /// </remarks>
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window closed)
        {
            closed.Closed -= OnWindowClosed;

            if (ReferenceEquals(MainWindow, closed))
            {
                MainWindow = null;
            }
        }

        _window = null;

        IServiceScope? scope = _windowScope;
        _windowScope = null;
        scope?.Dispose();

        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (_settings?.OnClose != CloseAction.KeepRunning)
        {
            Shutdown();
            return;
        }

        _tray?.Show(TrayTip());
    }

    private void OnTrayMenuRequested(object? sender, EventArgs e)
    {
        System.Windows.Controls.ContextMenu menu = new()
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };

        System.Windows.Controls.MenuItem open = new() { Header = "Open Windsock" };
        open.Click += (_, _) => Reveal();

        System.Windows.Controls.MenuItem quit = new() { Header = "Quit" };
        quit.Click += (_, _) => Shutdown();

        menu.Items.Add(open);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(quit);

        _tray?.TakeForeground();
        menu.IsOpen = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _windowScope?.Dispose();
        _windowScope = null;
        _window = null;

        if (_connection is not null)
        {
            _connection.StateChanged -= OnConnectionChanged;
            _connection = null;
        }

        _alerts?.Dispose();
        _alerts = null;

        if (_notices is not null)
        {
            _notices.Opened -= OnNoticeOpened;
            _notices.Dispose();
            _notices = null;
        }

        if (_tray is not null)
        {
            _tray.Opened -= OnTrayOpened;
            _tray.MenuRequested -= OnTrayMenuRequested;
            _tray.Dispose();
            _tray = null;
        }

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

        // Run at login has two mechanisms and only one works per edition. The
        // Run key is redirected into a private copy inside the MSIX container,
        // so a packaged Windsock must use the StartupTask from its manifest or
        // the setting turns on, reads back as on, and starts nothing. Chosen by
        // what the process actually is rather than by build flag, so a packaged
        // full edition would get the right one too.
        builder.Services.AddSingleton<WindowsStartup>();
        if (PackageIdentity.Exists)
        {
            builder.Services.AddSingleton<IStartupSwitch, PackagedStartup>();
        }
        else
        {
            builder.Services.AddSingleton<IStartupSwitch>(sp => sp.GetRequiredService<WindowsStartup>());
        }

        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddSingleton(_ => new IpHelperTotalsSource { Selection = [.. settings.Adapters] });
        builder.Services.AddSingleton<INetworkTotalsSource>(sp => sp.GetRequiredService<IpHelperTotalsSource>());
        builder.Services.AddSingleton<INetworkAdapterSource>(sp => sp.GetRequiredService<IpHelperTotalsSource>());
        builder.Services.AddSingleton<ThroughputMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ThroughputMonitor>());

        // The process list, its sockets and its connections need no privilege,
        // so both editions have them. What differs is what counts each
        // process's bytes as they move: a kernel trace, which only an
        // administrator can open, in the full edition; nothing at all in the
        // Store edition, which never asks for administrator rights and is built
        // without the trace.
        builder.Services.AddOptions<ProcessUsageOptions>();
#if STORE
        builder.Services.AddSingleton<WithheldTrafficSource>();
        builder.Services.AddSingleton<IProcessTrafficSource>(sp => sp.GetRequiredService<WithheldTrafficSource>());
        builder.Services.AddSingleton<IProcessEndpointSource>(sp => sp.GetRequiredService<WithheldTrafficSource>());
#else
        builder.Services.AddSingleton<EtwProcessTrafficSource>();
        builder.Services.AddSingleton<IProcessTrafficSource>(sp => sp.GetRequiredService<EtwProcessTrafficSource>());
        builder.Services.AddSingleton<IProcessEndpointSource>(
            sp => sp.GetRequiredService<EtwProcessTrafficSource>());
#endif
        builder.Services.AddSingleton<IProcessConnectionSource, IpHelperConnectionSource>();
        builder.Services.AddSingleton<IProcessIdentityResolver, ProcessIdentityResolver>();
        builder.Services.AddSingleton<IProcessConnectionDetailSource, IpHelperConnectionDetails>();

        // Addresses are named from the DNS client's cache in both editions, which
        // sends nothing. Only the full edition goes on to a reverse lookup where
        // the cache has no answer, and only it walks routes: the Store edition's
        // privacy policy promises its connection checks are all it sends.
        builder.Services.AddSingleton<IDnsCacheSource, WmiDnsCacheSource>();
#if STORE
        builder.Services.AddSingleton(sp => new NetworkRouteCache(sp.GetRequiredService<INetworkPathProbe>()) { IsEnabled = false });
        builder.Services.AddSingleton(sp => new HostNameCache(sp.GetRequiredService<IDnsCacheSource>(), askResolver: false));
#else
        builder.Services.AddSingleton<NetworkRouteCache>();
        builder.Services.AddSingleton(sp => new HostNameCache(sp.GetRequiredService<IDnsCacheSource>(), askResolver: true));
#endif

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

        builder.Services.AddSingleton<SqliteUsageHistoryStore>(_ => new SqliteUsageHistoryStore(WindsockPaths.Database));
        builder.Services.AddSingleton<IUsageHistoryStore>(sp => sp.GetRequiredService<SqliteUsageHistoryStore>());
        builder.Services.AddSingleton<IAppUsageStore>(sp => sp.GetRequiredService<SqliteUsageHistoryStore>());
        builder.Services.AddSingleton<IAppDetailStore>(sp => sp.GetRequiredService<SqliteUsageHistoryStore>());
        builder.Services.AddSingleton<AppHistory>();
        builder.Services.AddSingleton<UsageRecorder>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UsageRecorder>());
#if !STORE
        // Each app's minutes, measured by the trace itself while it runs. The
        // Store edition has no trace, so its per-app history is Windows' alone.
        builder.Services.AddSingleton<IProcessTrafficLedger>(sp => sp.GetRequiredService<EtwProcessTrafficSource>());
        builder.Services.AddSingleton<AppMinuteRecorder>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AppMinuteRecorder>());
#endif

        // Each app's use, copied out of Windows' own data usage: per-app history
        // in both editions, and a per-program figure where no trace is running.
        builder.Services.AddSingleton<DevicePaths>();
        builder.Services.AddSingleton<AppNames>();
        builder.Services.AddSingleton<IAppUsageReader, WindowsAppUsageReader>();
        builder.Services.AddSingleton<AppUsageCollector>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AppUsageCollector>());
        builder.Services.AddSingleton<AppIconCache>();
        builder.Services.AddSingleton<RecentAppFigures>();
        builder.Services.AddOptions<SystemMonitorOptions>();
        builder.Services.AddSingleton<SystemMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SystemMonitor>());

        // Both editions: the connection checks ping the router, and an internet
        // address only when one is chosen. The route tracing that also uses this
        // probe is still full edition only.
        builder.Services.AddSingleton<INetworkPathProbe, PingNetworkPathProbe>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<WindowsConnectionFactsSource>();
        builder.Services.AddSingleton<IConnectionFactsSource>(sp => sp.GetRequiredService<WindowsConnectionFactsSource>());
        builder.Services.AddSingleton<SqliteOutageStore>(_ => new SqliteOutageStore(WindsockPaths.Outages));
        builder.Services.AddSingleton<IOutageStore>(sp => sp.GetRequiredService<SqliteOutageStore>());
        builder.Services.AddSingleton<ConnectionMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectionMonitor>());

        // The machine's readings and the connection's checks, a minute at a
        // time beside network use.
        builder.Services.AddSingleton<IMachineHistoryStore>(sp => sp.GetRequiredService<SqliteUsageHistoryStore>());
        builder.Services.AddSingleton<MachineHistoryRecorder>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MachineHistoryRecorder>());
        builder.Services.AddSingleton<ConnectionAlerts>();

        builder.Services.AddSingleton<TaskbarMeter>();
        builder.Services.AddSingleton<TrayIcon>();
        builder.Services.AddSingleton<NotificationService>();

        // Shared rather than scoped: the per-process windows can outlive the
        // main window, and they take their units from this.
        builder.Services.AddSingleton<ThroughputOverviewViewModel>();

        // Everything below exists only while the main window is open. Each
        // opening gets a scope of its own and closing disposes it, so none of
        // these may be asked for from a singleton, and each must let go of
        // anything longer-lived it subscribed to when it is disposed.
        builder.Services.AddScoped<SystemPanelViewModel>();
        builder.Services.AddScoped<SystemPanelView>();
        builder.Services.AddScoped<LiveAppsViewModel>();
        builder.Services.AddScoped<AppTotalsViewModel>();
        builder.Services.AddScoped<AppsPanelViewModel>();
        builder.Services.AddScoped<AppsPanelView>();
        builder.Services.AddScoped<ConnectionPanelViewModel>();
        builder.Services.AddScoped<ConnectionPanelView>();
        builder.Services.AddScoped<HistoryPanelViewModel>();
        builder.Services.AddScoped<HistoryPanelView>();
        builder.Services.AddScoped<TaskbarPanelViewModel>();
        builder.Services.AddScoped<TaskbarPanelView>();

        builder.Services.AddScoped<NotificationsCardViewModel>();
        builder.Services.AddScoped<SettingsPanelViewModel>();
        builder.Services.AddScoped<SettingsPanelView>();
        builder.Services.AddScoped<ThroughputOverviewView>();
        builder.Services.AddScoped<MainWindow>();

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
