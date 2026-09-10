namespace Windsock.Core;

/// <summary>
/// Filesystem locations Windsock uses at runtime.
/// </summary>
public static class WindsockPaths
{
    /// <summary>
    /// The publisher folder the data root sits inside.
    /// </summary>
    /// <remarks>
    /// This layer is load bearing and must not be removed. The installer packs
    /// with <c>--packId Windsock</c>, so Velopack owns
    /// <c>%LOCALAPPDATA%\Windsock</c> and its uninstaller deletes that whole
    /// folder. Writing the database there put a year of history inside a
    /// directory something else was entitled to remove, and it did.
    /// Nesting under the publisher keeps the two apart, and matches the
    /// <c>Program Files\Shariar Shuvo\Windsock</c> layout the MSI already uses
    /// for a per-machine install.
    /// </remarks>
    private const string Publisher = "Shariar Shuvo";

    /// <summary>Root directory for all Windsock runtime data.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify),
        Publisher,
        "Windsock");

    /// <summary>Directory holding rolling log files.</summary>
    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>Full path to the usage database.</summary>
    public static string Database => Path.Combine(Root, "Windsock.db");

    /// <summary>Full path to the settings file.</summary>
    public static string Settings => Path.Combine(Root, "settings.json");

    /// <summary>
    /// Creates the data directories if they do not already exist.
    /// Safe to call repeatedly and from multiple processes.
    /// </summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }
}
