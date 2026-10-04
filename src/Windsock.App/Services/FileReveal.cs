using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Windsock.App.Services;

/// <summary>Opens a file's folder in Explorer, with the file picked out.</summary>
public static class FileReveal
{
    /// <summary>Whether there is anything at this path to show.</summary>
    public static bool Exists(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <summary>
    /// Whether a path is an ordinary full path Explorer can open: a drive
    /// letter or a share, not a device path, a relative path or anything
    /// carrying a quote that would end the argument early.
    /// </summary>
    public static bool IsOrdinaryPath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
        && !path.StartsWith(@"\\.\", StringComparison.Ordinal)
        && !path.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase)
        && path.IndexOfAny(Path.GetInvalidPathChars()) < 0
        && !path.Contains('"', StringComparison.Ordinal);

    /// <summary>Whether a path is inside the folder Windows installs packages into.</summary>
    public static bool IsInstalledPackage(string path)
    {
        string apps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolderOption.DoNotVerify),
            "WindowsApps") + Path.DirectorySeparatorChar;

        return path.StartsWith(apps, StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opens Explorer at the file, or at the folder itself when the path is a
    /// folder, or at its folder if the file has gone.
    /// </summary>
    public static void Show(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsOrdinaryPath(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                Start(string.Format(CultureInfo.InvariantCulture, "/select,\"{0}\"", path));
                return;
            }

            if (Directory.Exists(path))
            {
                Start(string.Format(CultureInfo.InvariantCulture, "\"{0}\"", path));
                return;
            }

            string? folder = Path.GetDirectoryName(path);

            if (Directory.Exists(folder))
            {
                Start(string.Format(CultureInfo.InvariantCulture, "\"{0}\"", folder));
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
        }
    }

    private static void Start(string arguments) =>
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true })?.Dispose();
}
