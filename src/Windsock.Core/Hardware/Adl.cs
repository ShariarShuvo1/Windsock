using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Windsock.Core.Hardware;

/// <summary>
/// The sensor library that ships with the AMD driver.
/// </summary>
public sealed partial class Adl : IDisposable
{
    private const string Library = "atiadlxx.dll";
    private const int Ok = 0;
    private const int AmdVendor = 0x1002;
    private const int PathLength = 256;
    private const int LogSensors = 256;
    private const int LogEdgeTemperature = 8;
    private const int LogHotspotTemperature = 27;
    private const int OverdriveEdge = 1;
    private const double Millidegrees = 0.001;
    private const double Coldest = 1;
    private const double Hottest = 150;
    private readonly List<int> _adapters = [];
    private nint _context;
    private bool _closed;

    private Adl()
    {
    }

    /// <summary>How many AMD cards it found.</summary>
    public int Count => _adapters.Count;

    /// <summary>
    /// Loads the library and finds every AMD card on the machine.
    /// </summary>
    public static Adl? Open()
    {
        Adl adl = new();

        try
        {
            unsafe
            {
                delegate* unmanaged[Cdecl]<int, nint> allocate = &Allocate;

                if (MainControlCreate((nint)allocate, 1, out adl._context) != Ok || adl._context == 0)
                {
                    return null;
                }
            }
        }
        catch (DllNotFoundException)
        {
            // No AMD driver on this machine.
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            // A driver too old to carry the entry points this expects.
            return null;
        }

        adl.Fill();

        // A library that loaded but holds nothing is no better than none.
        if (adl.Count == 0)
        {
            adl.Dispose();
            return null;
        }

        return adl;
    }

    private unsafe void Fill()
    {
        if (AdapterCount(_context, out int count) != Ok || count <= 0)
        {
            return;
        }

        int size = sizeof(AdapterInfo) * count;
        nint buffer = Marshal.AllocHGlobal(size);

        try
        {
            new Span<byte>((void*)buffer, size).Clear();

            if (AdapterInfoOf(_context, buffer, size) != Ok)
            {
                return;
            }

            AdapterInfo* adapters = (AdapterInfo*)buffer;
            HashSet<long> seen = [];

            for (int index = 0; index < count; index++)
            {
                AdapterInfo adapter = adapters[index];

                if (adapter.VendorId != AmdVendor || adapter.Exist == 0)
                {
                    continue;
                }

                long card = ((long)adapter.BusNumber << 32) | (uint)adapter.DeviceNumber;

                if (seen.Add(card))
                {
                    _adapters.Add(adapter.AdapterIndex);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The temperature of one card, or nothing where it will not say.
    /// </summary>
    public double? Read(int index)
    {
        if (_closed || index < 0 || index >= _adapters.Count)
        {
            return null;
        }

        int adapter = _adapters[index];

        return FromLog(adapter) ?? FromOverdriveN(adapter) ?? FromOverdrive5(adapter);
    }

    private double? FromLog(int adapter)
    {
        try
        {
            LogData data = default;
            data.Size = Marshal.SizeOf<LogData>();

            if (QueryLog(_context, adapter, ref data) != Ok)
            {
                return null;
            }
            return Believable(Sensor(ref data, LogEdgeTemperature))
                ?? Believable(Sensor(ref data, LogHotspotTemperature));
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static unsafe double? Sensor(ref LogData data, int index)
    {
        if (index < 0 || index >= LogSensors)
        {
            return null;
        }

        return data.Sensors[index * 2] != 0 ? data.Sensors[(index * 2) + 1] : null;
    }

    private double? FromOverdriveN(int adapter)
    {
        try
        {
            int reading = 0;

            return OverdriveNTemperature(_context, adapter, OverdriveEdge, ref reading) == Ok
                ? Believable(reading * Millidegrees)
                : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private double? FromOverdrive5(int adapter)
    {
        try
        {
            Temperature reading = new() { Size = Marshal.SizeOf<Temperature>() };

            return Overdrive5Temperature(_context, adapter, 0, ref reading) == Ok
                ? Believable(reading.Celsius * Millidegrees)
                : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static double? Believable(double? celsius) =>
        celsius is >= Coldest and <= Hottest ? celsius : null;

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _adapters.Clear();

        if (_context == 0)
        {
            return;
        }

        try
        {
            _ = MainControlDestroy(_context);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        _context = 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Allocate(int size) => Marshal.AllocHGlobal(size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Temperature
    {
        public int Size;
        public int Celsius;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct LogData
    {
        public int Size;
        public fixed int Sensors[LogSensors * 2];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AdapterInfo
    {
        public int Size;
        public int AdapterIndex;
        public fixed byte Udid[PathLength];
        public int BusNumber;
        public int DeviceNumber;
        public int FunctionNumber;
        public int VendorId;
        public fixed byte AdapterName[PathLength];
        public fixed byte DisplayName[PathLength];
        public int Present;
        public int Exist;
        public fixed byte DriverPath[PathLength];
        public fixed byte DriverPathExt[PathLength];
        public fixed byte PnpString[PathLength];
        public int OsDisplayIndex;
    }

    [LibraryImport(Library, EntryPoint = "ADL2_Main_Control_Create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int MainControlCreate(nint callback, int connected, out nint context);

    [LibraryImport(Library, EntryPoint = "ADL2_Main_Control_Destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int MainControlDestroy(nint context);

    [LibraryImport(Library, EntryPoint = "ADL2_Adapter_NumberOfAdapters_Get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int AdapterCount(nint context, out int count);

    [LibraryImport(Library, EntryPoint = "ADL2_Adapter_AdapterInfo_Get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int AdapterInfoOf(nint context, nint adapters, int size);

    [LibraryImport(Library, EntryPoint = "ADL2_Overdrive5_Temperature_Get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int Overdrive5Temperature(
        nint context,
        int adapter,
        int controller,
        ref Temperature temperature);

    [LibraryImport(Library, EntryPoint = "ADL2_OverdriveN_Temperature_Get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int OverdriveNTemperature(
        nint context,
        int adapter,
        int kind,
        ref int celsius);

    [LibraryImport(Library, EntryPoint = "ADL2_New_QueryPMLogData_Get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int QueryLog(nint context, int adapter, ref LogData data);
}
