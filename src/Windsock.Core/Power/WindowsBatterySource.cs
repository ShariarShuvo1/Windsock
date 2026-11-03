using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Windsock.Core.Power;

/// <summary>
/// Reads the batteries through the battery class driver, as a standard user.
/// </summary>
/// <remarks>
/// <para>
/// The batteries are found by their device interface,
/// <c>GUID_DEVICE_BATTERY</c>, through <c>CM_Get_Device_Interface_List</c>
/// rather than the SetupDi walk Microsoft's battery sample uses: the answer is
/// the same list of paths, without the detail structure whose size field must
/// be 8 on a 64-bit process and 6 on a 32-bit one.
/// </para>
/// <para>
/// Each is opened for reading only. The queries are declared
/// <c>FILE_READ_ACCESS</c>, so that is all they need; Microsoft's sample asks
/// for write access too, which nothing here uses. A battery the system will
/// not open is reported as refused, and the reading falls back to Windows'
/// own summary.
/// </para>
/// </remarks>
public sealed partial class WindowsBatterySource : IBatterySource
{
    private static readonly Guid BatteryInterface = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");

    /// <summary>How long a list of batteries is trusted before it is asked for again.</summary>
    private static readonly TimeSpan ListFor = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private long _listedAt = long.MinValue;
    private bool _refused;

    /// <inheritdoc />
    public unsafe SystemPowerStatus ReadSystemStatus()
    {
        byte* status = stackalloc byte[BatteryLayout.SystemStatusSize];

        return GetSystemPowerStatus(status)
            ? BatteryLayout.ReadSystemStatus(new ReadOnlySpan<byte>(status, BatteryLayout.SystemStatusSize)) ?? SystemPowerStatus.Unread
            : SystemPowerStatus.Unread;
    }

    /// <inheritdoc />
    public BatteryScan ReadBatteries(bool refresh)
    {
        long now = Environment.TickCount64;

        if (_slots.Count == 0 || now - _listedAt >= (long)ListFor.TotalMilliseconds)
        {
            List();
            _listedAt = now;
        }

        List<BatteryFigures> batteries = [];
        List<string>? gone = null;

        foreach ((string path, Slot slot) in _slots)
        {
            if (slot.Read(refresh) is { } figures)
            {
                batteries.Add(figures);
            }
            else if (slot.IsGone)
            {
                (gone ??= []).Add(path);
            }
        }

        if (gone is not null)
        {
            // A device that went away, or one that has failed several times
            // running: let its handle go, and list again next time, which opens
            // it afresh if it is still there - a UPS plugged back in on the same
            // path, a battery driver restarted after sleep.
            foreach (string path in gone)
            {
                _slots[path].Dispose();
                _ = _slots.Remove(path);
            }

            _listedAt = long.MinValue;
        }

        return new BatteryScan(batteries, _refused);
    }

    private void List()
    {
        HashSet<string> present = new(Paths(), StringComparer.OrdinalIgnoreCase);
        _refused = false;

        foreach (string gone in _slots.Keys.Where(path => !present.Contains(path)).ToList())
        {
            _slots[gone].Dispose();
            _ = _slots.Remove(gone);
        }

        foreach (string path in present)
        {
            if (_slots.ContainsKey(path))
            {
                continue;
            }

            SafeFileHandle handle = CreateFileW(path, GenericRead, ShareReadWrite, 0, OpenExisting, NormalFile, 0);

            if (handle.IsInvalid)
            {
                _refused |= Marshal.GetLastPInvokeError() == AccessDenied;
                handle.Dispose();
                continue;
            }

            _slots[path] = new Slot(handle);
        }
    }

    private static unsafe IReadOnlyList<string> Paths()
    {
        Guid battery = BatteryInterface;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint length, &battery, null, InterfacesPresent) != Success || length <= 1)
            {
                return [];
            }

            char[] list = new char[length];

            fixed (char* buffer = list)
            {
                uint result = CM_Get_Device_Interface_ListW(&battery, null, buffer, length, InterfacesPresent);

                if (result == Success)
                {
                    return BatteryLayout.SplitList(list);
                }

                if (result != BufferSmall)
                {
                    return [];
                }
            }
        }

        return [];
    }

    public void Dispose()
    {
        foreach (Slot slot in _slots.Values)
        {
            slot.Dispose();
        }

        _slots.Clear();
    }

    /// <summary>One battery device, held open, and what is known of the battery in it.</summary>
    private sealed class Slot(SafeFileHandle handle) : IDisposable
    {
        private uint _tag = BatteryLayout.NoTag;
        private BatteryInformation? _information;
        private string? _name;
        private string? _maker;
        private DateOnly? _made;
        private bool _noTemperature;
        private readonly BatterySlotHealth _health = new();
        private BatteryFigures? _last;

        /// <summary>Whether the device has gone, or failed often enough to be opened afresh.</summary>
        public bool IsGone => _health.IsGone;

        public BatteryFigures? Read(bool refresh)
        {
            (uint tag, BatterySlotAnswer answer) = Tag();

            if (answer == BatterySlotAnswer.Passing)
            {
                // A hiccup - not ready on waking, busy - keeps the last
                // figures for the few seconds it takes to pass, rather than
                // dropping the battery to Windows' summary for one reading.
                return _last;
            }

            if (tag == BatteryLayout.NoTag)
            {
                _tag = BatteryLayout.NoTag;
                _last = null;
                return null;
            }

            if (tag != _tag)
            {
                // A different battery, or the first look at this one.
                _tag = tag;
                _information = null;
                _name = Text(BatteryInformationLevel.DeviceName);
                _maker = Text(BatteryInformationLevel.ManufactureName);
                _made = Ask(BatteryInformationLevel.ManufactureDate, 4) is { } date ? BatteryLayout.ReadDate(date) : null;
                _noTemperature = false;
                refresh = true;
            }

            if (refresh || _information is null)
            {
                _information = Ask(BatteryInformationLevel.Information, BatteryLayout.InformationSize) is { } information
                    ? BatteryLayout.ReadInformation(information)
                    : _information;
            }

            BatteryStatus? status = Call(BatteryLayout.QueryStatus, BatteryLayout.Wait(tag), BatteryLayout.StatusSize) is { } live
                ? BatteryLayout.ReadStatus(live)
                : null;

            double? celsius = null;

            if (!_noTemperature)
            {
                celsius = Ask(BatteryInformationLevel.Temperature, 4) is { } heat ? BatteryLayout.ReadCelsius(heat) : null;

                // Most batteries do not report one; asking every time would be wasted.
                _noTemperature = celsius is null;
            }

            _last = new BatteryFigures(_information, status, _name, _maker, _made, celsius);
            return _last;
        }

        private unsafe (uint Tag, BatterySlotAnswer Answer) Tag()
        {
            uint wait = 0;
            uint tag = BatteryLayout.NoTag;

            if (!DeviceIoControl(handle, BatteryLayout.QueryTag, &wait, sizeof(uint), &tag, sizeof(uint), out uint got, 0))
            {
                return (BatteryLayout.NoTag, _health.Failed(Marshal.GetLastPInvokeError()));
            }

            _health.Answered();
            return (got >= sizeof(uint) ? tag : BatteryLayout.NoTag, BatterySlotAnswer.Answered);
        }

        private byte[]? Ask(BatteryInformationLevel level, int size) =>
            Call(BatteryLayout.QueryInformation, BatteryLayout.Query(_tag, level), size);

        private string? Text(BatteryInformationLevel level) =>
            Ask(level, BatteryLayout.StringSize * 2) is { } text ? BatteryLayout.ReadText(text) : null;

        private unsafe byte[]? Call(uint code, byte[] query, int size)
        {
            byte[] answer = new byte[size];

            fixed (byte* input = query)
            fixed (byte* output = answer)
            {
                if (!DeviceIoControl(handle, code, input, (uint)query.Length, output, (uint)size, out uint got, 0))
                {
                    return null;
                }

                return got == size ? answer : answer.AsSpan(0, (int)got).ToArray();
            }
        }

        public void Dispose() => handle.Dispose();
    }

    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private const uint NormalFile = 0x80;
    private const int AccessDenied = 5;
    private const uint InterfacesPresent = 0;
    private const uint Success = 0;
    private const uint BufferSmall = 0x1A;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetSystemPowerStatus(byte* status);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string name,
        uint access,
        uint share,
        nint security,
        uint disposition,
        uint flags,
        nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(
        SafeFileHandle device,
        uint code,
        void* input,
        uint inputSize,
        void* output,
        uint outputSize,
        out uint returned,
        nint overlapped);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint CM_Get_Device_Interface_List_SizeW(
        out uint length,
        Guid* interfaceClass,
        string? deviceId,
        uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint CM_Get_Device_Interface_ListW(
        Guid* interfaceClass,
        string? deviceId,
        char* buffer,
        uint length,
        uint flags);
}

/// <summary>What a failed tag query says about a battery device.</summary>
internal enum BatterySlotAnswer
{
    /// <summary>The device answered.</summary>
    Answered,

    /// <summary>The slot has no battery in it now: nothing is wrong with the device.</summary>
    Empty,

    /// <summary>A failure that may pass - not ready after sleep, busy, timed out.</summary>
    Passing,

    /// <summary>The device has gone, or has failed too often running to keep.</summary>
    Gone,
}

/// <summary>
/// Decides when a battery device that will not answer is let go and opened
/// afresh, rather than either trusted for ever or written off at the first
/// failure.
/// </summary>
/// <remarks>
/// Until 2026-10-05 any failure but "no battery in the slot" marked the device
/// gone for the rest of the run, and nothing cleared it: one
/// <c>ERROR_NOT_READY</c> on waking from sleep turned the battery off until a
/// restart. Now an error that says the device itself went away lets it go at
/// once; anything else must happen <see cref="FailuresBeforeGone"/> times
/// running; any answer clears the count.
/// </remarks>
internal sealed class BatterySlotHealth
{
    /// <summary>How many failures running before an unexplained failure counts as gone.</summary>
    public const int FailuresBeforeGone = 3;

    private const int FileNotFound = 2;
    private const int InvalidHandle = 6;
    private const int NoSuchDevice = 433;
    private const int DeviceNotConnected = 1167;
    private const int DeviceRemoved = 1617;

    /// <summary>Failures since the last answer.</summary>
    public int Failures { get; private set; }

    /// <summary>Whether the device should be let go and opened afresh.</summary>
    public bool IsGone { get; private set; }

    /// <summary>The device answered: whatever went before is forgotten.</summary>
    public void Answered()
    {
        Failures = 0;
        IsGone = false;
    }

    /// <summary>The device refused with a Win32 error; says what that means.</summary>
    public BatterySlotAnswer Failed(int error)
    {
        if (error is NoSuchDevice or FileNotFound)
        {
            // How the battery class says a slot is empty: the device is fine.
            Failures = 0;
            IsGone = false;
            return BatterySlotAnswer.Empty;
        }

        if (error is DeviceRemoved or DeviceNotConnected or InvalidHandle)
        {
            IsGone = true;
            return BatterySlotAnswer.Gone;
        }

        Failures++;
        IsGone = Failures >= FailuresBeforeGone;
        return IsGone ? BatterySlotAnswer.Gone : BatterySlotAnswer.Passing;
    }
}
