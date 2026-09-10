using System.Net;

namespace Windsock.Core.Networking;

/// <summary>
/// Folds host names down to the site they belong to.
/// </summary>
public static class DomainName
{
    private static readonly HashSet<string> Suffixes = new(StringComparer.Ordinal)
    {
        "ac", "co", "com", "edu", "gob", "gov", "govt", "int", "mil",
        "ne", "net", "or", "org", "sch", "web",
    };

    /// <summary>
    /// The site a host name belongs to, or the address itself when there is no
    /// name to fold.
    /// </summary>
    public static string Group(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return string.Empty;
        }

        string trimmed = host.Trim().TrimEnd('.');
        if (trimmed.Length == 0 || IPAddress.TryParse(trimmed, out _))
        {
            return trimmed;
        }

        string lowered = trimmed.ToLowerInvariant();
        string[] labels = lowered.Split('.');

        if (labels.Length <= 2)
        {
            return lowered;
        }
        int take = labels[^1].Length <= 3 && Suffixes.Contains(labels[^2]) ? 3 : 2;

        return string.Join('.', labels[^take..]);
    }
}
