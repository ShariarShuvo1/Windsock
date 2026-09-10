namespace Windsock.Core.Settings;

/// <summary>Which colour scheme Windsock should use.</summary>
public enum ThemePreference
{
    System,

    Light,

    Dark,
}

/// <summary>What closing the window does.</summary>
public enum CloseAction
{
    Quit,

    KeepRunning,
}

/// <summary>
/// Everything the user has chosen that outlives a run.
/// </summary>
public sealed class WindsockSettings
{
    private TaskbarSettings _taskbar = new();

    /// <summary>Which colour scheme to use.</summary>
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>
    /// The meter that sits in the Windows taskbar.
    /// </summary>
    public TaskbarSettings Taskbar
    {
        get => _taskbar;
        set => _taskbar = value ?? new TaskbarSettings();
    }

    /// <summary>
    /// What closing the window does.
    /// </summary>
    public CloseAction OnClose { get; set; } = CloseAction.Quit;

    /// <summary>
    /// Whether Windsock looks for a newer version by itself.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Whether usage is written to the history database.
    /// </summary>
    public bool RecordHistory { get; set; } = true;

    /// <summary>
    /// Whether the way to each far end is found and drawn.
    /// </summary>
    public bool FollowRoutes { get; set; } = true;

    /// <summary>
    /// The adapters to measure, by interface LUID, or empty for all of them.
    /// </summary>
    public List<ulong> Adapters { get; set; } = [];
}
