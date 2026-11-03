using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FluentIcons.Common;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Windsock.App.Services;
using Windsock.App.ViewModels;
using Windsock.App.Views;
using Windsock.Core.Formatting;
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Settings;

namespace Windsock.App;

/// <summary>
/// The meter's side of the composition root: what its clicks, its menu, the
/// tray menu and the keyboard shortcuts do, the compact window, the figures in
/// the tray icon and the first-run welcome. Kept apart from App.xaml.cs, which
/// the whole app shares.
/// </summary>
public partial class App
{
    /// <summary>The link name that opens the compact window rather than a page.</summary>
    private const string MiniLink = "mini";

    /// <summary>The pages the menus offer by name, in the sidebar's order.</summary>
    private static readonly (string Link, string Name, Symbol Icon)[] MenuPages =
    [
        ("overview", "Network overview", Symbol.DataArea),
        ("apps", "Apps", Symbol.DataUsage),
        ("status", "Connection", Symbol.HeartPulse),
        ("history", "History", Symbol.History),
        ("plan", "Data plan", Symbol.DataPie),
        ("taskbar", "Taskbar", Symbol.PanelBottom),
        ("settings", "Settings", Symbol.Settings),
    ];

    private HotkeyService? _hotkeys;
    private TrayFiguresPainter? _trayFigures;
    private MiniWindow? _mini;
    private bool _welcoming;

    private void HookMeter(TaskbarMeter meter)
    {
        meter.ActionRequested += OnMeterAction;
        meter.MenuRequested += OnMeterMenu;
        meter.Applied += OnMeterApplied;

        if (_host is null || _tray is null || _settings is null)
        {
            return;
        }

        _hotkeys = _host.Services.GetRequiredService<HotkeyService>();
        _hotkeys.Pressed += OnHotkey;

        _trayFigures = new TrayFiguresPainter(
            _tray,
            _host.Services.GetRequiredService<ThroughputMonitor>(),
            _settings,
            Dispatcher,
            OfflineLine);
    }

    /// <summary>"Internet unreachable since 21:04" while the internet is gone, else nothing.</summary>
    private string? OfflineLine() =>
        _connection is { } connection
            ? MeterReadout.Offline(new MeterReading(0, 0, Windsock.Core.Hardware.SystemSnapshot.Empty) { Connection = connection.State })
            : null;

    private void UnhookMeter(TaskbarMeter meter)
    {
        meter.ActionRequested -= OnMeterAction;
        meter.MenuRequested -= OnMeterMenu;
        meter.Applied -= OnMeterApplied;

        if (_hotkeys is not null)
        {
            _hotkeys.Pressed -= OnHotkey;
            _hotkeys = null;
        }

        _trayFigures?.Follow(false);
        _trayFigures = null;

        _mini?.Close();
        _mini = null;
    }

    /// <summary>
    /// The meter's settings were just applied: the shortcuts and the tray's
    /// figures follow them, since both are chosen on the Taskbar page.
    /// </summary>
    private void OnMeterApplied(object? sender, EventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        TaskbarSettings meter = _settings.Taskbar;

        _hotkeys?.Apply(meter.Shortcuts);
        _trayFigures?.Follow(meter.TrayFigures);

        if (_tray is null)
        {
            return;
        }

        if (meter.TrayFigures)
        {
            if (!_tray.IsShown)
            {
                _tray.Show(TrayTip());
            }

            return;
        }

        _tray.SetTooltip(TrayTip());

        // The icon is the way back in while no window is open; with the
        // window open it goes, as it always did, unless it is showing figures.
        if (_window is not null && _tray.IsShown)
        {
            _tray.Hide();
        }
    }

    /// <summary>Hides the tray icon as the window opens, unless it carries the figures.</summary>
    private void HideTray()
    {
        if (_settings?.Taskbar.TrayFigures != true)
        {
            _tray?.Hide();
        }
    }

    private void OnMeterAction(object? sender, MeterClick action) => Act(action);

    /// <summary>Does what a click on the meter, a menu item or a shortcut asks for.</summary>
    private void Act(MeterClick action)
    {
        switch (action)
        {
            case MeterClick.Open:
                Reveal();
                break;

            case MeterClick.OpenApps:
                Reveal("apps");
                break;

            case MeterClick.OpenHistory:
                Reveal("history");
                break;

            case MeterClick.OpenConnection:
                Reveal("status");
                break;

            case MeterClick.OpenTaskbar:
                Reveal("taskbar");
                break;

            case MeterClick.Mini:
                ToggleMini();
                break;

            case MeterClick.CopySpeeds:
                CopySpeeds();
                break;

            case MeterClick.PauseRecording:
                ToggleRecording();
                break;

            case MeterClick.HideMeter:
                ShowMeter(false);
                break;
        }
    }

    private void OnHotkey(object? sender, HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ShowWindow:
                Reveal();
                break;

            case HotkeyAction.ToggleMeter:
                ShowMeter(_settings?.Taskbar.Enabled != true);
                break;

            case HotkeyAction.PauseChart:
                if (_host?.Services.GetService<ThroughputOverviewViewModel>() is { } chart)
                {
                    chart.IsLive = !chart.IsLive;
                }

                break;
        }
    }

    /// <summary>Puts the meter on the taskbar or takes it off, as the page's switch does.</summary>
    private void ShowMeter(bool shown)
    {
        if (_settings is null || _meter is null || _host is null)
        {
            return;
        }

        _settings.Taskbar.Enabled = shown;
        _host.Services.GetRequiredService<ISettingsStore>().Save(_settings);
        _meter.Apply();

        // With the meter gone and no window open, the icon is the only way back.
        if (!shown && _window is null && _tray is { IsShown: false })
        {
            _tray.Show(TrayTip());
        }
    }

    /// <summary>Pauses recording history, or resumes it, as History's own button does.</summary>
    private void ToggleRecording()
    {
        if (_host is null || _settings is null)
        {
            return;
        }

        UsageRecorder recorder = _host.Services.GetRequiredService<UsageRecorder>();
        bool recording = !recorder.IsRecording;

        recorder.IsRecording = recording;
        _settings.RecordHistory = recording;
        _host.Services.GetRequiredService<ISettingsStore>().Save(_settings);
    }

    private void CopySpeeds()
    {
        if (_meter is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_meter.SpeedsText());
        }
        catch (ExternalException error)
        {
            // Another program holding the clipboard open is not worth more than a line.
            Log.Information("The speeds could not be copied: {Reason}", error.Message);
        }
    }

    // ------------------------------------------------------- the compact window

    private void ToggleMini()
    {
        if (_mini is not null)
        {
            _mini.Close();
            return;
        }

        ShowMini();
    }

    private void ShowMini()
    {
        if (_host is null || _settings is null || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (_mini is not null)
        {
            _mini.Show();
            return;
        }

        MiniViewModel model = new(
            _host.Services.GetRequiredService<ThroughputMonitor>(),
            _settings,
            Dispatcher);

        MiniWindow mini = new(model, _settings, _host.Services.GetRequiredService<ISettingsStore>());
        mini.OpenRequested += (_, _) => Reveal();
        mini.MenuRequested += (_, _) => OpenMenu(mini, fromTray: false);
        mini.Closed += OnMiniClosed;

        _mini = mini;
        mini.Show();
    }

    private void OnMiniClosed(object? sender, EventArgs e)
    {
        if (sender is MiniWindow closed)
        {
            closed.Closed -= OnMiniClosed;
        }

        _mini = null;

        // Opened on its own, by a link or a click, it may have been the only
        // thing on screen; the icon is the way back in.
        if (_window is null && !Dispatcher.HasShutdownStarted && _tray is { IsShown: false })
        {
            _tray.Show(TrayTip());
        }
    }

    // ------------------------------------------------------------------ menus

    private void OnTrayMenuRequested(object? sender, EventArgs e) => OpenMenu(null, fromTray: true);

    private void OnMeterMenu(object? sender, FrameworkElement meter) => OpenMenu(meter, fromTray: false);

    /// <summary>
    /// The one menu the tray icon, the meter and the compact window share:
    /// open the window or a page, show or hide the meter, the compact window,
    /// pause recording, copy the speeds, quit.
    /// </summary>
    private void OpenMenu(FrameworkElement? target, bool fromTray)
    {
        if (_host is null || _settings is null)
        {
            return;
        }

        ContextMenu menu = new()
        {
            // From the keyboard the pointer may be anywhere: the menu opens under the window.
            Placement = target is MiniWindow { MenuFromKeyboard: true } ? PlacementMode.Bottom : PlacementMode.MousePoint,
            PlacementTarget = target,
        };

        MenuItem open = Item("Open Windsock", Symbol.WindowApps, () => Reveal());
        open.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(open);

        MenuItem pages = Item("Open a page", Symbol.Navigation, null);

        foreach ((string link, string name, Symbol icon) in MenuPages)
        {
            pages.Items.Add(Item(name, icon, () => Reveal(link)));
        }

        menu.Items.Add(pages);
        menu.Items.Add(new Separator());

        bool shown = _settings.Taskbar.Enabled;
        bool recording = _host.Services.GetRequiredService<UsageRecorder>().IsRecording;

        menu.Items.Add(Item(shown ? "Hide the meter" : "Show the meter on the taskbar", Symbol.PanelBottom, () => ShowMeter(!shown)));
        menu.Items.Add(Item(_mini is null ? "Compact window" : "Close the compact window", Symbol.WindowNew, ToggleMini));
        menu.Items.Add(Item(recording ? "Pause recording" : "Resume recording", recording ? Symbol.Pause : Symbol.Record, ToggleRecording));
        menu.Items.Add(Item("Copy the speeds", Symbol.Copy, CopySpeeds));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit Windsock", Symbol.Power, Shutdown));

        // A menu from the notification area closes on a click elsewhere only
        // while its process is in front; the meter's and the compact window's
        // were just clicked, so they already are.
        if (fromTray)
        {
            _tray?.TakeForeground();
        }

        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Symbol icon, Action? act)
    {
        MenuItem item = new()
        {
            Header = header,
            Icon = new FluentIcons.Wpf.SymbolIcon { Symbol = icon, FontSize = 16 },
        };

        if (act is not null)
        {
            item.Click += (_, _) => act();
        }

        return item;
    }

    // --------------------------------------------------------------- welcome

    /// <summary>
    /// Once the window is up, the first time ever: the welcome that puts the
    /// meter on the taskbar. Somebody who already found the switch is not
    /// asked, and is marked as welcomed so they never are.
    /// </summary>
    private void Welcome(MainWindow window)
    {
        if (_welcoming || _host is null || _settings is null || _settings.Taskbar.Welcomed)
        {
            return;
        }

        if (_settings.Taskbar.Enabled)
        {
            _settings.Taskbar.Welcomed = true;
            _host.Services.GetRequiredService<ISettingsStore>().Save(_settings);
            return;
        }

        _welcoming = true;

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (_host is null || _settings is null || !window.IsVisible)
            {
                _welcoming = false;
                return;
            }

            WelcomeViewModel model = ActivatorUtilities.CreateInstance<WelcomeViewModel>(_host.Services, Dispatcher);
            WelcomeDialog dialog = new(model) { Owner = window };

            dialog.Closed += (_, _) =>
            {
                _welcoming = false;

                if (_window is not null)
                {
                    Reveal("taskbar");
                }
            };

            dialog.Show();
        }));
    }
}
