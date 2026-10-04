namespace Windsock.Core.Notifications;

/// <summary>
/// What a clicked notice asks Windsock to do, written into the notice and read
/// back when Windows hands it over.
/// </summary>
/// <remarks>
/// The text comes back from outside the process - from Windows, and for the
/// unpackaged edition through a COM call any local program could make - so it is
/// parsed strictly: one known action, one section name of plain lowercase
/// letters, and anything else is treated as a plain "open the window".
/// </remarks>
public static class NoticeLaunch
{
    private const string OpenAction = "action=open";
    private const string SectionKey = "section=";
    private const int LongestSection = 32;

    /// <summary>The launch text for a notice that opens a section, or the window.</summary>
    public static string Format(string? section) =>
        IsSectionName(section) ? string.Concat(OpenAction, "&", SectionKey, section) : OpenAction;

    /// <summary>
    /// The section a clicked notice asked for, or nothing when it asked for the
    /// window as it was or said something this version does not understand.
    /// </summary>
    public static string? Section(string? launch)
    {
        if (string.IsNullOrEmpty(launch) || launch.Length > 128)
        {
            return null;
        }

        string? section = null;
        bool opens = false;

        foreach (string part in launch.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(part, OpenAction, StringComparison.Ordinal))
            {
                opens = true;
            }
            else if (part.StartsWith(SectionKey, StringComparison.Ordinal))
            {
                section = part[SectionKey.Length..];
            }
        }

        return opens && IsSectionName(section) ? section : null;
    }

    private static bool IsSectionName(string? section)
    {
        if (section is null || section.Length is 0 or > LongestSection)
        {
            return false;
        }

        foreach (char each in section)
        {
            if (each is not (>= 'a' and <= 'z') and not '-')
            {
                return false;
            }
        }

        return true;
    }
}
