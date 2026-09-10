using System.Runtime.InteropServices;
using Windsock.Core;
using Windsock.Core.Startup;
using AppModel = Windows.ApplicationModel;
using Activation = Windows.ApplicationModel.Activation;

namespace Windsock.App.Services;

/// <summary>
/// Whether this launch should go straight to the taskbar without opening a
/// window.
/// </summary>
/// <remarks>
/// <para>
/// The unpackaged edition puts <c>--background</c> on the command line it
/// registers, so the answer is on the command line. A StartupTask takes no
/// arguments at all, so the packaged edition has nothing to read there and must
/// ask Windows how it was activated instead.
/// </para>
/// <para>
/// That question is answered by an API written for UWP, and a packaged desktop
/// app is not quite what it was written for, so it may return nothing. Nothing
/// is treated as an ordinary launch, which opens the window: the same thing
/// that happened before any of this existed, rather than a window that never
/// appears and a user who thinks the app failed to start.
/// </para>
/// </remarks>
internal static class QuietStart
{
    /// <summary>Whether to start without showing a window.</summary>
    public static bool Wanted(IEnumerable<string>? arguments) =>
        WindowsStartup.IsQuiet(arguments) || StartedByWindows();

    private static bool StartedByWindows()
    {
        if (!PackageIdentity.Exists)
        {
            return false;
        }

        try
        {
            return AppModel.AppInstance.GetActivatedEventArgs() is
            {
                Kind: Activation.ActivationKind.StartupTask,
            };
        }
        catch (Exception error) when (
            error is COMException or InvalidOperationException or NotSupportedException
                or PlatformNotSupportedException or TypeLoadException or MissingMethodException)
        {
            return false;
        }
    }
}
