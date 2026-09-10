using System.Runtime.InteropServices;

namespace Windsock.Core;

/// <summary>
/// Whether this process is running from an MSIX package.
/// </summary>
/// <remarks>
/// The Store edition and the GitHub edition are the same code, and a few things
/// only one of them may do. Registry writes are the sharp one: inside the
/// package container Windows redirects them into a private copy that only this
/// app can see, so a write succeeds, reads back, and changes nothing anyone
/// else will ever look at.
/// </remarks>
public static partial class PackageIdentity
{
    /// <summary>Returned when the caller has no package identity at all.</summary>
    private const int NoPackage = 15700;

    /// <summary>Whether Windows gave this process a package identity.</summary>
    public static bool Exists { get; } = Ask();

    private static bool Ask()
    {
        try
        {
            // Asking for the name into no buffer answers the only question here.
            // Packaged returns ERROR_INSUFFICIENT_BUFFER, unpackaged returns
            // APPMODEL_ERROR_NO_PACKAGE, and the name itself is of no interest.
            uint length = 0;
            return GetCurrentPackageFullName(ref length, 0) != NoPackage;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFullName")]
    private static partial int GetCurrentPackageFullName(ref uint length, nint name);
}
