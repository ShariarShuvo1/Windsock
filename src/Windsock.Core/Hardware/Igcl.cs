using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Windsock.Core.Hardware;

/// <summary>
/// The control library that ships with the Intel graphics driver.
/// </summary>
public sealed partial class Igcl : IDisposable
{
    private const string Library = "ControlLib.dll";
    private const int Ok = 0;

    private const uint Version = (1 << 16) | 1;

    private const uint UseLevelZero = 1;
    private const uint MostDevices = 64;
    private const double Coldest = 1;
    private const double Hottest = 150;
    private readonly List<nint> _devices = [];
    private nint _api;
    private bool _closed;

    private Igcl()
    {
    }

    /// <summary>How many Intel adapters it found.</summary>
    public int Count => _devices.Count;

    /// <summary>
    /// Loads the library and takes hold of every adapter it knows.
    /// </summary>
    public static Igcl? Open()
    {
        Igcl igcl = new();

        try
        {
            InitArgs args = default;
            args.Size = (uint)Marshal.SizeOf<InitArgs>();
            args.Version = 0;
            args.AppVersion = Version;
            args.Flags = UseLevelZero;

            if (Init(ref args, ref igcl._api) != Ok || igcl._api == 0)
            {
                return null;
            }
        }
        catch (DllNotFoundException)
        {
            // No Intel graphics driver on this machine.
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            // A driver too old to carry the entry points this expects.
            return null;
        }

        igcl.Fill();

        // A library that loaded but holds nothing is no better than none.
        if (igcl.Count == 0)
        {
            igcl.Dispose();
            return null;
        }

        return igcl;
    }

    private void Fill()
    {
        try
        {
            uint count = 0;

            if (Devices(_api, ref count, null) != Ok || count == 0)
            {
                return;
            }

            count = Math.Min(count, MostDevices);

            nint[] handles = new nint[count];

            if (Devices(_api, ref count, handles) != Ok)
            {
                return;
            }

            for (int index = 0; index < count && index < handles.Length; index++)
            {
                if (handles[index] != 0)
                {
                    _devices.Add(handles[index]);
                }
            }
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// The temperature of one adapter, or nothing where it will not say.
    /// </summary>
    public unsafe double? Read(int index)
    {
        if (_closed || index < 0 || index >= _devices.Count)
        {
            return null;
        }

        int size = sizeof(Telemetry);
        nint buffer = Marshal.AllocHGlobal(size * 2);

        try
        {
            new Span<byte>((void*)buffer, size * 2).Clear();

            Telemetry* telemetry = (Telemetry*)buffer;
            telemetry->Size = (uint)size;
            telemetry->Version = 0;

            if (Telemetry_Get(_devices[index], buffer) != Ok)
            {
                return null;
            }

            return Believable(Value(telemetry->GpuTemperature));
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static double? Value(in TelemetryItem item)
    {
        if (item.Supported == 0)
        {
            return null;
        }

        return item.Type switch
        {
            DataType.Int8 or DataType.Int16 or DataType.Int32 => (int)item.Raw,
            DataType.UInt8 or DataType.UInt16 or DataType.UInt32 => (uint)item.Raw,
            DataType.Int64 => item.Raw,
            DataType.UInt64 => (ulong)item.Raw,
            DataType.Float => BitConverter.Int32BitsToSingle((int)item.Raw),
            DataType.Double => BitConverter.Int64BitsToDouble(item.Raw),
            _ => null,
        };
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
        _devices.Clear();

        if (_api == 0)
        {
            return;
        }

        try
        {
            _ = Close(_api);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        _api = 0;
    }

    private static class DataType
    {
        public const int Int8 = 0;
        public const int UInt8 = 1;
        public const int Int16 = 2;
        public const int UInt16 = 3;
        public const int Int32 = 4;
        public const int UInt32 = 5;
        public const int Int64 = 6;
        public const int UInt64 = 7;
        public const int Float = 8;
        public const int Double = 9;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
        public uint SupportedVersion;
        public uint IdData1;
        public ushort IdData2;
        public ushort IdData3;
        public ulong IdData4;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TelemetryItem
    {
        public int Supported;
        public int Units;
        public int Type;
        public long Raw;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PsuInfo
    {
        public int Supported;
        public int Type;
        public TelemetryItem Energy;
        public TelemetryItem Voltage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Telemetry
    {
        public uint Size;
        public byte Version;
        public TelemetryItem TimeStamp;
        public TelemetryItem GpuEnergy;
        public TelemetryItem GpuVoltage;
        public TelemetryItem GpuClock;

        /// <summary>The one figure this class exists to read.</summary>
        public TelemetryItem GpuTemperature;
        public TelemetryItem GlobalActivity;
        public TelemetryItem RenderActivity;
        public TelemetryItem MediaActivity;
        public int GpuPowerLimited;
        public int GpuTemperatureLimited;
        public int GpuCurrentLimited;
        public int GpuVoltageLimited;
        public int GpuUtilisationLimited;
        public TelemetryItem VramEnergy;
        public TelemetryItem VramVoltage;
        public TelemetryItem VramClock;
        public TelemetryItem VramEffectiveClock;
        public TelemetryItem VramRead;
        public TelemetryItem VramWrite;
        public TelemetryItem VramTemperature;
        public int VramPowerLimited;
        public int VramTemperatureLimited;
        public int VramCurrentLimited;
        public int VramVoltageLimited;
        public int VramUtilisationLimited;
        public TelemetryItem TotalCardEnergy;
        public PsuInfo Psu0;
        public PsuInfo Psu1;
        public PsuInfo Psu2;
        public PsuInfo Psu3;
        public PsuInfo Psu4;
        public TelemetryItem Fan0;
        public TelemetryItem Fan1;
        public TelemetryItem Fan2;
        public TelemetryItem Fan3;
        public TelemetryItem Fan4;
        public TelemetryItem GpuVrTemperature;
        public TelemetryItem VramVrTemperature;
        public TelemetryItem SaVrTemperature;
        public TelemetryItem GpuEffectiveClock;
        public TelemetryItem GpuOverVoltagePercent;
        public TelemetryItem GpuPowerPercent;
        public TelemetryItem GpuTemperaturePercent;
        public TelemetryItem VramReadBandwidth;
        public TelemetryItem VramWriteBandwidth;
    }

    [LibraryImport(Library, EntryPoint = "ctlInit")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int Init(ref InitArgs args, ref nint api);

    [LibraryImport(Library, EntryPoint = "ctlClose")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int Close(nint api);

    [LibraryImport(Library, EntryPoint = "ctlEnumerateDevices")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int Devices(nint api, ref uint count, [Out] nint[]? devices);

    [LibraryImport(Library, EntryPoint = "ctlPowerTelemetryGet")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int Telemetry_Get(nint device, nint telemetry);
}
