using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Windsock.Core.Hardware;

/// <summary>Whether the PawnIO channel is open, and if not, why not.</summary>
public enum PawnIoState
{
    Stopped,

    Running,

    NotInstalled,

    RequiresElevation,

    Unavailable,
}

internal sealed partial class PawnIo : IDisposable
{
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";
    private const string InstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
    private const uint DeviceType = 41394u << 16;

    private const uint LoadBinary = DeviceType | (0x821 << 2);
    private const uint Execute = DeviceType | (0x841 << 2);

    private const int NameLength = 32;
    private const uint GenericReadWrite = 0xC0000000;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private const int AccessDenied = 5;
    private nint _handle = -1;

    private PawnIo(nint handle) => _handle = handle;

    /// <summary>Which PawnIO is installed, or nothing where none is.</summary>
    public static Version? InstalledVersion
    {
        get
        {
            foreach (RegistryView view in (ReadOnlySpan<RegistryView>)[RegistryView.Registry64, RegistryView.Registry32])
            {
                try
                {
                    using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using RegistryKey? entry = machine.OpenSubKey(InstallKey);

                    if (Version.TryParse(entry?.GetValue("DisplayVersion") as string, out Version? version))
                    {
                        return version;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                }
            }

            return null;
        }
    }

    /// <summary>Whether the driver has been installed at all.</summary>
    public static bool IsInstalled => InstalledVersion is not null;

    /// <summary>Opens the device and loads one module into it.</summary>
    public static PawnIo? Open(string resource, out PawnIoState state)
    {
        if (!IsInstalled)
        {
            state = PawnIoState.NotInstalled;
            return null;
        }

        nint handle = CreateFileW(DevicePath, GenericReadWrite, ShareReadWrite, 0, OpenExisting, 0, 0);

        if (handle == -1)
        {
            state = Marshal.GetLastWin32Error() == AccessDenied
                ? PawnIoState.RequiresElevation
                : PawnIoState.Unavailable;

            return null;
        }

        byte[]? blob = ReadResource(resource);

        if (blob is null || !DeviceIoControl(handle, LoadBinary, blob, blob.Length, [], 0, out _, 0))
        {
            _ = CloseHandle(handle);
            state = PawnIoState.Unavailable;
            return null;
        }

        state = PawnIoState.Running;
        return new PawnIo(handle);
    }

    /// <summary>Calls a function in the loaded module.</summary>
    public long[]? Call(string name, long[] input, int outputs)
    {
        if (_handle == -1)
        {
            return null;
        }

        // The call is one buffer: a fixed name field, then the arguments.
        byte[] query = new byte[NameLength + (input.Length * sizeof(long))];
        int written = Encoding.ASCII.GetBytes(name, 0, Math.Min(name.Length, NameLength - 1), query, 0);

        if (written != name.Length)
        {
            return null;
        }

        Buffer.BlockCopy(input, 0, query, NameLength, input.Length * sizeof(long));

        byte[] answer = new byte[outputs * sizeof(long)];

        if (!DeviceIoControl(_handle, Execute, query, query.Length, answer, answer.Length, out int got, 0)
            || got < answer.Length)
        {
            return null;
        }

        long[] values = new long[outputs];
        Buffer.BlockCopy(answer, 0, values, 0, answer.Length);

        return values;
    }

    public void Dispose()
    {
        if (_handle != -1)
        {
            _ = CloseHandle(_handle);
            _handle = -1;
        }
    }

    private static byte[]? ReadResource(string resource)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);

        if (stream is null)
        {
            return null;
        }

        using MemoryStream buffer = new();
        stream.CopyTo(buffer);

        return buffer.ToArray();
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
