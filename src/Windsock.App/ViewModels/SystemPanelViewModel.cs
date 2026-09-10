using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Formatting;
using Windsock.Core.Hardware;

namespace Windsock.App.ViewModels;

/// <summary>
/// Backs the System tab: what the machine is, and what it is doing.
/// </summary>
public sealed partial class SystemPanelViewModel : ObservableObject, IDisposable
{
    private const double WarmGraphics = 75;
    private const double WarmDrive = 55;
    private const double WarmProcessor = 85;
    private const double WornDrive = 70;
    private const int NamesLastFor = 60;
    private readonly SystemMonitor _monitor;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, string> _names = [];
    private readonly Dictionary<string, string> _drivers = new(StringComparer.Ordinal);

    private IDisposable? _watch;
    private int _sinceNamed;
    private bool _identified;
    private bool _closed;

    public SystemPanelViewModel(SystemMonitor monitor, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        _monitor = monitor;
        _dispatcher = dispatcher;

        Processor = new SystemTileViewModel(SystemPart.Processor, "PROCESSOR");
        Graphics = new SystemTileViewModel(SystemPart.Graphics, "GRAPHICS");
        Memory = new SystemTileViewModel(SystemPart.Memory, "MEMORY");
        Storage = new SystemTileViewModel(SystemPart.Storage, "STORAGE");

        Tiles = [Processor, Graphics, Memory, Storage];
        Selected = Processor;
        Processor.IsSelected = true;
        foreach (SystemTileViewModel tile in Tiles)
        {
            tile.PropertyChanged += OnTileChanged;
        }

        _monitor.Sampled += OnSampled;
        _monitor.Identified += OnIdentified;
    }

    /// <summary>The four tiles, in the order they are shown.</summary>
    public IReadOnlyList<SystemTileViewModel> Tiles { get; }

    /// <summary>The processor tile.</summary>
    public SystemTileViewModel Processor { get; }

    /// <summary>The graphics tile.</summary>
    public SystemTileViewModel Graphics { get; }

    /// <summary>The memory tile.</summary>
    public SystemTileViewModel Memory { get; }

    /// <summary>The storage tile.</summary>
    public SystemTileViewModel Storage { get; }

    /// <summary>Which tile's detail is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProcessorShowing))]
    [NotifyPropertyChangedFor(nameof(IsGraphicsShowing))]
    [NotifyPropertyChangedFor(nameof(IsMemoryShowing))]
    [NotifyPropertyChangedFor(nameof(IsStorageShowing))]
    public partial SystemTileViewModel Selected { get; set; }

    /// <summary>Whether the processor's detail is the one showing.</summary>
    public bool IsProcessorShowing => Selected.Part == SystemPart.Processor;

    /// <summary>Whether the graphics detail is the one showing.</summary>
    public bool IsGraphicsShowing => Selected.Part == SystemPart.Graphics;

    /// <summary>Whether the memory detail is the one showing.</summary>
    public bool IsMemoryShowing => Selected.Part == SystemPart.Memory;

    /// <summary>Whether the storage detail is the one showing.</summary>
    public bool IsStorageShowing => Selected.Part == SystemPart.Storage;

    /// <summary>Whether a first reading has arrived.</summary>
    [ObservableProperty]
    public partial bool HasReading { get; set; }

    /// <summary>This machine's name.</summary>
    [ObservableProperty]
    public partial string MachineName { get; set; } = Environment.MachineName;

    /// <summary>The Windows it is running.</summary>
    [ObservableProperty]
    public partial string Windows { get; set; } = string.Empty;

    /// <summary>Its motherboard and firmware.</summary>
    [ObservableProperty]
    public partial string Board { get; set; } = string.Empty;

    /// <summary>How long it has been up.</summary>
    [ObservableProperty]
    public partial string Uptime { get; set; } = string.Empty;

    /// <summary>What the processor is.</summary>
    [ObservableProperty]
    public partial string ProcessorName { get; set; } = string.Empty;

    /// <summary>Its cores, threads and the split between them.</summary>
    [ObservableProperty]
    public partial string ProcessorCores { get; set; } = string.Empty;

    /// <summary>Its socket, clock and caches.</summary>
    [ObservableProperty]
    public partial string ProcessorSilicon { get; set; } = string.Empty;

    /// <summary>What it is clocked at now.</summary>
    [ObservableProperty]
    public partial string ProcessorClock { get; set; } = string.Empty;

    /// <summary>The share of its time spent in the kernel.</summary>
    [ObservableProperty]
    public partial string ProcessorKernel { get; set; } = string.Empty;

    /// <summary>How many processes are running.</summary>
    [ObservableProperty]
    public partial string ProcessorProcesses { get; set; } = string.Empty;

    /// <summary>How many threads and handles they hold between them.</summary>
    [ObservableProperty]
    public partial string ProcessorThreads { get; set; } = string.Empty;

    /// <summary>
    /// What the chip says it is, or a dash where it will not say.
    /// </summary>
    [ObservableProperty]
    public partial string ProcessorTemperature { get; set; } = string.Empty;

    /// <summary>Why there is no temperature, in a few words, where there is none.</summary>
    [ObservableProperty]
    public partial string ProcessorTemperatureNote { get; set; } = string.Empty;

    /// <summary>The same reason at length, for the tooltip.</summary>
    [ObservableProperty]
    public partial string ProcessorTemperatureHint { get; set; } = string.Empty;

    /// <summary>Physical memory in use, from nothing to one.</summary>
    [ObservableProperty]
    public partial double MemoryFraction { get; set; }

    /// <summary>What is in use, and out of how much.</summary>
    [ObservableProperty]
    public partial string MemoryUsed { get; set; } = string.Empty;

    /// <summary>What is free.</summary>
    [ObservableProperty]
    public partial string MemoryAvailable { get; set; } = string.Empty;

    /// <summary>How much is promised, from nothing to one.</summary>
    [ObservableProperty]
    public partial double CommitFraction { get; set; }

    /// <summary>What is promised, and out of how much.</summary>
    [ObservableProperty]
    public partial string Committed { get; set; } = string.Empty;

    /// <summary>What is holding cached file data.</summary>
    [ObservableProperty]
    public partial string Cached { get; set; } = string.Empty;

    /// <summary>What the kernel is holding.</summary>
    [ObservableProperty]
    public partial string Pools { get; set; } = string.Empty;

    /// <summary>What is physically fitted, from the firmware.</summary>
    [ObservableProperty]
    public partial string MemoryFitted { get; set; } = string.Empty;

    /// <summary>One row per logical processor.</summary>
    public ObservableCollection<MeterRowViewModel> Cores { get; } = [];

    /// <summary>One card per display adapter.</summary>
    public ObservableCollection<GraphicsCardViewModel> Cards { get; } = [];

    /// <summary>One row per memory module fitted.</summary>
    public ObservableCollection<MemoryModuleViewModel> Modules { get; } = [];

    /// <summary>One card per drive.</summary>
    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    private void OnTileChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemTileViewModel.IsSelected)
            || sender is not SystemTileViewModel tile
            || !tile.IsSelected)
        {
            return;
        }

        Selected = tile;

        foreach (SystemTileViewModel other in Tiles)
        {
            if (other != tile)
            {
                other.IsSelected = false;
            }
        }
    }

    /// <summary>Starts asking for readings.</summary>
    public void Show() => _watch ??= _monitor.Watch();

    /// <summary>Stops asking for readings.</summary>
    public void Hide()
    {
        _watch?.Dispose();
        _watch = null;
    }

    private void OnIdentified(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(Identify);

    private void OnSampled(object? sender, SystemSnapshot snapshot) =>
        _dispatcher.BeginInvoke(() => Apply(snapshot));

    private void Identify()
    {
        if (_closed || _identified)
        {
            return;
        }

        _identified = true;

        MachineFacts machine = _monitor.Machine;

        MachineName = machine.ComputerName;
        Windows = machine.Windows;
        Board = machine.Board.Length > 0 && machine.Bios.Length > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{machine.Board} · BIOS {machine.Bios}")
            : Smbios.Join(machine.Board, machine.Bios);
        _drivers.Clear();

        foreach (GraphicsFacts card in machine.Graphics)
        {
            _drivers[card.Name] = card.Driver;
        }

        ProcessorFacts processor = machine.Processor;

        ProcessorName = processor.Name;
        ProcessorCores = Describe(processor);
        ProcessorSilicon = Silicon(processor);

        Modules.Clear();
        long fitted = 0;

        foreach (MemoryModule module in machine.Modules)
        {
            Modules.Add(new MemoryModuleViewModel(module));
            fitted += module.Bytes;
        }

        MemoryFitted = fitted > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{SizeFormatter.Format(fitted)} fitted across {Modules.Count} slot{(Modules.Count == 1 ? string.Empty : "s")}")
            : string.Empty;
    }

    private static string Describe(ProcessorFacts processor)
    {
        string counts = string.Create(
            CultureInfo.InvariantCulture,
            $"{processor.Cores} cores · {processor.Threads} threads");

        return processor.IsHybrid
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{counts} · {processor.PerformanceCores} performance + {processor.EfficiencyCores} efficiency")
            : counts;
    }

    private static string Silicon(ProcessorFacts processor)
    {
        List<string> parts = [];

        if (processor.Socket.Length > 0)
        {
            parts.Add(processor.Socket);
        }

        if (processor.BaseMegahertz > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{processor.BaseMegahertz / 1000.0:F2} GHz base"));
        }

        if (processor.LevelTwoCache > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"L2 {SizeFormatter.Format(processor.LevelTwoCache)}"));
        }

        if (processor.LevelThreeCache > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"L3 {SizeFormatter.Format(processor.LevelThreeCache)}"));
        }

        return string.Join(" · ", parts);
    }

    private void Apply(SystemSnapshot snapshot)
    {
        if (_closed)
        {
            return;
        }

        if (!_identified)
        {
            Identify();
        }

        HasReading = true;
        Uptime = DurationLabel.Format(_monitor.Machine.Uptime.TotalSeconds);

        if (++_sinceNamed >= NamesLastFor)
        {
            _sinceNamed = 0;
            _names.Clear();
        }

        ApplyProcessor(snapshot.Processor);
        ApplyGraphics(snapshot.Graphics);
        ApplyMemory(snapshot.Memory);
        ApplyStorage(snapshot.Drives);
    }

    private void ApplyProcessor(ProcessorReading reading)
    {
        Processor.Show(reading.BusyPercent);

        ProcessorClock = reading.Megahertz > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{reading.Megahertz / 1000:F2} GHz")
            : string.Empty;

        Processor.Detail = ProcessorClock.Length > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{ProcessorClock} · {reading.Threads:N0} threads")
            : string.Create(CultureInfo.InvariantCulture, $"{reading.Threads:N0} threads");

        ProcessorKernel = string.Create(CultureInfo.InvariantCulture, $"{reading.KernelPercent:F0}% in the kernel");

        ProcessorProcesses = string.Create(CultureInfo.InvariantCulture, $"{reading.Processes:N0} processes");

        ProcessorThreads = string.Create(
            CultureInfo.InvariantCulture,
            $"{reading.Threads:N0} threads · {reading.Handles:N0} handles");

#if !STORE
        ApplyProcessorTemperature(reading.Celsius);
#endif
        while (Cores.Count < reading.Cores.Count)
        {
            int index = Cores.Count;

            Cores.Add(new MeterRowViewModel(
                index.ToString(CultureInfo.InvariantCulture),
                index.ToString(CultureInfo.InvariantCulture)));
        }

        for (int index = 0; index < reading.Cores.Count && index < Cores.Count; index++)
        {
            Cores[index].Show(reading.Cores[index]);
        }
    }

#if !STORE
    private void ApplyProcessorTemperature(double? celsius)
    {
        (PawnIoState state, string? detail, string? explanation) = _monitor.CpuTemperature;
        Processor.Warmth(celsius, WarmProcessor);

        ProcessorTemperature = celsius is { } degrees
            ? SystemTileViewModel.Temperature(degrees)
            : MeterReadout.Unknown;
        ProcessorTemperatureNote = state is PawnIoState.Running ? string.Empty : detail ?? string.Empty;
        ProcessorTemperatureHint = state is PawnIoState.Running ? string.Empty : explanation ?? string.Empty;
    }
#endif

    private void ApplyGraphics(IReadOnlyList<GraphicsReading> readings)
    {
        Merge(
            Cards,
            readings,
            static card => card.Key,
            static reading => reading.Name,
            static reading => new GraphicsCardViewModel(reading.Name));

        double busiest = 0;
        double? warmest = null;
        long used = 0;
        long fitted = 0;

        for (int index = 0; index < readings.Count && index < Cards.Count; index++)
        {
            GraphicsReading reading = readings[index];
            GraphicsCardViewModel card = Cards[index];

            card.Driver = _drivers.GetValueOrDefault(reading.Name, string.Empty);
            card.Busy = string.Create(CultureInfo.InvariantCulture, $"{reading.BusyPercent:F0}%");
            card.BusyFraction = Math.Clamp(reading.BusyPercent / 100, 0, 1);
            card.Temperature = reading.Celsius is { } celsius ? SystemTileViewModel.Temperature(celsius) : string.Empty;
            card.Power = reading.Watts is { } watts ? string.Create(CultureInfo.InvariantCulture, $"{watts:F0} W") : string.Empty;
            card.Fan = reading.FanPercent is { } fan ? string.Create(CultureInfo.InvariantCulture, $"{fan:F0}%") : string.Empty;
            card.Clock = reading.Megahertz is { } clock ? string.Create(CultureInfo.InvariantCulture, $"{clock:F0} MHz") : string.Empty;
            card.MemoryClock = reading.MemoryMegahertz is { } bus ? string.Create(CultureInfo.InvariantCulture, $"{bus:F0} MHz") : string.Empty;
            card.HasSensors = reading.Celsius is not null || reading.Watts is not null;

            card.MemoryFraction = reading.MemoryTotal > 0
                ? Math.Clamp((double)reading.MemoryUsed / reading.MemoryTotal, 0, 1)
                : 0;

            card.Memory = reading.MemoryTotal > 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{SizeFormatter.Format(reading.MemoryUsed)} of {SizeFormatter.Format(reading.MemoryTotal)}")
                : SizeFormatter.Format(reading.MemoryUsed);

            Engines(card, reading.Engines);
            Busiest(card, reading.Processes);

            busiest = Math.Max(busiest, reading.BusyPercent);
            used += reading.MemoryUsed;
            fitted += reading.MemoryTotal;

            if (reading.Celsius is { } degrees)
            {
                warmest = warmest is { } known ? Math.Max(known, degrees) : degrees;
            }
        }

        Graphics.Show(busiest);
        Graphics.Warmth(warmest, WarmGraphics);

        Graphics.Detail = fitted > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{SizeFormatter.Format(used)} of {SizeFormatter.Format(fitted)} used")
            : "No adapter reporting";
    }

    private static void Engines(GraphicsCardViewModel card, IReadOnlyList<EngineLoad> engines)
    {
        Merge(
            card.Engines,
            engines,
            static row => row.Key,
            static engine => engine.Kind,
            static engine => new MeterRowViewModel(engine.Kind, engine.Kind));

        for (int index = 0; index < engines.Count && index < card.Engines.Count; index++)
        {
            card.Engines[index].Show(engines[index].Percent);
        }
    }

    private void Busiest(GraphicsCardViewModel card, IReadOnlyList<GraphicsProcess> processes)
    {
        Merge(
            card.Processes,
            processes,
            static row => row.Key,
            static process => process.ProcessId.ToString(CultureInfo.InvariantCulture),
            process => new MeterRowViewModel(
                process.ProcessId.ToString(CultureInfo.InvariantCulture),
                Named(process.ProcessId)));

        for (int index = 0; index < processes.Count && index < card.Processes.Count; index++)
        {
            card.Processes[index].Label = Named(processes[index].ProcessId);
            card.Processes[index].Show(processes[index].Percent);
        }
    }

    private string Named(int processId)
    {
        if (_names.TryGetValue(processId, out string? name))
        {
            return name;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch (ArgumentException)
        {
            // Gone between the counter reading it and this asking.
            name = string.Create(CultureInfo.InvariantCulture, $"Process {processId}");
        }
        catch (InvalidOperationException)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"Process {processId}");
        }

        _names[processId] = name;
        return name;
    }

    private void ApplyMemory(MemoryReading reading)
    {
        Memory.Show(reading.UsedPercent);
        Memory.Detail = string.Create(
            CultureInfo.InvariantCulture,
            $"{SizeFormatter.Format(reading.Used)} of {SizeFormatter.Format(reading.Total)}");

        MemoryFraction = Math.Clamp(reading.UsedPercent / 100, 0, 1);

        MemoryUsed = string.Create(
            CultureInfo.InvariantCulture,
            $"{SizeFormatter.Format(reading.Used)} of {SizeFormatter.Format(reading.Total)} usable");

        MemoryAvailable = string.Create(CultureInfo.InvariantCulture, $"{SizeFormatter.Format(reading.Available)} free");

        CommitFraction = reading.CommitLimit > 0
            ? Math.Clamp((double)reading.Committed / reading.CommitLimit, 0, 1)
            : 0;

        Committed = string.Create(
            CultureInfo.InvariantCulture,
            $"{SizeFormatter.Format(reading.Committed)} of {SizeFormatter.Format(reading.CommitLimit)}");

        Cached = SizeFormatter.Format(reading.Cached);

        Pools = string.Create(
            CultureInfo.InvariantCulture,
            $"{SizeFormatter.Format(reading.PagedPool)} paged · {SizeFormatter.Format(reading.NonPagedPool)} non-paged");
    }

    private void ApplyStorage(IReadOnlyList<DriveReading> readings)
    {
        Merge(
            Drives,
            readings,
            static row => row.Key,
            static drive => drive.Index,
            static drive => new DriveViewModel(drive.Index));

        double busiest = 0;
        double? warmest = null;
        long free = 0;
        double reads = 0;
        double writes = 0;

        for (int index = 0; index < readings.Count && index < Drives.Count; index++)
        {
            DriveReading reading = readings[index];
            DriveViewModel drive = Drives[index];

            drive.Model = reading.Model;
            drive.Description = Describe(reading);
            drive.Fraction = Math.Clamp(reading.UsedPercent / 100, 0, 1);

            drive.Capacity = string.Create(
                CultureInfo.InvariantCulture,
                $"{SizeFormatter.Format(reading.Used)} used · {SizeFormatter.Format(reading.Free)} free");

            drive.Reading = Rate(reading.ReadBytesPerSecond);
            drive.Writing = Rate(reading.WriteBytesPerSecond);
            drive.Busy = string.Create(CultureInfo.InvariantCulture, $"{reading.BusyPercent:F0}%");
            drive.BusyFraction = Math.Clamp(reading.BusyPercent / 100, 0, 1);

            drive.Temperature = reading.Celsius is { } celsius ? SystemTileViewModel.Temperature(celsius) : string.Empty;
            drive.IsHot = reading.Celsius >= WarmDrive;

            drive.Life = reading.LifeUsedPercent is { } life
                ? string.Create(CultureInfo.InvariantCulture, $"{life:F0}% life used")
                : string.Empty;

            drive.IsWorn = reading.LifeUsedPercent >= WornDrive;

            Volumes(drive, reading.Volumes);

            busiest = Math.Max(busiest, reading.BusyPercent);
            free += reading.Free;
            reads += reading.ReadBytesPerSecond;
            writes += reading.WriteBytesPerSecond;

            if (reading.Celsius is { } degrees)
            {
                warmest = warmest is { } known ? Math.Max(known, degrees) : degrees;
            }
        }

        Storage.Show(busiest);
        Storage.Warmth(warmest, WarmDrive);

        Storage.Detail = readings.Count > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{SizeFormatter.Format(free)} free · {Rate(reads + writes)}")
            : "No drives";
    }

    private static void Volumes(DriveViewModel drive, IReadOnlyList<VolumeReading> volumes)
    {
        Merge(
            drive.Volumes,
            volumes,
            static row => row.Key,
            static volume => volume.Letter,
            static volume => new VolumeViewModel(volume.Letter));

        for (int index = 0; index < volumes.Count && index < drive.Volumes.Count; index++)
        {
            drive.Volumes[index].Update(volumes[index]);
        }
    }

    private static string Describe(DriveReading drive)
    {
        List<string> parts = [drive.IsSpinning ? "Hard disk" : "Solid state"];

        if (drive.Bus.Length > 0)
        {
            parts.Add(drive.Bus);
        }

        if (drive.Size > 0)
        {
            parts.Add(SizeFormatter.Format(drive.Size));
        }

        return string.Join(" · ", parts);
    }

    private static string Rate(double bytesPerSecond) =>
        RateFormatter.Format(bytesPerSecond, RateFamily.Bytes, RateScale.Auto);

    private static void Merge<TRow, TReading, TKey>(
        ObservableCollection<TRow> rows,
        IReadOnlyList<TReading> readings,
        Func<TRow, TKey> rowKey,
        Func<TReading, TKey> readingKey,
        Func<TReading, TRow> make)
        where TRow : class
        where TKey : notnull
    {
        for (int index = 0; index < readings.Count; index++)
        {
            TKey wanted = readingKey(readings[index]);
            int found = -1;

            for (int at = index; at < rows.Count; at++)
            {
                if (EqualityComparer<TKey>.Default.Equals(rowKey(rows[at]), wanted))
                {
                    found = at;
                    break;
                }
            }

            if (found < 0)
            {
                rows.Insert(index, make(readings[index]));
                continue;
            }

            if (found != index)
            {
                rows.Move(found, index);
            }
        }

        while (rows.Count > readings.Count)
        {
            rows.RemoveAt(rows.Count - 1);
        }
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _monitor.Sampled -= OnSampled;
        _monitor.Identified -= OnIdentified;

        foreach (SystemTileViewModel tile in Tiles)
        {
            tile.PropertyChanged -= OnTileChanged;
        }

        Hide();
    }
}
