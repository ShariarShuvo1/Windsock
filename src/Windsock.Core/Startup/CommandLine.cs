namespace Windsock.Core.Startup;

/// <summary>The switches Windsock can be started with, and how they are read.</summary>
public static class CommandLine
{
    /// <summary>Whether a switch is among the arguments Windsock was given.</summary>
    public static bool Has(IEnumerable<string>? arguments, string flag)
    {
        if (arguments is null)
        {
            return false;
        }

        foreach (string argument in arguments)
        {
            if (string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
