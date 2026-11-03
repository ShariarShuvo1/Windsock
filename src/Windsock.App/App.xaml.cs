using System.Diagnostics.CodeAnalysis;
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
using Windsock.App.Services.Alerts;
using Windsock.App.Services.Connectivity;
using Windsock.App.Services.Network;
using Windsock.App.Services.Notifications;
using Windsock.App.Services.Usage;
using Windsock.App.Theming;
using Windsock.App.ViewModels;
using Windsock.App.Views;
using Windsock.Core;
using Windsock.Core.Alerts;
using Windsock.Core.Apps;
using Windsock.Core.Connectivity;
using Windsock.Core.Feed;
using Windsock.Core.Formatting;
using Windsock.Core.Hardware;
using Windsock.Core.Navigation;
using Windsock.Core.History;
using Windsock.Core.Insight;
using Windsock.Core.Networking;
using Windsock.Core.Notifications;
using Windsock.Core.Plans;
using Windsock.Core.Processes;
using Windsock.Core.Reports;
using Windsock.Core.Settings;
using Windsock.Core.Startup;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Windsock.App;

/// <summary>
/// Composition root. Owns the generic host carrying the collectors, the
/// history store and the machine readings as hosted services.
/// </summary>
public partial class App : Application
{
    private const string StillRunning = "Windsock is still running";

    /// <summary>
    /// How long a launch whose hand-off nobody answered waits for the lock
    /// before leaving, in case its holder is on its way out.
    /// </summary>
    private static readonly TimeSpan TakeOverWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// settings.json, read on a pool thread from the moment the application
    /// object exists, so it runs beside App.xaml's resources being parsed and
    /// the start-up below rather than in front of them; or nothing, for a
    /// Windsock replacing another, which reads it in <see cref="OnStartup"/>.
    /// </summary>
    /// <remarks>
    /// The first read costs 150 to 300 ms on a cold start, measured on
    /// 2026-10-05, nearly all of it the JIT compiling System.Text.Json's code
    /// for each of the settings' types and enums - the reflection is the small
    /// part, so a source-generated context made it no faster. Reading touches
    /// nothing but the file, and holds it only while copying it
    /// (<see cref="SettingsStore"/>), so a second launch that reads it for
    /// nothing and leaves cannot spoil the running one's save. A replacement
    /// (<c>--replacing</c>: restarting as administrator, after an import) must
    /// not read before the outgoing Windsock has gone, or what that one saves
    /// as it closes - the window's place, the last page, the compact window's
    /// spot - is missed and later written over.
    /// </remarks>
    private readonly Task<WindsockSettings>? _settingsRead =
        SingleInstance.IsReplacing(Environment.GetCommandLineArgs())
            ? null
            : Task.Run(static () => new SettingsStore(WindsockPaths.Settings).Load());

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

    /// <summary>
    /// The SQLite files <see cref="OnStartup"/> opens ahead of the host, or
    /// nothing when the host is built by anything else - a render harness
    /// calling <c>BuildHost</c> - which then opens them as it always did.
    /// </summary>
    private static Opening? _opening;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? link;
        bool tookOver;

        // Until the log is open a failure has nowhere to be written, and used
        // to end the process as a crash, or as a quiet 0, that nobody could
        // place. It now leaves a note and an exit code of its own (StartupExit).
        try
        {
            if (!Arrive(e, out link, out tookOver))
            {
                return;
            }
        }
#pragma warning disable CA1031 // Anything at all, written down where it can be found, then the process ends.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Leave(StartupExit.FailedBeforeLogging, "Failed before the log was open: " + error);
            return;
        }

        HookGlobalExceptionHandlers();

        if (tookOver)
        {
            Log.Warning("A Windsock held this data root's lock but did not answer, then let go; this one runs in its place");
        }
        else if (SingleInstance.IsReplacing(e.Args) && !_only.OwnsLock)
        {
            Log.Warning(
                "The Windsock being replaced had not let go after {Seconds} s; this one runs without the single-instance lock",
                SingleInstance.Handover.TotalSeconds);
        }

        // The three SQLite files open on pool threads while the settings, the
        // theme and the host's container are seen to on this one, and the host
        // takes them as its services ask. None of their constructors throws for
        // a file it cannot open or needs this thread. The first also runs
        // Microsoft.Data.Sqlite's static constructor, which loads the native
        // library and asks WinRT for a package's app data folder - throwing
        // twice, caught, in an unpackaged process - off this thread too.
        _opening = new Opening(
            Task.Run(static () => new SqliteUsageHistoryStore(WindsockPaths.Database)),
            Task.Run(static () => new SqliteOutageStore(WindsockPaths.Outages)),
            Task.Run(static () => new SqliteAlertLog(WindsockPaths.Alerts)));

        // Listening once logging is up, so a pipe name some other program holds
        // is written down; a second launch meanwhile waits a moment or falls back.
        _only.WakeRequested += (_, wake) => Dispatcher.BeginInvoke(new Action(() => Reveal(wake.Link)));
        _only.HandOffTrouble += (_, reason) => Log.Warning(
            "The hand-off pipe could not be made ({Reason}); a second launch will show the window as it was, not the page it names",
            reason);
        _only.Listen();
        SettingsStore store = new(
            WindsockPaths.Settings,
            new SerilogLoggerFactory(Log.Logger).CreateLogger<SettingsStore>());
        WindsockSettings settings = _settingsRead?.GetAwaiter().GetResult() ?? store.Load();

        // The registrations and the container need nothing of this thread - the
        // services themselves are made by Start, below, on this one - so they
        // are put together on a pool thread while the theme is applied here.
        ThemeManager theme = _theme = new ThemeManager(this);
        Task<IHost> built = Task.Run(() => BuildHost(store, settings, theme));

        theme.Start(settings.Theme);
        theme.ApplyAppearance(settings.Appearance);

        _host = built.GetAwaiter().GetResult();
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
        // A page asked for by name is a reason to show the window regardless.
        if (link is null && QuietStart.Wanted(e.Args) && settings.Taskbar.Enabled)
        {
            _tray.Show(TrayTip());
        }
        else
        {
            Reveal(link);
        }

        StartMeter();
        LookForUpdate(settings);

        // After VelopackApp.Run, which gives the process the id the list is
        // kept under, and behind the meter and the window: the shell takes a
        // tenth of a second over it, spent once the UI thread is first idle.
        _host.Services.GetRequiredService<JumpTasks>().ApplyWhenIdle(this);
    }

    /// <summary>
    /// Everything before the log is open: Velopack's start, the
    /// single-instance lock, the data folder and the log itself. False when
    /// this process is to leave, its exit code already given.
    /// </summary>
    [MemberNotNull(nameof(_only))]
    private bool Arrive(StartupEventArgs e, out string? link, out bool tookOver)
    {
        // Uninstalling takes the notification registration with it, and the
        // PATH entry if the person added one; both name this program's folder,
        // which is about to be gone. A hook run ends inside Run, with
        // Velopack's own exit code, which its Update.exe reads.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                ToastChannel.Unregister();
                CommandLinePath.ForgetOnUninstall();
            })
            .Run();
        _only = new SingleInstance(
            WindsockPaths.InstanceName,
            SingleInstance.IsReplacing(e.Args) ? SingleInstance.Handover : TimeSpan.Zero);

        // A page named on the command line - a jump list task, a shortcut,
        // "Windsock.exe --open processor" - is where the window opens, or is
        // handed to the Windsock already running.
        link = PageLink.FromArguments(e.Args);
        tookOver = false;

        if (_only.IsOnly)
        {
            WindsockPaths.EnsureCreated();
            ConfigureLogging();
            return true;
        }

        if (_only.Refused)
        {
            _only.Wake(link);
            Leave(
                StartupExit.LockRefused,
                "The single-instance lock could not be opened: a Windsock runs as administrator, or under another account's rights. It was asked to show itself.");
            return false;
        }

        if (_only.Wake(link, signalIfUnanswered: false))
        {
            Leave(StartupExit.HandedOff, "Handed over to the Windsock already running on this data root.");
            return false;
        }

        // Nobody answered. The holder may be on its way out - killed a moment
        // ago - so wait a little for the lock before giving up on running.
        if (!_only.TakeOver(TakeOverWait))
        {
            _only.Signal();
            Leave(
                StartupExit.Unanswered,
                "A Windsock holds this data root's lock but neither answered the hand-off nor let go. Its show-yourself event was set.");
            return false;
        }

        tookOver = true;
        WindsockPaths.EnsureCreated();
        ConfigureLogging();
        return true;
    }

    /// <summary>
    /// Ends a start that is not to run: the exit code says why
    /// (<see cref="StartupExit"/>), the debugger output says so, and anything
    /// but the everyday hand-off leaves a line in the data root's
    /// <see cref="StartupExit.NoteFile"/>.
    /// </summary>
    private void Leave(int code, string reason)
    {
        StartupExit.Note(WindsockPaths.Root, code, reason, keep: code != StartupExit.HandedOff, DateTimeOffset.Now);
        Shutdown(code);
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
        HookMeter(_meter);
        _meter.Apply();
    }

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

        // The compact window has a link of its own, and it is not a page.
        if (section == MiniLink)
        {
            ShowMini();
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

        HideTray();
        Welcome(window);
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
            UnhookMeter(_meter);
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
        // sends nothing. Only the full edition can go on to a reverse lookup where
        // the cache has no answer, and only it can walk routes, each only once
        // asked for on the privacy page or the map (OutsideContacts): the Store
        // edition's privacy policy promises its connection checks are all it sends.
        builder.Services.AddSingleton<IDnsCacheSource, WmiDnsCacheSource>();
#if STORE
        builder.Services.AddSingleton(sp => new NetworkRouteCache(sp.GetRequiredService<INetworkPathProbe>()) { IsEnabled = false });
        builder.Services.AddSingleton(sp => new HostNameCache(sp.GetRequiredService<IDnsCacheSource>(), askResolver: false));
#else
        builder.Services.AddSingleton(sp => new NetworkRouteCache(
            sp.GetRequiredService<INetworkPathProbe>(),
            sp.GetRequiredService<TimeProvider>()) { IsEnabled = settings.TraceRoutes });
        builder.Services.AddSingleton(sp => new HostNameCache(sp.GetRequiredService<IDnsCacheSource>(), askResolver: settings.ReverseLookups));
#endif
        builder.Services.AddSingleton<OutsideContacts>();

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

        builder.Services.AddSingleton<SqliteUsageHistoryStore>(_ =>
            _opening?.History.GetAwaiter().GetResult() ?? new SqliteUsageHistoryStore(WindsockPaths.Database));
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
        builder.Services.AddSingleton<SqliteOutageStore>(_ =>
            _opening?.Outages.GetAwaiter().GetResult() ?? new SqliteOutageStore(WindsockPaths.Outages));
        builder.Services.AddSingleton<IOutageStore>(sp => sp.GetRequiredService<SqliteOutageStore>());
        builder.Services.AddSingleton<ConnectionMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectionMonitor>());

        // Adapters and Wi-Fi, speed tests, the public address, the hop-by-hop
        // table: idle until a page or a switch asks.
        builder.Services.AddConnectionTools();

        // The machine's readings and the connection's checks, a minute at a
        // time beside network use.
        builder.Services.AddSingleton<IMachineHistoryStore>(sp => sp.GetRequiredService<SqliteUsageHistoryStore>());
        builder.Services.AddSingleton<MachineHistoryRecorder>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MachineHistoryRecorder>());
        builder.Services.AddSingleton<ConnectionAlerts>();

        // Usage and data plans: the network each minute was measured on, the
        // plans worked out from history, reports, and what is done with history
        // by itself - scheduled export, a weekly backup, a weekly digest. All of
        // it reads Windows and the history file; none of it sends anything.
        builder.Services.AddSingleton<IUsageAggregates>(sp => sp.GetRequiredService<SqliteUsageHistoryStore>());
        builder.Services.AddSingleton<IHistoryNotes>(sp => sp.GetRequiredService<SqliteUsageHistoryStore>());
        builder.Services.AddSingleton<IHistoryFile>(sp => sp.GetRequiredService<SqliteUsageHistoryStore>());
        builder.Services.AddSingleton<IPlanLevelStore>(sp => sp.GetRequiredService<SqliteUsageHistoryStore>());
        builder.Services.AddSingleton<ILocalTrafficStore>(sp => sp.GetRequiredService<SqliteUsageHistoryStore>());
        builder.Services.AddSingleton<WindowsNetworkSightingSource>();
        builder.Services.AddSingleton<INetworkSightingSource>(sp => sp.GetRequiredService<WindowsNetworkSightingSource>());
        builder.Services.AddSingleton<CurrentNetwork>();
        builder.Services.AddSingleton<INetworkTagSource>(sp => sp.GetRequiredService<CurrentNetwork>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CurrentNetwork>());
        builder.Services.AddSingleton<PlanCalculator>();
        builder.Services.AddSingleton<PlanMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<PlanMonitor>());
        builder.Services.AddSingleton<UsageReports>();
        builder.Services.AddSingleton<HistoryJson>();
        builder.Services.AddSingleton<ScheduledExport>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ScheduledExport>());
        builder.Services.AddSingleton<HistoryBackup>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HistoryBackup>());
        builder.Services.AddSingleton<INoticeSink>(sp => new NoticeSink(notice => sp.GetRequiredService<NotificationService>().Send(notice)));
        builder.Services.AddSingleton<DigestService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DigestService>());

        // The home network (devices, router check, household traffic) and the
        // local feed. Every one is off until switched on; none reaches past the
        // router, and the feed listens on loopback only.
        builder.Services.AddHomeNetworkAndFeed();
        builder.Services.AddSingleton<CommandLinePath>();

        // Insight and control over the programs using the network: new-app
        // sightings, signatures, service names, blocklists and VirusTotal (both
        // off until switched on), Windows Update's share; in the full edition
        // the timeline (off until switched on), the local/internet split and
        // each app's name lookups, which need the kernel trace.
        builder.Services.AddInsight();
        builder.Services.AddSingleton<InsightActions>();
        builder.Services.AddSingleton<ListedConnections>();
        builder.Services.AddSingleton<AppFacts>();

        builder.Services.AddSingleton<MeterCardViewModel>();
        builder.Services.AddSingleton<HotkeyService>();
        builder.Services.AddSingleton<TaskbarMeter>();
        builder.Services.AddSingleton<JumpTasks>();
        builder.Services.AddSingleton<TrayIcon>();
        builder.Services.AddSingleton<NotificationService>();

        // Alerts. Every notice passes the desk on its way out - flood control,
        // quiet hours, the sound chosen for its kind, the record in Alerts.db,
        // forwarding - and each source below watches only while its alerts are
        // on. Forwarding is the only part that sends anything, and only once the
        // person sets up somewhere to send to.
        builder.Services.AddSingleton<SqliteAlertLog>(_ =>
            _opening?.Alerts.GetAwaiter().GetResult() ?? new SqliteAlertLog(WindsockPaths.Alerts));
        builder.Services.AddSingleton<IAlertLog>(sp => sp.GetRequiredService<SqliteAlertLog>());
        builder.Services.AddSingleton<AlertSettling>();
        builder.Services.AddSingleton<HttpAlertForwarder>();
        builder.Services.AddSingleton<IAlertForwarder>(sp => sp.GetRequiredService<HttpAlertForwarder>());
        builder.Services.AddSingleton<AlertDesk>();
        builder.Services.AddSingleton<EventTimeline>();
        builder.Services.AddSingleton<SystemSessionEvents>();
        builder.Services.AddSingleton<ISessionEvents>(sp => sp.GetRequiredService<SystemSessionEvents>());
        builder.Services.AddSingleton<ProxyWatch>();
        builder.Services.AddSingleton<IRouterAddressSource, NeighbourTableRouterAddress>();
        builder.Services.AddSingleton<IAlertSource, ConnectionEvents>();
        builder.Services.AddSingleton<IAlertSource, PingAlerts>();
        builder.Services.AddSingleton<IAlertSource, SpeedAlerts>();
        builder.Services.AddSingleton<IAlertSource, HardwareAlerts>();
        builder.Services.AddSingleton<IAlertSource, PlanAlerts>();
        builder.Services.AddSingleton<IAlertSource, AwayAlerts>();
        builder.Services.AddSingleton<IAlertSource, ProxyAlerts>();
        builder.Services.AddSingleton<IAlertSource, NetworkChangeAlerts>();
#if !STORE
        builder.Services.AddSingleton<IAlertSource, UpdateAlerts>();
#endif
        builder.Services.AddSingleton<AlertService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertService>());

        // Shared rather than scoped: the per-process windows can outlive the
        // main window, and they take their units from this.
        builder.Services.AddSingleton<ThroughputOverviewViewModel>();

        // The marks on the live chart, read only while it is live.
        builder.Services.AddScoped<ChartMarkersFeed>();

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
        builder.Services.AddScoped<MapPanelViewModel>();
        builder.Services.AddScoped<MapPanelView>();
        builder.Services.AddScoped<DevicesPanelViewModel>();
        builder.Services.AddScoped<DevicesPanelView>();
        builder.Services.AddScoped<HouseholdFigureViewModel>();
        builder.Services.AddScoped<HomeFeedSettingsViewModel>();
        builder.Services.AddScoped<ActivityPanelViewModel>();
        builder.Services.AddScoped<ActivityPanelView>();
        builder.Services.AddScoped<InsightPrivacyViewModel>();
        builder.Services.AddScoped<UpdateShareViewModel>();
        builder.Services.AddScoped<ConnectionPanelViewModel>();
        builder.Services.AddScoped<ConnectionPanelView>();
        builder.Services.AddScoped<OutagesPanelViewModel>();
        builder.Services.AddScoped<OutagesPanelView>();
        builder.Services.AddScoped<HistoryPanelViewModel>();
        builder.Services.AddScoped<HistoryPanelView>();
        builder.Services.AddScoped<DataPlanPanelViewModel>();
        builder.Services.AddScoped<DataPlanPanelView>();
        builder.Services.AddScoped<ReportsPanelViewModel>();
        builder.Services.AddScoped<HistoryMarksViewModel>();
        builder.Services.AddScoped<AppTimelineViewModel>();
        builder.Services.AddScoped<HistoryCareViewModel>();
        builder.Services.AddScoped<HistoryCareView>();
        builder.Services.AddScoped<ReportsPanelView>();
        builder.Services.AddScoped<TaskbarPanelViewModel>();
        builder.Services.AddScoped<TaskbarPanelView>();
        builder.Services.AddScoped<MeterPrivacyViewModel>();
        builder.Services.AddScoped<MeterPrivacyRows>();

        builder.Services.AddScoped<NotificationsCardViewModel>();
        builder.Services.AddScoped<AlertsPanelViewModel>();
        builder.Services.AddScoped<AlertsPanelView>();
        builder.Services.AddScoped<SettingsPanelViewModel>();
        builder.Services.AddScoped<SettingsPanelView>();
        builder.Services.AddScoped<ThroughputOverviewView>();
        builder.Services.AddScoped<LanSplitViewModel>();
        builder.Services.AddScoped<SidebarViewModel>();
        builder.Services.AddScoped<MainWindow>();

        return builder.Build();
    }

    /// <summary>The files start-up opens ahead of the host, each on a pool thread of its own.</summary>
    private sealed record Opening(
        Task<SqliteUsageHistoryStore> History,
        Task<SqliteOutageStore> Outages,
        Task<SqliteAlertLog> Alerts);

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
