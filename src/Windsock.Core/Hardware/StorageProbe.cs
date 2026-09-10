using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Windsock.Core.Hardware;

/// <summary>
/// Reads what drives are fitted, what is on them, and what they are doing.
/// </summary>
public sealed partial class StorageProbe
{
    private const string ReadCounter = "disk.read";
    private const string WriteCounter = "disk.write";
    private const string IdleCounter = "disk.idle";
    private const int MostDrives = 32;
    private readonly Dictionary<int, DriveIdentity> _known = [];
    private readonly Dictionary<int, List<VolumeReading>> _volumes = [];
    private List<int> _present = [];

    private sealed record DriveIdentity(string Model, string Bus, bool IsSpinning, long Size);

    /// <summary>Adds the counters this probe reads to a shared query.</summary>
    public static void Register(PerformanceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        _ = query.Add(ReadCounter, @"\PhysicalDisk(*)\Disk Read Bytes/sec", wildcard: true);
        _ = query.Add(WriteCounter, @"\PhysicalDisk(*)\Disk Write Bytes/sec", wildcard: true);
        _ = query.Add(IdleCounter, @"\PhysicalDisk(*)\% Idle Time", wildcard: true);
    }

    /// <summary>
    /// Takes a reading.
    /// </summary>
    public IReadOnlyList<DriveReading> Read(PerformanceQuery query, bool rescan)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (rescan || _present.Count == 0)
        {
            Rescan();
        }

        List<DriveReading> readings = new(_present.Count);

        foreach (int index in _present)
        {
            if (!_known.TryGetValue(index, out DriveIdentity? identity))
            {
                continue;
            }

            (double? celsius, double? life) = Health(index);

            readings.Add(new DriveReading(
                index,
                identity.Model,
                identity.Bus,
                identity.IsSpinning,
                identity.Size,
                celsius,
                life,
                Rate(query, ReadCounter, index),
                Rate(query, WriteCounter, index),
                Math.Clamp(100 - Rate(query, IdleCounter, index), 0, 100),
                _volumes.TryGetValue(index, out List<VolumeReading>? volumes) ? [.. volumes] : []));
        }

        return readings;
    }

    private static double Rate(PerformanceQuery query, string counter, int drive)
    {
        foreach (PerformanceQuery.Reading reading in query.Values(counter))
        {
            if (DiskNumber(reading.Instance) == drive)
            {
                return reading.Value;
            }
        }

        return 0;
    }

    private static int DiskNumber(string instance)
    {
        int at = 0;

        while (at < instance.Length && char.IsAsciiDigit(instance[at]))
        {
            at++;
        }

        return at > 0 && int.TryParse(instance.AsSpan(0, at), out int number) ? number : -1;
    }

    private void Rescan()
    {
        List<int> present = [];

        for (int index = 0; index < MostDrives; index++)
        {
            using DeviceHandle drive = DeviceHandle.Open(Drive(index));

            if (!drive.IsOpen)
            {
                continue;
            }

            present.Add(index);

            if (!_known.ContainsKey(index))
            {
                _known[index] = Identify(drive);
            }
        }

        _present = present;
        Mount();
    }

    private static string Drive(int index) =>
        string.Create(CultureInfo.InvariantCulture, $@"\\.\PhysicalDrive{index}");

    private static DriveIdentity Identify(DeviceHandle drive)
    {
        byte[] answer = new byte[1024];

        string model = drive.Query(StorageDeviceProperty, answer, out int got) && got >= 36
            ? Model(answer, got)
            : "Drive";

        string bus = got >= 32 ? BusName(answer[28]) : string.Empty;

        bool spinning = drive.Query(SeekPenaltyProperty, answer, out int penalty)
            && penalty > 8
            && answer[8] != 0;

        return new DriveIdentity(model, bus, spinning, Capacity(drive));
    }

    private static string Model(byte[] answer, int got)
    {
        string vendor = Text(answer, BitConverter.ToInt32(answer, 12), got);
        string product = Text(answer, BitConverter.ToInt32(answer, 16), got);
        string model = Smbios.Join(vendor, product);

        return model.Length > 0 ? model : "Drive";
    }

    private static long Capacity(DeviceHandle drive)
    {
        byte[] answer = new byte[64];

        return drive.Control(GetDriveGeometry, [], answer, out int got) && got >= 32
            ? BitConverter.ToInt64(answer, 24)
            : 0;
    }

    private static (double? Celsius, double? Life) Health(int index)
    {
        using DeviceHandle drive = DeviceHandle.Open(Drive(index));

        if (!drive.IsOpen)
        {
            return (null, null);
        }
        (double? celsius, double? life) = NvmeHealth(drive);

        return celsius is not null ? (celsius, life) : (Temperature(drive), life);
    }

    private static double? Temperature(DeviceHandle drive)
    {
        byte[] answer = new byte[512];

        if (!drive.Query(DeviceTemperatureProperty, answer, out int got) || got < 26)
        {
            return null;
        }

        int sensors = BitConverter.ToUInt16(answer, 12);
        for (int sensor = 0; sensor < sensors; sensor++)
        {
            int at = 24 + (sensor * 10);

            if (at + 4 > got)
            {
                break;
            }

            short celsius = BitConverter.ToInt16(answer, at + 2);

            if (celsius is > 0 and < 200)
            {
                return celsius;
            }
        }

        return null;
    }

    private static (double? Celsius, double? Life) NvmeHealth(DeviceHandle drive)
    {
        const int header = 8;
        const int specific = 40;
        const int page = 512;

        byte[] query = new byte[header + specific + page];

        BitConverter.GetBytes(ProtocolSpecificProperty).CopyTo(query, 0);
        BitConverter.GetBytes(StandardQuery).CopyTo(query, 4);
        BitConverter.GetBytes(NvmeProtocol).CopyTo(query, header);
        BitConverter.GetBytes(LogPageData).CopyTo(query, header + 4);
        BitConverter.GetBytes(HealthLogPage).CopyTo(query, header + 8);
        BitConverter.GetBytes(specific).CopyTo(query, header + 16);
        BitConverter.GetBytes(page).CopyTo(query, header + 20);

        byte[] answer = new byte[header + specific + page];

        if (!drive.Control(QueryProperty, query, answer, out int got))
        {
            return (null, null);
        }

        int at = header + specific;

        if (at + 6 > got)
        {
            return (null, null);
        }

        int kelvin = answer[at + 1] | (answer[at + 2] << 8);
        double? celsius = kelvin > 0 ? kelvin - 273 : null;
        double? life = answer[at + 5];

        return (celsius, life);
    }

    private void Mount()
    {
        foreach (List<VolumeReading> volumes in _volumes.Values)
        {
            volumes.Clear();
        }

        foreach (DriveInfo volume in Volumes())
        {
            string root = volume.Name.TrimEnd('\\');

            foreach (int drive in DrivesUnder(root))
            {
                if (!_volumes.TryGetValue(drive, out List<VolumeReading>? on))
                {
                    on = [];
                    _volumes[drive] = on;
                }

                on.Add(Describe(volume, root));
            }
        }
    }

    private static VolumeReading Describe(DriveInfo volume, string root)
    {
        string label;
        string format;
        long size;
        long free;

        try
        {
            label = volume.VolumeLabel;
            format = volume.DriveFormat;
            size = volume.TotalSize;
            free = volume.TotalFreeSpace;
        }
        catch (IOException)
        {
            // A volume that went away between being listed and being asked.
            return new VolumeReading(root, string.Empty, string.Empty, 0, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return new VolumeReading(root, string.Empty, string.Empty, 0, 0);
        }

        return new VolumeReading(root, label, format, size, free);
    }

    private static IEnumerable<DriveInfo> Volumes()
    {
        DriveInfo[] all;

        try
        {
            all = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (DriveInfo volume in all)
        {
            bool usable;

            try
            {
                usable = volume.IsReady && volume.DriveType is DriveType.Fixed or DriveType.Removable;
            }
            catch (IOException)
            {
                usable = false;
            }

            if (usable)
            {
                yield return volume;
            }
        }
    }

    private static IEnumerable<int> DrivesUnder(string root)
    {
        using DeviceHandle volume = DeviceHandle.Open($@"\\.\{root}");

        if (!volume.IsOpen)
        {
            yield break;
        }

        byte[] answer = new byte[8 + (24 * 16)];

        if (!volume.Control(GetVolumeExtents, [], answer, out int got) || got < 8)
        {
            yield break;
        }

        int extents = BitConverter.ToInt32(answer, 0);

        for (int extent = 0; extent < extents; extent++)
        {
            int at = 8 + (extent * 24);

            if (at + 4 > got)
            {
                break;
            }

            yield return BitConverter.ToInt32(answer, at);
        }
    }

    private static string Text(byte[] buffer, int at, int length)
    {
        if (at <= 0 || at >= length)
        {
            return string.Empty;
        }

        int end = at;

        while (end < length && buffer[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(buffer, at, end - at).Trim();
    }

    private static string BusName(byte bus) => bus switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "FireWire",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 or 15 => "Virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => string.Empty,
    };

    private const uint QueryProperty = 0x2D1400;
    private const uint GetDriveGeometry = 0x000700A0;
    private const uint GetVolumeExtents = 0x00560000;
    private const int StorageDeviceProperty = 0;
    private const int SeekPenaltyProperty = 7;
    private const int ProtocolSpecificProperty = 50;
    private const int DeviceTemperatureProperty = 52;
    private const int StandardQuery = 0;
    private const int NvmeProtocol = 3;
    private const int LogPageData = 2;
    private const int HealthLogPage = 2;

    private sealed partial class DeviceHandle : IDisposable
    {
        private nint _handle = -1;

        private DeviceHandle()
        {
        }

        public bool IsOpen => _handle != -1;

        public static DeviceHandle Open(string path)
        {
            const uint shareReadWrite = 3;
            const uint openExisting = 3;

            DeviceHandle device = new()
            {
                _handle = CreateFileW(path, 0, shareReadWrite, 0, openExisting, 0, 0),
            };

            return device;
        }

        /// <summary>Asks the storage stack for one of its property descriptors.</summary>
        public bool Query(int property, byte[] answer, out int got)
        {
            byte[] query = new byte[12];
            BitConverter.GetBytes(property).CopyTo(query, 0);
            BitConverter.GetBytes(StandardQuery).CopyTo(query, 4);

            return Control(QueryProperty, query, answer, out got);
        }

        public bool Control(uint code, byte[] query, byte[] answer, out int got)
        {
            got = 0;

            return IsOpen
                && DeviceIoControl(_handle, code, query, query.Length, answer, answer.Length, out got, 0);
        }

        public void Dispose()
        {
            if (IsOpen)
            {
                _ = CloseHandle(_handle);
                _handle = -1;
            }
        }

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial nint CreateFileW(
            string name,
            uint access,
            uint share,
            nint security,
            uint disposition,
            uint flags,
            nint template);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeviceIoControl(
            nint device,
            uint code,
            [In] byte[] query,
            int querySize,
            [Out] byte[] answer,
            int answerSize,
            out int returned,
            nint overlapped);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint handle);
    }
}
