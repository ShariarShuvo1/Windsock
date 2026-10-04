using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windsock.Core.Apps;

namespace Windsock.Core.Processes;

/// <summary>
/// Reads and caches what Windows will say about a process.
/// </summary>
public sealed class ProcessIdentityResolver : IProcessIdentityResolver
{
    private readonly Dictionary<int, ProcessIdentity> _cache = [];

    // The table, the details window and the history recorder ask from
    // different threads. The query itself runs outside the lock.
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public ProcessIdentity Resolve(int processId)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(processId, out ProcessIdentity? known))
            {
                return known;
            }
        }

        ProcessIdentity identity = Query(processId);

        lock (_gate)
        {
            _cache[processId] = identity;
        }

        return identity;
    }

    /// <inheritdoc />
    public void Forget(int processId)
    {
        lock (_gate)
        {
            _cache.Remove(processId);
        }
    }

    /// <summary>
    /// What Windows' data usage files a running process under: its package
    /// family when it runs a file its package installed, and otherwise its
    /// executable's kernel path (<c>\Device\HarddiskVolume3\...</c>), which
    /// Windows lower-cases. Both null for a process that has gone or will not
    /// say. Not cached: a process id is reused, and this is asked once a process.
    /// </summary>
    public static (string? NativePath, string? PackageFamily) ReadCountedAs(int processId)
    {
        if (processId <= 0)
        {
            return (null, null);
        }

        using SafeFileHandle handle = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            (uint)processId);

        if (handle.IsInvalid)
        {
            return (null, null);
        }

        string? native = ImagePath(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_NATIVE);
        string? path = ImagePath(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32);
        string? family = path is not null && OperatingSystem.IsWindowsVersionAtLeast(10)
            ? PackageFamily(handle, path)
            : null;

        return (native, family);
    }

    private static ProcessIdentity Query(int processId)
    {
        if (processId == 0)
        {
            return new ProcessIdentity("System Idle Process", null, null, null);
        }

        Process process;

        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // Exited between being seen and being asked about.
            return Unknown(processId);
        }
        catch (InvalidOperationException)
        {
            return Unknown(processId);
        }

        using (process)
        {
            string name;

            try
            {
                name = process.ProcessName;
            }
            catch (InvalidOperationException)
            {
                // Exited after it was found; Process only learns that on asking.
                return Unknown(processId);
            }

            (string? path, string? family) = ReadFile(process);

            return new ProcessIdentity(name, ReadDescription(path), path, ReadStartTime(process), family);
        }
    }

    /// <summary>The process's executable, and its package family when it runs a packaged app's own file.</summary>
    private static (string? Path, string? Family) ReadFile(Process process)
    {
        using SafeFileHandle handle = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            (uint)process.Id);

        string? path = handle.IsInvalid ? null : ImagePath(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32);

        if (path is null)
        {
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                path = null;
            }
        }

        string? family = !handle.IsInvalid && path is not null && OperatingSystem.IsWindowsVersionAtLeast(10)
            ? PackageFamily(handle, path)
            : null;

        return (path, family);
    }

    /// <summary>
    /// The package family of a process running a file its package installed,
    /// or null. A process can carry a package's identity and still run a file
    /// from elsewhere - a program a packaged app downloaded and started - and
    /// Windows counts that one by its path, so it is not called packaged here.
    /// </summary>
    [SupportedOSPlatform("windows8.1")]
    private static string? PackageFamily(SafeFileHandle handle, string path)
    {
        uint length = 0;
        WIN32_ERROR first = PInvoke.GetPackageFullName(handle, ref length, default);

        // An unpackaged process answers that it has no package.
        if (first != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER || length is 0 or > 256)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[(int)length];

        if (PInvoke.GetPackageFullName(handle, ref length, buffer) != WIN32_ERROR.NO_ERROR)
        {
            return null;
        }

        int end = buffer.IndexOf('\0');
        string fullName = new(end < 0 ? buffer : buffer[..end]);

        return AppNames.FolderOf(fullName) is { } folder
            && path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                ? RunningApps.FamilyOf(fullName)
                : null;
    }

    /// <summary>
    /// The process's executable. Asked with the least access Windows grants,
    /// which it gives for elevated processes and most services too; reading the
    /// main module instead needs rights an ordinary user does not have over
    /// those, and left them without a path, an icon, or a match to what Windows
    /// counted for them.
    /// </summary>
    private static string? ImagePath(SafeFileHandle handle, PROCESS_NAME_FORMAT format)
    {
        Span<char> buffer = stackalloc char[1024];
        uint length = (uint)buffer.Length;

        return PInvoke.QueryFullProcessImageName(handle, format, buffer, ref length)
            && length > 0 && length <= buffer.Length
            ? buffer[..(int)length].ToString()
            : null;
    }

    private static string? ReadDescription(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            return string.IsNullOrEmpty(description) ? null : description;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static ProcessIdentity Unknown(int processId) =>
        new(string.Create(CultureInfo.InvariantCulture, $"PID {processId}"), null, null, null);
}
