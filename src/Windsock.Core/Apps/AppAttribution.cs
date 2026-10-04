using System.Text.RegularExpressions;

namespace Windsock.Core.Apps;

/// <summary>What Windows filed a stretch of traffic under.</summary>
public enum AppKind
{
    /// <summary>A program, named by the full path of its executable.</summary>
    Program,

    /// <summary>A packaged app, named by its package family.</summary>
    Package,

    /// <summary>Windows itself, or one of the buckets it keeps for its own traffic.</summary>
    System,

    /// <summary>Traffic Windows measured but could not put to any app.</summary>
    Unattributed,
}

/// <summary>
/// One of the identifiers Windows' data usage gives traffic, and what sort of
/// thing it names.
/// </summary>
/// <remarks>
/// Windows does not name what it counts; it hands back an identifier and an
/// empty name. Measured on 2026-09-15, the identifiers are of four shapes: an
/// NT device path in lower case (<c>\device\harddiskvolume3\program files\...\chrome.exe</c>),
/// a package family name (<c>Microsoft.WindowsStore_8wekyb3d8bbwe</c>),
/// <c>System</c> or a bucket under it (<c>System\IPv6 Control Message</c>), and
/// an empty string for bytes it could not attribute at all.
/// </remarks>
public readonly partial record struct AppAttribution(AppKind Kind, string Id)
{
    private const string SystemName = "System";

    /// <summary>
    /// The key the traffic of an unattributable minute is stored under, since
    /// an empty string cannot be told apart from a missing one.
    /// </summary>
    public const string UnattributedId = "";

    /// <summary>Works out what an identifier names.</summary>
    public static AppAttribution Classify(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return new AppAttribution(AppKind.Unattributed, UnattributedId);
        }

        string trimmed = id.Trim();

        if (trimmed.StartsWith('\\') || (trimmed.Length > 2 && trimmed[1] == ':' && trimmed[2] == '\\'))
        {
            return new AppAttribution(AppKind.Program, trimmed);
        }

        if (string.Equals(trimmed, SystemName, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(SystemName + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return new AppAttribution(AppKind.System, trimmed);
        }

        return PackageFamily().IsMatch(trimmed)
            ? new AppAttribution(AppKind.Package, trimmed)
            : new AppAttribution(AppKind.System, trimmed);
    }

    /// <summary>
    /// The part of a system bucket's identifier after <c>System\</c>, or
    /// <see langword="null"/> for Windows as a whole.
    /// </summary>
    public string? SystemBucket
    {
        get
        {
            if (Kind != AppKind.System || string.Equals(Id, SystemName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return Id.StartsWith(SystemName + "\\", StringComparison.OrdinalIgnoreCase)
                ? Id[(SystemName.Length + 1)..]
                : Id;
        }
    }

    // A package family name is the package name, an underscore and the
    // thirteen-character publisher hash, which is lower-case base 32.
    [GeneratedRegex("^[A-Za-z0-9.-]{3,50}_[a-z0-9]{13}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageFamily();
}
