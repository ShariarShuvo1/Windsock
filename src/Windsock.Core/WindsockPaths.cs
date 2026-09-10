namespace Windsock.Core;

/// <summary>
/// Filesystem locations Windsock uses at runtime.
/// </summary>
public static class WindsockPaths
{
    /// <summary>Root directory for all Windsock runtime data.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify),
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
