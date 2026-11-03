using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.App.Services;
using Windsock.Core.Docking;
using Windsock.Core.Hardware;
using Windsock.Core.Settings;
using Windsock.Core.Startup;

namespace Windsock.App.ViewModels;

/// <summary>
/// The first-run welcome: put the meter on the taskbar, pick a layout, and
/// say whether Windsock starts with Windows. Three steps, each skippable.
/// </summary>
/// <remarks>
/// Every choice takes effect as it is made, so the meter appears on the bar
/// while the welcome is still open and a layout is seen rather than imagined.
/// Nothing chosen here is anywhere but where the Taskbar page and Settings
/// already keep it, so each stays changeable later.
/// </remarks>
public sealed partial class WelcomeViewModel : ObservableObject, IDisposable
{
    /// <summary>How many steps there are.</summary>
    public const int Steps = 3;

    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _store;
    private readonly TaskbarMeter _meter;
    private readonly IStartupSwitch _startup;
    private readonly Dispatcher _dispatcher;
    private bool _mirroring;
    private bool _disposed;

    public WelcomeViewModel(
        WindsockSettings settings,
        ISettingsStore store,
        TaskbarMeter meter,
        IStartupSwitch startup,
        SystemMonitor system,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(system);

        _settings = settings;
        _store = store;
        _meter = meter;
        _startup = startup;
        _dispatcher = dispatcher;

        _mirroring = true;
        MeterOn = settings.Taskbar.Enabled;
        StartWithWindows = startup.State == StartupState.On;
        _mirroring = false;

        StartupNote = startup.Detail;
        Presets = PresetChoice.For(system.Machine.HasBattery, settings.Taskbar);
        (MeterStatus, _) = MeterWords.Describe(meter.Situation);

        _meter.StatusChanged += OnStatusChanged;
        _ = ReadStartupAsync();
    }

    /// <summary>Raised when the welcome is done with, finished or skipped.</summary>
    public event EventHandler? Finished;

    /// <summary>Which step is showing, from zero.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepText), nameof(IsFirst), nameof(IsLast), nameof(NextText))]
    public partial int Step { get; private set; }

    /// <summary>Where the reader is, in words.</summary>
    public string StepText => string.Concat("Step ", (Step + 1).ToString(System.Globalization.CultureInfo.CurrentCulture), " of 3");

    /// <summary>Whether this is the first step, which has nothing to go back to.</summary>
    public bool IsFirst => Step == 0;

    /// <summary>Whether this is the last step.</summary>
    public bool IsLast => Step == Steps - 1;

    /// <summary>What the forward button says.</summary>
    public string NextText => IsLast ? "Done" : "Next";

    /// <summary>Whether the meter is on the taskbar.</summary>
    [ObservableProperty]
    public partial bool MeterOn { get; set; }

    /// <summary>Where the meter got to, in words.</summary>
    [ObservableProperty]
    public partial string MeterStatus { get; private set; } = string.Empty;

    /// <summary>The layouts on offer, the one in use marked.</summary>
    public IReadOnlyList<PresetChoice> Presets { get; }

    /// <summary>Whether Windows starts Windsock at sign-in.</summary>
    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    /// <summary>What Windows said about starting at sign-in, when there is something to say.</summary>
    [ObservableProperty]
    public partial string StartupNote { get; private set; } = string.Empty;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _meter.StatusChanged -= OnStatusChanged;
    }

    /// <summary>On to the next step, or done after the last.</summary>
    [RelayCommand]
    private void Next()
    {
        if (IsLast)
        {
            Finish();
            return;
        }

        Step++;
    }

    /// <summary>Back a step.</summary>
    [RelayCommand]
    private void Back()
    {
        if (Step > 0)
        {
            Step--;
        }
    }

    /// <summary>Leaves the welcome where it is; everything chosen so far stays.</summary>
    [RelayCommand]
    private void Skip() => Finish();

    /// <summary>Lays the meter out as a preset does, at once.</summary>
    [RelayCommand]
    private void Choose(PresetChoice? choice)
    {
        if (choice is null)
        {
            return;
        }

        choice.Preset.ApplyTo(_settings.Taskbar);

        // Choosing a layout is asking to see it.
        if (!MeterOn)
        {
            MeterOn = true;
        }

        _meter.Apply();
        _store.Save(_settings);
        PresetChoice.Mark(Presets, _settings.Taskbar);
    }

    partial void OnMeterOnChanged(bool value)
    {
        if (_mirroring)
        {
            return;
        }

        _settings.Taskbar.Enabled = value;
        _meter.Apply();
        _store.Save(_settings);
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_mirroring)
        {
            return;
        }

        _ = WriteStartupAsync(value);
    }

    private void Finish()
    {
        _settings.Taskbar.Welcomed = true;
        _store.Save(_settings);
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void OnStatusChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_disposed)
            {
                (MeterStatus, _) = MeterWords.Describe(_meter.Situation);
            }
        }));

    private async Task WriteStartupAsync(bool wanted) => Show(await _startup.SetAsync(wanted).ConfigureAwait(true));

    private async Task ReadStartupAsync() => Show(await _startup.RefreshAsync().ConfigureAwait(true));

    /// <summary>Shows what Windows actually decided, which a person's own Task Manager choice may override.</summary>
    private void Show(StartupState state)
    {
        StartupNote = _startup.Detail;

        _mirroring = true;
        StartWithWindows = state == StartupState.On;
        _mirroring = false;
    }
}
