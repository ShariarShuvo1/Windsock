namespace Windsock.Core.Feed;

/// <summary>
/// Adding a folder to, and taking it out of, a PATH value - the user's own
/// PATH in HKCU\Environment, so that typing <c>windsock</c> in a console finds
/// the command line. Pure, so the edit is tested without touching anybody's
/// environment.
/// </summary>
/// <remarks>
/// Entries are compared as Windows resolves them: quotes and surrounding space
/// ignored, environment variables expanded (the value is REG_EXPAND_SZ, so an
/// entry may read <c>%LOCALAPPDATA%\Windsock\current</c>), a trailing backslash
/// ignored, and case ignored. Nothing but the folder's own entries is ever
/// changed, and the order of the rest is kept.
/// </remarks>
public static class UserPath
{
    /// <summary>Whether a PATH value already holds a folder.</summary>
    public static bool Contains(string? path, string folder, Func<string, string>? expand = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        string wanted = Normalise(folder, expand);
        return Entries(path).Any(each => string.Equals(Normalise(each, expand), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The value with the folder added at the end, or unchanged when it is there already.</summary>
    public static string Add(string? path, string folder, Func<string, string>? expand = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        if (Contains(path, folder, expand))
        {
            return path ?? string.Empty;
        }

        string trimmed = (path ?? string.Empty).TrimEnd(';', ' ');
        string entry = folder.Trim().TrimEnd('\\');
        return trimmed.Length == 0 ? entry : string.Concat(trimmed, ";", entry);
    }

    /// <summary>The value with every entry for the folder taken out, everything else as it was.</summary>
    public static string Remove(string? path, string folder, Func<string, string>? expand = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        string wanted = Normalise(folder, expand);
        IEnumerable<string> kept = (path ?? string.Empty)
            .Split(';')
            .Where(each => each.Trim().Length == 0 || !string.Equals(Normalise(each, expand), wanted, StringComparison.OrdinalIgnoreCase));

        return string.Join(';', kept).Trim(';');
    }

    private static IEnumerable<string> Entries(string? path) =>
        (path ?? string.Empty).Split(';').Where(each => each.Trim().Length > 0);

    private static string Normalise(string entry, Func<string, string>? expand)
    {
        string trimmed = entry.Trim().Trim('"').Trim();
        string expanded = (expand ?? Environment.ExpandEnvironmentVariables)(trimmed);
        return expanded.TrimEnd('\\', '/');
    }
}
