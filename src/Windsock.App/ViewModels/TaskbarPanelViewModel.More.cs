using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Windsock.App.Services;
using Windsock.App.Views;
using Windsock.Core.Formatting;
using Windsock.Core.Hardware;
using Windsock.Core.Meter;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>
/// The Taskbar page's newer choices: one-click layouts and layout files, what
/// each kind of click does, keyboard shortcuts, the tray icon's figures, the
/// compact window and the meter's feeds.
/// </summary>
public sealed partial class TaskbarPanelViewModel
{
    private SystemMonitor? _system;
    private HotkeyService? _hotkeys;
    private bool _mirroring;

    /// <summary>Whether this page is applying a change of its own, which it need not read back.</summary>
    private bool _changing;

    /// <summary>The places the board was last built from, to tell when they were replaced off the page.</summary>
    private List<MeterSlot>? _builtFrom;

    /// <summary>The layouts on offer with one click, the one in use marked.</summary>
    public IReadOnlyList<PresetChoice> Presets { get; private set; } = [];

    /// <summary>What the last save or load of a layout file did, or why it did not.</summary>
    [ObservableProperty]
    public partial string LayoutNote { get; private set; } = string.Empty;

    /// <summary>Every action a click can be given, by name.</summary>
    public IReadOnlyList<MeterOption<MeterClick>> ClickActions { get; } =
    [
        new(MeterClick.Open, "Open Windsock"),
        new(MeterClick.OpenApps, "Open the Apps page"),
        new(MeterClick.OpenHistory, "Open History"),
        new(MeterClick.OpenConnection, "Open Connection"),
        new(MeterClick.OpenTaskbar, "Open this page"),
        new(MeterClick.Mini, "Open or close the compact window"),
        new(MeterClick.CopySpeeds, "Copy the speeds"),
        new(MeterClick.PauseRecording, "Pause or resume recording"),
        new(MeterClick.HideMeter, "Hide the meter"),
        new(MeterClick.Nothing, "Nothing"),
    ];

    /// <summary>What a double-click on the meter does.</summary>
    [ObservableProperty]
    public partial MeterClick DoubleClick { get; set; }

    /// <summary>What a middle-click on the meter does.</summary>
    [ObservableProperty]
    public partial MeterClick MiddleClick { get; set; }

    /// <summary>Whether the tray icon carries the rates.</summary>
    [ObservableProperty]
    public partial bool TrayFigures { get; set; }

    /// <summary>Whether places on the meter may read web and local feeds.</summary>
    [ObservableProperty]
    public partial bool Feeds { get; set; }

    /// <summary>The shortcut that brings the window to the front.</summary>
    [ObservableProperty]
    public partial string ShowWindowKeys { get; set; } = string.Empty;

    /// <summary>The shortcut that takes the meter off the taskbar or puts it back.</summary>
    [ObservableProperty]
    public partial string ToggleMeterKeys { get; set; } = string.Empty;

    /// <summary>The shortcut that pauses the network chart.</summary>
    [ObservableProperty]
    public partial string PauseChartKeys { get; set; } = string.Empty;

    /// <summary>Why the window shortcut is not working, or nothing.</summary>
    [ObservableProperty]
    public partial string ShowWindowProblem { get; private set; } = string.Empty;

    /// <summary>Why the meter shortcut is not working, or nothing.</summary>
    [ObservableProperty]
    public partial string ToggleMeterProblem { get; private set; } = string.Empty;

    /// <summary>Why the chart shortcut is not working, or nothing.</summary>
    [ObservableProperty]
    public partial string PauseChartProblem { get; private set; } = string.Empty;

    /// <summary>Takes the newer choices from the settings and starts following the shortcuts.</summary>
    private void ReadMore(SystemMonitor system, HotkeyService hotkeys)
    {
        _system = system;
        _hotkeys = hotkeys;

        TaskbarSettings meter = _settings.Taskbar;

        _mirroring = true;

        try
        {
            DoubleClick = meter.DoubleClick;
            MiddleClick = meter.MiddleClick;
            TrayFigures = meter.TrayFigures;
        }
        finally
        {
            _mirroring = false;
        }

        ReadKeys();

        Presets = PresetChoice.For(system.Machine.HasBattery, meter);
        hotkeys.Changed += OnHotkeysChanged;
        _meter.Applied += OnApplied;
        ReadProblems();
    }

    /// <summary>Takes the shortcuts and the feeds switch from the settings, saving nothing.</summary>
    private void ReadKeys()
    {
        TaskbarSettings meter = _settings.Taskbar;

        bool was = _mirroring;

        _mirroring = true;

        try
        {
            Feeds = meter.Feeds;
            ShowWindowKeys = meter.Shortcuts.ShowWindow ?? string.Empty;
            ToggleMeterKeys = meter.Shortcuts.ToggleMeter ?? string.Empty;
            PauseChartKeys = meter.Shortcuts.PauseChart ?? string.Empty;
        }
        finally
        {
            _mirroring = was;
        }
    }

    private void ForgetMore()
    {
        if (_hotkeys is not null)
        {
            _hotkeys.Changed -= OnHotkeysChanged;
        }

        _meter.Applied -= OnApplied;
    }

    /// <summary>Whether a reading can be put on this machine's meter at all.</summary>
    private bool Offered(MeterField field)
    {
        if (!MeterSlotChoiceViewModel.IsOffered(field))
        {
            return false;
        }

        MachineFacts? machine = _system?.Machine;

        return field switch
        {
            MeterField.BatteryCharge or MeterField.BatteryTime => machine?.HasBattery == true,
            MeterField.NpuLoad => machine?.Npus.Count > 0,
            MeterField.ProcessorPower => _system?.ReadsProcessorPower == true,
            _ => true,
        };
    }

    /// <summary>The graphics cards by name, as the readings call them.</summary>
    private IReadOnlyList<string> CardNames()
    {
        if (_system is null)
        {
            return [];
        }

        SystemSnapshot latest = _system.Latest;

        return latest.HasValue && latest.Graphics.Count > 0
            ? [.. latest.Graphics.Select(card => card.Name).Distinct(StringComparer.Ordinal)]
            : [.. _system.Machine.Graphics.Select(card => card.Name).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Lays the meter out as a preset does.</summary>
    [RelayCommand]
    private void ChoosePreset(PresetChoice? choice)
    {
        if (choice is null)
        {
            return;
        }

        Change(choice.Preset.ApplyTo);
        BuildSlots();
        LayoutNote = string.Empty;
    }

    /// <summary>Writes the meter's layout and look to a small file to keep or share.</summary>
    [RelayCommand]
    private async Task SaveLayoutAsync()
    {
        SaveFileDialog dialog = new()
        {
            Title = "Save the meter's layout",
            Filter = "Windsock meter layout (*" + MeterLayoutFile.Extension + ")|*" + MeterLayoutFile.Extension + "|All files (*.*)|*.*",
            DefaultExt = MeterLayoutFile.Extension,
            FileName = "My meter" + MeterLayoutFile.Extension,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, MeterLayoutFile.Write(_settings.Taskbar), new UTF8Encoding(false)).ConfigureAwait(true);
            LayoutNote = string.Concat("Saved to ", Path.GetFileName(dialog.FileName), ". It holds what each place shows and how the meter looks, nothing else.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LayoutNote = "Could not write that file: " + error.Message;
        }
    }

    /// <summary>
    /// Reads a layout file and puts it on the meter. A file that is not one,
    /// or holds anything Windsock cannot use, changes nothing; one that reads
    /// from addresses names them, and asks first while feeds are allowed.
    /// </summary>
    [RelayCommand]
    private async Task LoadLayoutAsync()
    {
        OpenFileDialog dialog = new()
        {
            Title = "Load a meter layout",
            Filter = "Windsock meter layout (*" + MeterLayoutFile.Extension + ")|*" + MeterLayoutFile.Extension + "|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string text;

        try
        {
            FileInfo file = new(dialog.FileName);

            if (file.Length > MeterLayoutFile.Largest)
            {
                LayoutNote = "That file is far too large to be a meter layout. Nothing was changed.";
                return;
            }

            text = await File.ReadAllTextAsync(file.FullName).ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LayoutNote = "Could not read that file: " + error.Message;
            return;
        }

        LayoutRead read = MeterLayoutFile.Read(text);

        if (read.Layout is not { } layout)
        {
            LayoutNote = string.Concat(read.Problem ?? "That is not a meter layout.", " Nothing was changed.");
            return;
        }

        // A layout that reads from addresses would start contacting them at
        // once while feeds are allowed; somebody else's file asks first.
        if (read.Addresses.Count > 0 && _settings.Taskbar.Feeds
            && !InsightDialog.Ask(
                Application.Current?.MainWindow,
                "Load a meter layout",
                "This layout reads from addresses",
                "Its places ask these addresses for a value, every minute or so, as soon as it is loaded. Load it only if you trust them.",
                read.Addresses,
                "Load the layout"))
        {
            return;
        }

        // Made on another PC or in the other edition, a layout can name
        // readings this one cannot give; those places are left empty.
        (int emptied, IReadOnlyList<MeterField> missing) = MeterLayoutFile.Fit(layout, Offered);

        Change(meter => MeterLayoutFile.Apply(layout, meter));
        ReadLook();
        BuildSlots();

        string loaded = string.Concat("Loaded ", Path.GetFileName(dialog.FileName), ".");
        string fitted = Unoffered(emptied, missing);
        string feeds = read.Addresses.Count == 0
            ? string.Empty
            : _settings.Taskbar.Feeds
                ? string.Concat(" It reads from ", string.Join(", ", read.Addresses), ".")
                : string.Concat(" It reads from ", string.Join(", ", read.Addresses), ", which stays switched off until you allow web and local feeds.");

        LayoutNote = string.Concat(loaded, fitted, feeds);
    }

    /// <summary>
    /// What fitting a layout to this PC left out, in a sentence: "2 places
    /// were left empty: this PC has no battery." Nothing when nothing was.
    /// </summary>
    private static string Unoffered(int emptied, IReadOnlyList<MeterField> missing)
    {
        if (missing.Count == 0)
        {
            return string.Empty;
        }

        List<string> reasons = [];

        foreach (MeterField field in missing)
        {
            string reason = field switch
            {
                MeterField.BatteryCharge or MeterField.BatteryTime => "this PC has no battery",
                MeterField.NpuLoad => "this PC has no NPU",
#if STORE
                MeterField.ProcessorTemperature or MeterField.ProcessorPower => "this edition cannot read the processor's temperature or power",
#else
                MeterField.ProcessorPower => "the processor's power is not being read here",
#endif
                _ => string.Concat(MeterReadout.Name(field), " is not offered here"),
            };

            if (!reasons.Contains(reason))
            {
                reasons.Add(reason);
            }
        }

        string why = string.Join("; ", reasons);

        return emptied switch
        {
            0 => string.Concat(" Some readings it takes turns with were left out: ", why, "."),
            1 => string.Concat(" 1 place was left empty: ", why, "."),
            _ => string.Concat(" ", emptied.ToString(System.Globalization.CultureInfo.CurrentCulture), " places were left empty: ", why, "."),
        };
    }

    /// <summary>Opens the compact window, or closes it.</summary>
    [RelayCommand]
    private void ToggleMini() => _meter.Request(MeterClick.Mini);

    /// <summary>Reads back the look a layout file just set.</summary>
    private void ReadLook()
    {
        TaskbarSettings meter = _settings.Taskbar;
        bool was = _mirroring;

        _mirroring = true;

        try
        {
            FitToContents = meter.FitToContents;
            SteadyWidth = meter.SteadyWidth;
            Width = meter.Width;
            Corner = meter.Corner;
            Shadow = meter.Shadow;
        }
        finally
        {
            _mirroring = was;
        }

        Ground.Told();
    }

    partial void OnDoubleClickChanged(MeterClick value) => Change(meter => meter.DoubleClick = value);

    partial void OnMiddleClickChanged(MeterClick value) => Change(meter => meter.MiddleClick = value);

    partial void OnTrayFiguresChanged(bool value) => Change(meter => meter.TrayFigures = value);

    partial void OnFeedsChanged(bool value)
    {
        if (!_mirroring)
        {
            Change(meter => meter.Feeds = value);
        }

        foreach (MeterSlotChoiceViewModel slot in SlotChoices)
        {
            slot.Refresh();
        }
    }

    partial void OnShowWindowKeysChanged(string value) => Keys(shortcuts => shortcuts.ShowWindow = Tidy(value));

    partial void OnToggleMeterKeysChanged(string value) => Keys(shortcuts => shortcuts.ToggleMeter = Tidy(value));

    partial void OnPauseChartKeysChanged(string value) => Keys(shortcuts => shortcuts.PauseChart = Tidy(value));

    private void Keys(Action<MeterShortcuts> edit)
    {
        if (_mirroring)
        {
            return;
        }

        // A new object, so the shortcuts in force and the ones being set are
        // never the same instance half-changed.
        Change(meter =>
        {
            MeterShortcuts shortcuts = meter.Shortcuts.Copy();
            edit(shortcuts);
            meter.Shortcuts = shortcuts;
        });

        ReadProblems();
    }

    private static string? Tidy(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void OnHotkeysChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(new Action(ReadProblems));

    /// <summary>
    /// The meter's settings were applied from off the page - the menu's Hide
    /// the meter, a shortcut, the welcome choosing a layout - so the page reads
    /// them all back: every switch and choice, and the board itself when its
    /// places were replaced. A change the page made itself is not read back,
    /// so an open sheet is never rebuilt under the reader.
    /// </summary>
    private void OnApplied(object? sender, EventArgs e)
    {
        if (_changing || _disposed)
        {
            return;
        }

        TaskbarSettings meter = _settings.Taskbar;

        _mirroring = true;

        try
        {
            IsEnabled = meter.Enabled;
            Placement = meter.Placement;
            Click = meter.Click;
            DoubleClick = meter.DoubleClick;
            MiddleClick = meter.MiddleClick;
            TrayFigures = meter.TrayFigures;
            FullScreen = meter.FullScreen;
            Remote = meter.Remote;
            Locked = meter.Locked;
            MonitorChoice = ChoiceOf(meter);
        }
        finally
        {
            _mirroring = false;
        }

        ReadLook();
        ReadKeys();
        ReadProblems();

        if (!ReferenceEquals(_builtFrom, meter.Slots) || Rows != meter.Rows || Columns != meter.Columns)
        {
            BuildSlots();
        }
        else
        {
            PresetChoice.Mark(Presets, meter);
        }

        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(IsFloating));
        ResetAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Why each shortcut is not working: one Windows refused, or one that is not a shortcut at all.</summary>
    private void ReadProblems()
    {
        IReadOnlyDictionary<HotkeyAction, string> problems = _hotkeys?.Problems ?? new Dictionary<HotkeyAction, string>();

        ShowWindowProblem = Problem(ShowWindowKeys, HotkeyAction.ShowWindow, problems);
        ToggleMeterProblem = Problem(ToggleMeterKeys, HotkeyAction.ToggleMeter, problems);
        PauseChartProblem = Problem(PauseChartKeys, HotkeyAction.PauseChart, problems);
    }

    private static string Problem(string keys, HotkeyAction action, IReadOnlyDictionary<HotkeyAction, string> problems)
    {
        if (string.IsNullOrWhiteSpace(keys))
        {
            return string.Empty;
        }

        if (Hotkeys.Parse(keys).Problem is { } wrong)
        {
            return wrong;
        }

        return problems.TryGetValue(action, out string? refused) ? refused : string.Empty;
    }
}
