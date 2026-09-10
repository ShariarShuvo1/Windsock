namespace Windsock.Core.Startup;

/// <summary>Where the run-at-login setting has got to.</summary>
public enum StartupState
{
    /// <summary>Windows will not start Windsock at sign-in.</summary>
    Off,

    /// <summary>Windows will start Windsock at sign-in.</summary>
    On,

    /// <summary>
    /// Turned off in Task Manager or Settings, and only the person who did that
    /// can turn it back on. Windsock is not allowed to override them.
    /// </summary>
    BlockedByUser,

    /// <summary>Not offered on this machine, with a reason worth showing.</summary>
    Unavailable,
}

/// <summary>
/// Whether Windows starts Windsock when the user signs in.
/// </summary>
/// <remarks>
/// There are two mechanisms and they are not interchangeable. The unpackaged
/// build writes an HKCU Run value. The packaged build cannot: the container
/// redirects that write into a private copy, so it appears to work and does
/// nothing. A packaged app declares a StartupTask in its manifest instead and
/// switches it through Windows itself, which is also what puts it in the
/// Startup apps list where the user expects to find it.
/// </remarks>
public interface IStartupSwitch
{
    /// <summary>What the last look found. Refreshed by <see cref="RefreshAsync"/>.</summary>
    StartupState State { get; }

    /// <summary>Why the setting is unavailable, when it is. Empty otherwise.</summary>
    string Detail { get; }

    /// <summary>Looks again, because Windows may have been told something else.</summary>
    Task<StartupState> RefreshAsync();

    /// <summary>
    /// Asks for the setting to change, and answers with what it actually became.
    /// Asking to turn on something the user blocked leaves it blocked.
    /// </summary>
    Task<StartupState> SetAsync(bool wanted);
}
