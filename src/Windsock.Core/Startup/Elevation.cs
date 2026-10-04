using System.Security.Principal;

namespace Windsock.Core.Startup;

/// <summary>Whether this process is running with administrator rights.</summary>
public static class Elevation
{
    /// <summary>
    /// True only for an elevated token. An administrator's ordinary session runs
    /// with a filtered token, which is not in the Administrators role, so this is
    /// false for them until they choose to elevate.
    /// </summary>
    public static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
