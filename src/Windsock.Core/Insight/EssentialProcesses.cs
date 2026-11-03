namespace Windsock.Core.Insight;

/// <summary>
/// The programs and services Windows cannot do without, which Windsock never
/// offers to end or to block whatever the rights.
/// </summary>
public static class EssentialProcesses
{
    /// <summary>
    /// Processes Windows cannot run without. Ending one stops the PC; blocking
    /// one by its file cuts off everything it hosts - a rule on svchost.exe
    /// applies to every service host, name lookups and Windows Update included.
    /// </summary>
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Secure System", "Memory Compression", "smss", "csrss",
        "wininit", "winlogon", "services", "lsass", "LsaIso", "svchost", "fontdrvhost", "dwm",
    };

    /// <summary>
    /// Services Windows needs to reach any network at all. Blocking one by
    /// itself would cut the PC off, so it is never offered either.
    /// </summary>
    private static readonly HashSet<string> NetworkServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dnscache", "Dhcp", "NlaSvc", "netprofm", "nsi", "BFE", "MpsSvc", "RpcSs",
        "RpcEptMapper", "DcomLaunch", "LSM", "WinHttpAutoProxySvc", "Netman", "WcmSvc",
    };

    /// <summary>Whether a process's name, or its file's path, is one Windows cannot do without.</summary>
    public static bool IsEssential(string? nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
        {
            return false;
        }

        string trimmed = nameOrPath.Trim();

        // "Secure System" and "Memory Compression" have spaces and no file; a
        // path or a name with ".exe" is reduced to its name first.
        return Images.Contains(trimmed) || Images.Contains(Path.GetFileNameWithoutExtension(trimmed));
    }

    /// <summary>Whether a service is one Windows needs to reach any network.</summary>
    public static bool IsNetworkService(string? serviceName) =>
        !string.IsNullOrWhiteSpace(serviceName) && NetworkServices.Contains(serviceName.Trim());
}
