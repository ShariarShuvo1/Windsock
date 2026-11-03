namespace Windsock.Core.Navigation;

/// <summary>
/// A link to one page of the window: its plain name, and how a command line,
/// a jump list task or a second launch carries it.
/// </summary>
/// <remarks>
/// <para>
/// A page's name is the key of its line in the sidebar - "history",
/// "outages", "processor" - and every way into the window names pages the
/// same way: a clicked notification, the jump list, <c>--open</c> on the
/// command line, and a second Windsock handing its request to the one already
/// running.
/// </para>
/// <para>
/// Names come back from outside the process (a command line anybody can type,
/// a notice, a pipe any program of this user could write to), so they are held
/// to plain lowercase letters and hyphens, and anything else is no link at all,
/// which opens the window as it was.
/// </para>
/// </remarks>
public static class PageLink
{
    /// <summary>The switch that names a page to open: <c>--open history</c>, or <c>--open=history</c>.</summary>
    public const string OpenSwitch = "--open";

    /// <summary>The longest name a link may carry.</summary>
    public const int LongestName = 32;

    /// <summary>Whether a name could be a page's: 1 to 32 lowercase letters and hyphens.</summary>
    public static bool IsName(string? name)
    {
        if (name is null || name.Length is 0 or > LongestName)
        {
            return false;
        }

        foreach (char each in name)
        {
            if (each is not (>= 'a' and <= 'z') and not '-')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The page a command line asks for, or nothing when it asks for none or
    /// names one that cannot be a page. The switch is matched without regard
    /// to case; the name must already be lowercase.
    /// </summary>
    public static string? FromArguments(IEnumerable<string>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        bool next = false;

        foreach (string argument in arguments)
        {
            if (next)
            {
                return IsName(argument) ? argument : null;
            }

            if (string.Equals(argument, OpenSwitch, StringComparison.OrdinalIgnoreCase))
            {
                next = true;
                continue;
            }

            if (argument.Length > OpenSwitch.Length + 1
                && argument.StartsWith(OpenSwitch, StringComparison.OrdinalIgnoreCase)
                && argument[OpenSwitch.Length] is '=' or ':')
            {
                string name = argument[(OpenSwitch.Length + 1)..];
                return IsName(name) ? name : null;
            }
        }

        return null;
    }

    /// <summary>The command line that opens a page, for a jump list task or a shortcut.</summary>
    public static string Arguments(string name)
    {
        if (!IsName(name))
        {
            throw new ArgumentException("A page's name is lowercase letters and hyphens.", nameof(name));
        }

        return string.Concat(OpenSwitch, " ", name);
    }
}
