namespace Windsock.App.ViewModels;

/// <summary>
/// Settings › Data and privacy's home network switches, and Settings ›
/// Advanced's local feed, overlay, command line and AI assistants.
/// </summary>
public sealed partial class SettingsPanelViewModel
{
    /// <summary>Their own view model, which the two bands bind to; scoped with this one, so it goes with the window.</summary>
    public HomeFeedSettingsViewModel HomeFeed { get; }
}
