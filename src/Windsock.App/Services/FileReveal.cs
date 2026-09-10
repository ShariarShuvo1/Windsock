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
    /// Opens Explorer at the file, or at its folder if the file has gone.
    /// </summary>
    public static void Show(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
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
