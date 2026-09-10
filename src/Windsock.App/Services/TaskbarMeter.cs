using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windsock.App.ViewModels;
using Windsock.App.Views;
using Windsock.Core.Formatting;
using Windsock.Core.Hardware;
using Windsock.Core.Networking;
using Windsock.Core.Settings;

namespace Windsock.App.Services;

/// <summary>What the meter managed to do with what it was asked for.</summary>
public enum MeterStatus
{
    Off,

    Docked,

    Floating,

    Unavailable,
}

/// <summary>
/// Owns the meter that sits in the Windows taskbar: whether it exists, where it
/// is, and what it says.
/// </summary>
public sealed partial class TaskbarMeter : IDisposable
{
    private static readonly TimeSpan Patrol = TimeSpan.FromMilliseconds(300);

    private const double Padding = 4;
    private readonly ThroughputMonitor _network;
    private readonly SystemMonitor _machine;
    private readonly WindsockSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<TaskbarMeter> _logger;
    private readonly DispatcherTimer _patrol;
    private TaskbarMeterWindow? _window;
    private TaskbarMeterViewModel? _meter;
    private IDisposable? _watch;
    private DockPlacement _placed;
    private SystemSnapshot _snapshot = SystemSnapshot.Empty;
    private MeterPlacement _placement;
    private nint _host;
    private bool _dark;
    private bool _listening;
    private bool _pending;
    private bool _disposed;
    private double _download;
    private double _upload;

    public TaskbarMeter(
        ThroughputMonitor network,
        SystemMonitor machine,
        WindsockSettings settings,
        Dispatcher dispatcher,
        ILogger<TaskbarMeter> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _network = network;
        _machine = machine;
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;

        _patrol = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Patrol };
        _patrol.Tick += (_, _) => Station();
    }

    /// <summary>Raised when the reader clicks the meter and asked for that to open Windsock.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Raised when <see cref="Status"/> changes.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>What the meter is currently doing.</summary>
    public MeterStatus Status { get; private set; }

    /// <summary>
    /// Makes what is on screen match what the settings say.
    /// </summary>
    public void Apply()
    {
        if (_disposed)
        {
            return;
        }

        TaskbarSettings settings = _settings.Taskbar;

        if (!settings.Enabled)
        {
            Teardown();
            Report(MeterStatus.Off);
            return;
        }
        bool dark = TaskbarIsDark();

        if (_window is not null && (_dark != dark || _placement != settings.Placement))
        {
            Teardown();
        }

        if (_window is null && !Raise(dark))
        {
            Report(MeterStatus.Unavailable);
            return;
        }

        Listen(settings);
        _meter!.Apply(settings);

        Station(force: true);
    }

    /// <summary>Takes the meter off the taskbar and stops everything behind it.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Teardown();
    }

    private bool Raise(bool dark)
    {
        try
        {
            return Build(dark);
        }
#pragma warning disable CA1031 // There is no useful narrower set: this guards a
        // whole subsystem, and every outcome is the same - no meter, app fine.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBuildFailed(ex);
            Teardown();
            return false;
        }
    }

    private bool Build(bool dark)
    {
        TaskbarSettings settings = _settings.Taskbar;
        bool docked = settings.Placement == MeterPlacement.Docked;

        DockPlacement frame = TaskbarDock.Frame();
        if (docked && frame.Host == 0)
        {
            LogNoTaskbar();
            return false;
        }

        _dark = dark;
        _placement = settings.Placement;

        _meter = new TaskbarMeterViewModel(dark);
        _meter.Apply(settings);
        _meter.Resized += OnResized;

        _window = new TaskbarMeterWindow(_meter);
        _window.Chosen += OnChosen;
        _window.Show();

        _host = 0;

        if (docked && !TaskbarDock.Attach(_window.Handle, frame.Host))
        {
            LogDockFailed();
            Teardown();
            return false;
        }

        _host = docked ? frame.Host : 0;
        TaskbarDock.Aside(_window.Handle, topmost: !docked);

        _patrol.Start();

        return true;
    }

    private void Teardown()
    {
        Listen(null);
        _patrol.Stop();

        if (_meter is not null)
        {
            _meter.Resized -= OnResized;
            _meter = null;
        }

        if (_window is not null)
        {
            _window.Chosen -= OnChosen;
            TaskbarDock.Detach(_window.Handle);
            _window.Close();
            _window = null;
        }

        _host = 0;
        _placed = default;
        _snapshot = SystemSnapshot.Empty;
    }

    private void Listen(TaskbarSettings? settings)
    {
        bool wanted = settings is not null;

        if (wanted != _listening)
        {
            _listening = wanted;

            if (wanted)
            {
                _network.SampleTaken += OnNetworkSample;
                _download = _network.Latest.DownloadBytesPerSecond;
                _upload = _network.Latest.UploadBytesPerSecond;
            }
            else
            {
                _network.SampleTaken -= OnNetworkSample;
            }
        }

        bool machine = settings is not null && NeedsMachine(settings);

        if (machine == (_watch is not null))
        {
            return;
        }

        if (machine)
        {
            _machine.Sampled += OnMachineSample;
            _watch = _machine.Watch();
            _snapshot = _machine.Latest;
            return;
        }

        _machine.Sampled -= OnMachineSample;
        _watch?.Dispose();
        _watch = null;
        _snapshot = SystemSnapshot.Empty;
    }

    private static bool NeedsMachine(TaskbarSettings settings)
    {
        for (int row = 0; row < settings.Rows; row++)
        {
            for (int column = 0; column < settings.Columns; column++)
            {
                if (MeterReadout.NeedsMachine(settings.Slot(row, column)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void Station(bool force = false)
    {
        if (_window is null)
        {
            return;
        }

        TaskbarSettings settings = _settings.Taskbar;
        bool docked = settings.Placement == MeterPlacement.Docked;

        DockPlacement frame = TaskbarDock.Frame();

        if (frame.Host == 0)
        {
            Report(docked ? MeterStatus.Unavailable : MeterStatus.Floating);
            return;
        }

        if (docked)
        {
            if (_host != frame.Host || !TaskbarDock.Alive(_host))
            {
                if (!TaskbarDock.Attach(_window.Handle, frame.Host))
                {
                    Report(MeterStatus.Unavailable);
                    return;
                }

                _host = frame.Host;
                _placed = default;
                force = true;
                LogRedocked();
            }
        }

        DockPlacement wanted = TaskbarDock.Hang(frame, Across(settings, frame));

        if (!docked)
        {
            wanted = TaskbarDock.OnScreen(wanted);
        }

        bool moved = force || !Same(wanted, _placed);
        bool buried = docked && !TaskbarDock.IsInFront(_window.Handle, _host);

        if (moved || buried)
        {
            TaskbarDock.Place(_window.Handle, wanted, floating: !docked);
            _placed = wanted;
        }

        Report(docked ? MeterStatus.Docked : MeterStatus.Floating);
    }

    private int Across(TaskbarSettings settings, DockPlacement frame)
    {
        double scale = frame.Dpi / 96d;

        if (!settings.FitToContents || _window is null)
        {
            return (int)Math.Round(settings.Width * scale);
        }

        double height = frame.Height / scale;
        double wide = _window.DesiredWidth(height) + Padding;

        wide = Math.Clamp(wide, TaskbarSettings.MinimumWidth, TaskbarSettings.MaximumWidth);
        return (int)(Math.Ceiling(wide * scale / 4) * 4);
    }

    private static bool Same(DockPlacement left, DockPlacement right) =>
        left.X == right.X
        && left.Y == right.Y
        && left.Width == right.Width
        && left.Height == right.Height;

    private void OnNetworkSample(object? sender, ThroughputSample sample)
    {
        _download = sample.DownloadBytesPerSecond;
        _upload = sample.UploadBytesPerSecond;
        Queue();
    }

    private void OnMachineSample(object? sender, SystemSnapshot snapshot)
    {
        _snapshot = snapshot;
        Queue();
    }

    private void Queue()
    {
        if (_pending)
        {
            return;
        }

        _pending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Draw));
    }

    private void Draw()
    {
        _pending = false;
        _meter?.Update(new MeterReading(_download, _upload, _snapshot));
    }

    private void OnResized(object? sender, EventArgs e) => Station();

    private void OnChosen(object? sender, EventArgs e)
    {
        if (_settings.Taskbar.Click == MeterClick.Open)
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Report(MeterStatus status)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool TaskbarIsDark()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme",
                null);

            return value is not int light || light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return true;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Taskbar meter could not start: no taskbar window was found")]
    private partial void LogNoTaskbar();

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Taskbar meter could not be docked into the taskbar")]
    private partial void LogDockFailed();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Taskbar meter re-docked after the taskbar was rebuilt")]
    private partial void LogRedocked();

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Taskbar meter could not be created; carrying on without it")]
    private partial void LogBuildFailed(Exception ex);
}
