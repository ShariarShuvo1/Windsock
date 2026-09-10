using System.Runtime.InteropServices;
using System.Text;

namespace Windsock.Core.Hardware;

/// <summary>What an NVIDIA adapter says about itself.</summary>
public sealed record NvidiaReading(
    string Name,
    double? Celsius,
    double? BusyPercent,
    double? MemoryBusyPercent,
    long MemoryUsed,
    long MemoryTotal,
    double? FanPercent,
    double? Watts,
    double? Megahertz,
    double? MemoryMegahertz);

/// <summary>
/// The sensor library that ships with the NVIDIA driver.
/// </summary>
public sealed partial class Nvml : IDisposable
{
    private const int Ok = 0;
    private const int TextLength = 96;
    private readonly List<nint> _devices = [];
    private bool _closed;

    private Nvml()
    {
    }

    /// <summary>The driver version behind the library.</summary>
    public string Driver { get; private set; } = string.Empty;

    /// <summary>How many adapters it found.</summary>
    public int Count => _devices.Count;

    /// <summary>
    /// Loads the library and takes hold of every adapter it knows.
    /// </summary>
    public static Nvml? Open()
    {
        Nvml nvml = new();

        try
        {
            if (Init() != Ok)
            {
                return null;
            }
        }
        catch (DllNotFoundException)
        {
            // No NVIDIA driver on this machine.
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            // A driver too old to carry the entry points this expects.
            return null;
        }

        nvml.Fill();
        if (nvml.Count == 0)
        {
            nvml.Dispose();
            return null;
        }

        return nvml;
    }

    private void Fill()
    {
        Driver = Version();

        if (DeviceCount(out uint count) != Ok)
        {
            return;
        }

        for (uint index = 0; index < count; index++)
        {
            if (DeviceHandle(index, out nint device) == Ok)
            {
                _devices.Add(device);
            }
        }
    }

    /// <summary>Reads one adapter.</summary>
    public NvidiaReading? Read(int index)
    {
        if (_closed || index < 0 || index >= _devices.Count)
        {
            return null;
        }

        nint device = _devices[index];

        bool load = Utilisation(device, out Rates rates) == Ok;
        bool store = MemoryOf(device, out Store memory) == Ok;

        return new NvidiaReading(
            Name(device),
            Temperature(device, CoreSensor, out uint celsius) == Ok ? celsius : null,
            load ? rates.Gpu : null,
            load ? rates.Memory : null,
            store ? (long)memory.Used : 0,
            store ? (long)memory.Total : 0,
            FanSpeed(device, out uint fan) == Ok ? fan : null,
            Power(device, out uint milliwatts) == Ok ? milliwatts / 1000.0 : null,
            ClockOf(device, GraphicsClock, out uint core) == Ok ? core : null,
            ClockOf(device, MemoryClock, out uint bus) == Ok ? bus : null);
    }

    private static unsafe string Name(nint device)
    {
        byte* buffer = stackalloc byte[TextLength];

        return DeviceName(device, buffer, TextLength) == Ok
            ? Read(buffer)
            : string.Empty;
    }

    private static unsafe string Version()
    {
        byte* buffer = stackalloc byte[TextLength];

        return DriverVersion(buffer, TextLength) == Ok
            ? Read(buffer)
            : string.Empty;
    }

    private static unsafe string Read(byte* buffer)
    {
        int length = 0;

        while (length < TextLength && buffer[length] != 0)
        {
            length++;
        }

        return Encoding.ASCII.GetString(buffer, length);
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _devices.Clear();

        try
        {
            _ = Shutdown();
        }
        catch (DllNotFoundException)
        {
        }
    }

    private const int GraphicsClock = 0;
    private const int MemoryClock = 2;
    private const int CoreSensor = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rates
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Store
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [LibraryImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static partial int Init();

    [LibraryImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    private static partial int Shutdown();

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
    private static partial int DeviceCount(out uint count);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static partial int DeviceHandle(uint index, out nint device);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")]
    private static unsafe partial int DeviceName(nint device, byte* name, uint length);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlSystemGetDriverVersion")]
    private static unsafe partial int DriverVersion(byte* version, uint length);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
    private static partial int Temperature(nint device, int sensor, out uint celsius);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
    private static partial int Utilisation(nint device, out Rates rates);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
    private static partial int MemoryOf(nint device, out Store memory);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed")]
    private static partial int FanSpeed(nint device, out uint percent);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
    private static partial int Power(nint device, out uint milliwatts);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")]
    private static partial int ClockOf(nint device, int kind, out uint megahertz);
}
