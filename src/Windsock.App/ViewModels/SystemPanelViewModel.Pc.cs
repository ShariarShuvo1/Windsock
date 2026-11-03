using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using Windsock.Core.Formatting;
using Windsock.Core.Hardware;
using Windsock.Core.History;
using Windsock.Core.Power;
using Windsock.Core.Settings;
using CorePartHistory = Windsock.Core.Hardware.PartHistory;

namespace Windsock.App.ViewModels;

/// <summary>
/// What step 8 of 0.2.0 added to the System pages: the Battery part, the power
/// mode, the busiest processes, each part's day or week, the processor's
/// utility figure, its two kinds of core and its watts, and neural processors.
/// </summary>
/// <remarks>
/// <para>
/// The busiest processes are read only while a page that shows them is on
/// screen - the Processor, Memory or Storage page, in a window that can be
/// seen - through <see cref="SystemMonitor.WatchBusiest"/>. The page being
/// built is not enough: the window keeps this view model's readings running
/// for the sidebar's figures whichever page is open.
/// </para>
/// <para>
/// A part's history is read when its page comes on screen, when the span
/// changes, and at most once a minute after that as minutes are recorded.
/// </para>
/// </remarks>
public sealed partial class SystemPanelViewModel
{
    private const double WarmBattery = 45;
    private const int HistoryLastsFor = 60_000;

    private readonly IMachineHistoryStore? _machineHistory;
    private IDisposable? _busiestWatch;
    private bool _onScreen;
    private bool _followingRecorder;
    private int _historyLoads;
    private long _historyReadAt = long.MinValue;
    private (SystemPart Part, PartSpan Span)? _historyRead;
    private IReadOnlyList<LogicalProcessor> _logical = [];
    private int _groupedCores = -1;

    /// <summary>The battery tile; its page is offered only on a machine with a battery.</summary>
    public SystemTileViewModel Battery { get; }

    /// <summary>The busiest processes for the part on screen.</summary>
    public BusiestListViewModel BusiestProcesses { get; } = new();

    /// <summary>The part on screen over its last day or week.</summary>
    public PartHistoryViewModel PartHistory { get; } = new();

    /// <summary>The Battery page's figures.</summary>
    public BatteryDetailViewModel BatteryDetail { get; } = new();

    /// <summary>One card per neural processor; none on most machines.</summary>
    public ObservableCollection<NpuCardViewModel> Npus { get; } = [];

    /// <summary>The logical processors, by the kind of core each sits on.</summary>
    public ObservableCollection<CoreGroupViewModel> CoreGroups { get; } = [];

    /// <summary>
    /// Whether the machine has a battery: said when it is identified, or by any
    /// reading since. Once true it stays so for the window's life, so the page
    /// does not come and go with a UPS cable.
    /// </summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial bool HasBattery { get; private set; }

    /// <summary>Whether the processor has performance and efficiency cores.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial bool IsHybrid { get; private set; }

    /// <summary>Windows' <c>% Processor Utility</c>, as a second figure.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string ProcessorUtility { get; private set; } = MeterReadout.Unknown;

    /// <summary>The performance cores' average load.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string PerformanceLoad { get; private set; } = MeterReadout.Unknown;

    /// <summary>How many performance cores and threads there are.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string PerformanceNote { get; private set; } = string.Empty;

    /// <summary>The efficiency cores' average load.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string EfficiencyLoad { get; private set; } = MeterReadout.Unknown;

    /// <summary>How many efficiency cores and threads there are.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string EfficiencyNote { get; private set; } = string.Empty;

    /// <summary>The processor package's power, or a dash.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string ProcessorPower { get; private set; } = string.Empty;

    /// <summary>Why there are no watts, in a few words, where there are none.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string ProcessorPowerNote { get; private set; } = string.Empty;

    /// <summary>The same at length, for the tooltip.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string ProcessorPowerHint { get; private set; } = string.Empty;

    /// <summary>The power mode, mains or battery, and saver, for the machine line.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string PowerLine { get; private set; } = string.Empty;

    /// <summary>Whether the battery's page is showing.</summary>
    public bool IsBatteryShowing => Selected.Part == SystemPart.Battery;

    /// <summary>Whether the page showing lists its busiest processes.</summary>
    public bool ShowsBusiest => Selected.Part is SystemPart.Processor or SystemPart.Memory or SystemPart.Storage;

    /// <summary>Whether the page showing has a day or week of history.</summary>
    public bool ShowsPartHistory => PartOf(Selected.Part) is not null;

    /// <summary>
    /// Whether the page is the one on screen, as its view reports: its
    /// readings run whenever the window is up, but the busiest processes and
    /// the history are for the page alone.
    /// </summary>
    public void SetOnScreen(bool visible)
    {
        if (_onScreen == visible)
        {
            return;
        }

        _onScreen = visible;
        FollowBusiest();
        FollowRecorder();
        RequestHistory(force: false);
    }

    partial void OnSelectedChanged(SystemTileViewModel value)
    {
        OnPropertyChanged(nameof(IsBatteryShowing));
        OnPropertyChanged(nameof(ShowsBusiest));
        OnPropertyChanged(nameof(ShowsPartHistory));

        BusiestProcesses.Reset(value.Part);
        PartHistory.Reset(value.Part);
        _historyRead = null;

        FollowBusiest();
        FollowRecorder();
        RequestHistory(force: true);
    }

    partial void OnHasBatteryChanged(bool value) => OnPropertyChanged(nameof(PageSubtitle));

    partial void OnPowerLineChanged(string value) => OnPropertyChanged(nameof(MachineLine));

    /// <summary>Holds the process table open while a page that lists processes is on screen.</summary>
    private void FollowBusiest()
    {
        bool wanted = !_closed && _onScreen && _watch is not null && ShowsBusiest;

        if (wanted)
        {
            _busiestWatch ??= _monitor.WatchBusiest();
            return;
        }

        _busiestWatch?.Dispose();
        _busiestWatch = null;
    }

    private void FollowRecorder()
    {
        bool wanted = !_closed && _onScreen && ShowsPartHistory;

        if (wanted == _followingRecorder)
        {
            return;
        }

        _followingRecorder = wanted;

        if (wanted)
        {
            _recorder.Recorded += OnHistoryRecorded;
        }
        else
        {
            _recorder.Recorded -= OnHistoryRecorded;
        }
    }

    private void OnHistoryRecorded(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RequestHistory(force: false)));

    private void OnSpanChanged(object? sender, EventArgs e) => RequestHistory(force: true);

    private void OnTemperatureScaleChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (!_closed)
            {
                PartHistory.Restate(_recorder.IsRecording);
            }
        });

    private static MachinePart? PartOf(SystemPart part) => part switch
    {
        SystemPart.Processor => MachinePart.Processor,
        SystemPart.Graphics => MachinePart.Graphics,
        SystemPart.Memory => MachinePart.Memory,
        SystemPart.Storage => MachinePart.Storage,
        _ => null,
    };

    /// <summary>Reads the part's history, unless what is showing is fresh.</summary>
    private void RequestHistory(bool force)
    {
        if (_closed || !_onScreen || _machineHistory is null || PartOf(Selected.Part) is not { } part)
        {
            return;
        }

        (SystemPart, PartSpan) wanted = (Selected.Part, PartHistory.SelectedSpan);
        bool fresh = _historyRead == wanted && Environment.TickCount64 - _historyReadAt < HistoryLastsFor;

        if (fresh && !force)
        {
            return;
        }

        _historyRead = wanted;
        _historyReadAt = Environment.TickCount64;
        _ = ReadHistoryAsync(part, wanted.Item2);
    }

    private async Task ReadHistoryAsync(MachinePart part, PartSpan span)
    {
        int token = ++_historyLoads;
        IMachineHistoryStore store = _machineHistory!;

        try
        {
            PartSeries series = await Task.Run(() => CorePartHistory.Read(store, part, span, DateTimeOffset.Now)).ConfigureAwait(true);

            if (token == _historyLoads && !_closed)
            {
                PartHistory.Show(series, _recorder.IsRecording);
            }
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            if (token == _historyLoads && !_closed)
            {
                PartHistory.Fail();
            }
        }
    }

    /// <summary>Sets up what this part of the view model needs, from the constructor.</summary>
    private void StartPc(WindsockSettings settings)
    {
        TemperatureScale.Follow(settings);
        TemperatureScale.Changed += OnTemperatureScaleChanged;
        PartHistory.SpanChanged += OnSpanChanged;
        BusiestProcesses.Reset(Selected.Part);
        PartHistory.Reset(Selected.Part);
    }

    /// <summary>Takes what identification says about the battery, the cores and neural processors.</summary>
    private void IdentifyPc(MachineFacts machine)
    {
        HasBattery |= machine.HasBattery;
        _logical = machine.Processor.Logical;
        IsHybrid = machine.Processor.IsHybrid && _logical.Any(processor => processor.Kind != CoreKind.Uniform);
        _groupedCores = -1;

        PerformanceNote = Count(CoreKind.Performance);
        EfficiencyNote = Count(CoreKind.Efficiency);

        Npus.Clear();

        foreach (NpuFacts npu in machine.Npus)
        {
            Npus.Add(new NpuCardViewModel(npu.Luid, npu.Name));
        }
    }

    private string Count(CoreKind kind)
    {
        int threads = _logical.Count(processor => processor.Kind == kind);
        int cores = _logical.Where(processor => processor.Kind == kind).Select(processor => (processor.Group, processor.Core)).Distinct().Count();

        return threads == 0
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{cores} core{(cores == 1 ? string.Empty : "s")} · {threads} thread{(threads == 1 ? string.Empty : "s")}");
    }

    /// <summary>Takes a reading's step-8 parts.</summary>
    private void ApplyPc(SystemSnapshot snapshot)
    {
        ApplyProcessorExtras(snapshot.Processor);
        ApplyBattery(snapshot.Battery, snapshot.Power);
        ApplyNpus(snapshot.Npus);

        PowerLine = Describe(snapshot.Power);

        if (_busiestWatch is not null && ShowsBusiest)
        {
            BusiestProcesses.Show(Selected.Part, snapshot.Busiest, snapshot.Memory.Total);
        }
    }

    private void ApplyProcessorExtras(ProcessorReading reading)
    {
        ProcessorUtility = reading.UtilityPercent is { } utility
            ? string.Create(CultureInfo.InvariantCulture, $"{utility:F0}%")
            : MeterReadout.Unknown;

        PerformanceLoad = reading.PerformancePercent is { } fast
            ? string.Create(CultureInfo.InvariantCulture, $"{fast:F0}%")
            : MeterReadout.Unknown;

        EfficiencyLoad = reading.EfficiencyPercent is { } slow
            ? string.Create(CultureInfo.InvariantCulture, $"{slow:F0}%")
            : MeterReadout.Unknown;

        GroupCores();

        foreach (CoreGroupViewModel group in CoreGroups)
        {
            if (group.Kind == CoreKind.Uniform)
            {
                continue;
            }

            string load = group.Kind == CoreKind.Performance ? PerformanceLoad : EfficiencyLoad;
            string count = group.Kind == CoreKind.Performance ? PerformanceNote : EfficiencyNote;
            group.Note = string.Concat(count, " · ", load, " on average");
        }

#if !STORE
        (PawnIoState state, string? detail, string? explanation) = _monitor.CpuTemperature;

        if (reading.Watts is { } watts)
        {
            ProcessorPower = BatteryText.Watts(watts);
            ProcessorPowerNote = string.Empty;
            ProcessorPowerHint = string.Empty;
        }
        else
        {
            ProcessorPower = MeterReadout.Unknown;
            (ProcessorPowerNote, ProcessorPowerHint) = state is not PawnIoState.Running
                ? (detail ?? string.Empty, explanation ?? string.Empty)
                : _monitor.ReadsProcessorPower
                    ? ("Measuring", "The power is the energy counter's change between two readings, so it arrives with the second.")
                    : ("Not reported by this processor", "PawnIO is running, but the processor's energy counter did not answer.");
        }
#endif
    }

    /// <summary>
    /// Puts each logical processor's bar under its kind of core, once the bars
    /// exist and whenever their number changes. The bars are the same rows
    /// <see cref="Cores"/> holds, so they fill as that does.
    /// </summary>
    private void GroupCores()
    {
        if (_groupedCores == Cores.Count)
        {
            return;
        }

        _groupedCores = Cores.Count;
        CoreGroups.Clear();

        bool split = IsHybrid && _logical.Count == Cores.Count;

        if (!split)
        {
            CoreGroupViewModel all = new(CoreKind.Uniform);

            foreach (MeterRowViewModel row in Cores)
            {
                all.Rows.Add(row);
            }

            CoreGroups.Add(all);
            return;
        }

        CoreGroupViewModel performance = new(CoreKind.Performance);
        CoreGroupViewModel efficiency = new(CoreKind.Efficiency);

        for (int index = 0; index < Cores.Count; index++)
        {
            (_logical[index].Kind == CoreKind.Efficiency ? efficiency : performance).Rows.Add(Cores[index]);
        }

        CoreGroups.Add(performance);
        CoreGroups.Add(efficiency);
    }

    private void ApplyBattery(BatteryReading? battery, PowerReading power)
    {
        BatteryDetail.Update(battery, power);

        if (battery is null)
        {
            return;
        }

        if (!HasBattery)
        {
            HasBattery = true;
        }

        // A charge nobody could read is a dash and a gap in the line, never 0%.
        if (battery.ChargePercent is { } charge)
        {
            Battery.Show(charge);
        }
        else
        {
            Battery.ShowNothing();
        }

        Battery.Detail = BatteryText.State(battery);
        Battery.Warmth(battery.Units.Count > 0 ? battery.Units.Max(unit => unit.Celsius) : null, WarmBattery);

        Battery.Fact(0, battery.RateWatts is { } watts ? BatteryText.Rate(watts) : BatteryText.State(battery));
        Battery.Fact(1, BatteryText.Time(battery));
        Battery.Fact(2, battery.HealthPercent is { } health
            ? string.Create(CultureInfo.InvariantCulture, $"{health:F0}% of new")
            : MeterReadout.Unknown);

        if (IsBatteryShowing)
        {
            OnPropertyChanged(nameof(PageSubtitle));
        }
    }

    private void ApplyNpus(IReadOnlyList<NpuReading> readings)
    {
        for (int index = 0; index < readings.Count && index < Npus.Count; index++)
        {
            NpuReading reading = readings[index];
            NpuCardViewModel card = Npus[index];

            card.Busy = string.Create(CultureInfo.InvariantCulture, $"{reading.BusyPercent:F0}%");
            card.BusyFraction = Math.Clamp(reading.BusyPercent / 100, 0, 1);

            card.Memory = (reading.DedicatedBytes, reading.SharedBytes) switch
            {
                ({ } own, { } shared) => string.Concat(SizeFormatter.Format(own), " own · ", SizeFormatter.Format(shared), " shared"),
                (null, { } shared) => string.Concat(SizeFormatter.Format(shared), " of system memory"),
                ({ } own, null) => SizeFormatter.Format(own),
                _ => MeterReadout.Unknown,
            };

            Engines(card.Engines, reading.Engines);
            Users(card.Processes, reading.Processes);
        }
    }

    private static void Engines(ObservableCollection<MeterRowViewModel> rows, IReadOnlyList<EngineLoad> engines)
    {
        Merge(rows, engines, static row => row.Key, static engine => engine.Kind, static engine => new MeterRowViewModel(engine.Kind, engine.Kind));

        for (int index = 0; index < engines.Count && index < rows.Count; index++)
        {
            rows[index].Show(engines[index].Percent);
        }
    }

    private void Users(ObservableCollection<MeterRowViewModel> rows, IReadOnlyList<GraphicsProcess> processes)
    {
        Merge(
            rows,
            processes,
            static row => row.Key,
            static process => process.ProcessId.ToString(CultureInfo.InvariantCulture),
            process => new MeterRowViewModel(process.ProcessId.ToString(CultureInfo.InvariantCulture), Named(process.ProcessId)));

        for (int index = 0; index < processes.Count && index < rows.Count; index++)
        {
            rows[index].Show(processes[index].Percent);
        }
    }

    /// <summary>The power mode, and battery or saver where either applies, for the machine line.</summary>
    private static string Describe(PowerReading power)
    {
        string mode = BatteryText.Mode(power);

        return power.Source == PowerSource.Battery
            ? mode.Length > 0 ? string.Concat(mode, ", on battery") : "On battery"
            : mode;
    }

    /// <summary>Lets go of what this part holds, from Dispose.</summary>
    private void StopPc()
    {
        TemperatureScale.Changed -= OnTemperatureScaleChanged;
        PartHistory.SpanChanged -= OnSpanChanged;
        _busiestWatch?.Dispose();
        _busiestWatch = null;

        if (_followingRecorder)
        {
            _recorder.Recorded -= OnHistoryRecorded;
            _followingRecorder = false;
        }
    }
}
