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

    /// <summary>
    /// Environment variable that moves the whole data root elsewhere, so a
    /// build being checked can run without reading or writing anybody's real
    /// history. Only an absolute path is honoured.
    /// </summary>
    public const string RootVariable = "WINDSOCK_DATA_ROOT";

    /// <summary>Root directory for all Windsock runtime data.</summary>
    public static string Root { get; } = Choose(Environment.GetEnvironmentVariable(RootVariable));

    /// <summary>
    /// The data root for a given override: the override when it is an absolute
    /// path, otherwise the usual folder under local app data.
    /// </summary>
    public static string Choose(string? overridden) =>
        overridden is { Length: > 0 } && Path.IsPathFullyQualified(overridden)
            ? Path.GetFullPath(overridden)
            : Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData,
                    Environment.SpecialFolderOption.DoNotVerify),
                Publisher,
                "Windsock");

    /// <summary>Whether <see cref="RootVariable"/> moved the data root.</summary>
    public static bool IsOverridden { get; } =
        !string.Equals(Root, Choose(null), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The name the single-instance lock is taken under. A copy running on a
    /// moved data root takes its own, so checking a build never hands an
    /// installed Windsock's activations to it, or the other way round.
    /// </summary>
    public static string InstanceName => IsOverridden
        ? "Windsock-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Root.ToUpperInvariant())))[..12]
        : "Windsock";

    /// <summary>Directory holding rolling log files.</summary>
    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>Full path to the usage database.</summary>
    public static string Database => Path.Combine(Root, "Windsock.db");

    /// <summary>Full path to the outage log.</summary>
    public static string Outages => Path.Combine(Root, "Outages.db");

    /// <summary>
    /// Full path to the record of alerts, the networks remembered for the change
    /// alerts, and the snooze.
    /// </summary>
    public static string Alerts => Path.Combine(Root, "Alerts.db");

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
