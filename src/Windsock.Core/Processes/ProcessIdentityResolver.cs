using System.Diagnostics;
using System.Globalization;

namespace Windsock.Core.Processes;

/// <summary>
/// Reads and caches what Windows will say about a process.
/// </summary>
public sealed class ProcessIdentityResolver : IProcessIdentityResolver
{
    private readonly Dictionary<int, ProcessIdentity> _cache = [];

    /// <inheritdoc />
    public ProcessIdentity Resolve(int processId)
    {
        if (_cache.TryGetValue(processId, out ProcessIdentity? identity))
        {
            return identity;
        }

        identity = Query(processId);
        _cache[processId] = identity;
        return identity;
    }

    /// <inheritdoc />
    public void Forget(int processId) => _cache.Remove(processId);

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
            string name = process.ProcessName;
            string? path = ReadPath(process);

            return new ProcessIdentity(name, ReadDescription(path), path, ReadStartTime(process));
        }
    }

    private static string? ReadPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
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
