using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Windsock.Core;
using Windsock.Core.Navigation;

namespace Windsock.App.Services;

/// <summary>
/// The jump list: what right-clicking Windsock's taskbar button or its pinned
/// shortcut offers, each a page to open straight to.
/// </summary>
/// <remarks>
/// <para>
/// Each task starts Windsock with <c>--open</c> and a page's link name. A
/// Windsock already running is handed the link by the one just started (see
/// <c>SingleInstance</c>) and turns to the page; otherwise the new one opens on it.
/// </para>
/// <para>
/// <b>Identity.</b> A jump list belongs to an AppUserModelID, and Windsock never
/// sets one of its own. In an installed copy Velopack's <c>VelopackApp.Run</c>
/// sets the process's to <c>velopack.Windsock</c>, the <c>shortcutAumid</c> its
/// package manifest carries and the id it stamps on the Start menu and desktop
/// shortcuts it makes, so the running window, the pinned shortcut and this
/// list are one button. WPF's list takes the process's id when it is applied,
/// which is why it is applied after Velopack has run. The notification id,
/// <c>ShariarShuvo.Windsock</c>, is a separate registration for notices alone;
/// setting it as the process id would part the window from its pinned shortcut,
/// and every pin made with 0.1.0 carries Velopack's. In the Store edition the
/// package supplies the id, and Microsoft's guidance is not to set one in code
/// at all; a packaged jump list may point only at the app's own exe, which these do.
/// </para>
/// <para>
/// <b>When.</b> Handing the list to the shell took 100 to 140 ms of the UI
/// thread, measured on 2026-10-05, and it sat in front of the meter docking and
/// the window's first frame. It is now handed over once the UI thread is first
/// idle, from a thread of its own: WPF's list needs a single-threaded apartment
/// and nothing of the dispatcher's, so any STA thread will do.
/// </para>
/// </remarks>
internal sealed partial class JumpTasks(ILogger<JumpTasks> logger)
{
    /// <summary>The tasks, in the order the list shows them: a page's link name, the words, and the tooltip.</summary>
    /// <remarks>One line a task. A page that wants a place here adds its line.</remarks>
    private static readonly (string Link, string Title, string Description)[] Tasks =
    [
        ("history", "History", "Open Usage › History"),
        ("plan", "Data plan", "Open Usage › Data plan: what is left and how long it lasts"),
        ("status", "Connection", "Open Connection › Status"),
        ("outages", "Outages", "Open Connection › Outages"),
        ("speedtest", "Speed test", "Open Connection › Speed test, without starting a test"),
        ("alerts", "Alerts", "Open Alerts"),
        ("mini", "Compact window", "Open the compact window: the rates and their last minute, always on top"),
    ];

    /// <summary>
    /// Puts the list in place for this program once the UI thread is first
    /// idle, on a thread of its own. Asked once at start, after
    /// <c>VelopackApp.Run</c>: Windows keeps the list, and refreshing it each
    /// start keeps its paths right after an update moves the program.
    /// </summary>
    /// <remarks>
    /// A copy on a moved data root is a build being checked, and sets no list:
    /// the list belongs to the AppUserModelID, which such a copy can share with
    /// the installed Windsock, so its paths would replace the installed one's
    /// until that started again - as a scratch build's did, 2026-10-05. The
    /// same rule keeps such a copy out of the notification registration
    /// (<c>ToastChannel.Open</c>).
    /// </remarks>
    public void ApplyWhenIdle(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        if (WindsockPaths.IsOverridden)
        {
            LogMovedRoot(logger);
            return;
        }

        application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            Thread thread = new(() => Apply(application))
            {
                IsBackground = true,
                Name = "Jump list",
                Priority = ThreadPriority.BelowNormal,
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }));
    }

    /// <summary>Hands the list to the shell, on whatever STA thread calls it.</summary>
    private void Apply(Application application)
    {
        string? program = Environment.ProcessPath;

        if (string.IsNullOrEmpty(program))
        {
            return;
        }

        try
        {
            JumpList list = new()
            {
                ShowFrequentCategory = false,
                ShowRecentCategory = false,
            };

            foreach ((string link, string title, string description) in Tasks)
            {
                list.JumpItems.Add(new JumpTask
                {
                    Title = title,
                    Description = description,
                    ApplicationPath = program,
                    Arguments = PageLink.Arguments(link),
                    IconResourcePath = program,
                    IconResourceIndex = 0,
                });
            }

            list.JumpItemsRejected += OnRejected;
            JumpList.SetJumpList(application, list);

            LogApplied(logger, Tasks.Length, PackageIdentity.Exists);
        }
#pragma warning disable CA1031 // On a thread of its own, anything uncaught ends the process.
        catch (Exception error)
#pragma warning restore CA1031
        {
            // The shell can refuse a list - a policy that turns them off, an
            // Explorer that is not running. Nothing else depends on it, so no
            // failure here is worth the app: it is written down and left.
            LogFailed(logger, error);
        }
    }

    private void OnRejected(object? sender, JumpItemsRejectedEventArgs e)
    {
        for (int index = 0; index < e.RejectedItems.Count; index++)
        {
            string title = e.RejectedItems[index] is JumpTask task ? task.Title : string.Empty;
            LogRejected(logger, title, e.RejectionReasons[index]);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Jump list set with {Count} tasks (packaged: {Packaged})")]
    private static partial void LogApplied(ILogger logger, int count, bool packaged);

    [LoggerMessage(Level = LogLevel.Information, Message = "Jump list not set: the data root is moved, so this is a build being checked")]
    private static partial void LogMovedRoot(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The jump list could not be set")]
    private static partial void LogFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The shell refused the jump list task {Title}: {Reason}")]
    private static partial void LogRejected(ILogger logger, string title, JumpItemRejectionReason reason);
}
