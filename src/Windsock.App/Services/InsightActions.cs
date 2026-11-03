using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Windsock.App.Views;
using Windsock.Core.Insight;
using Windsock.Core.Processes;
#if !STORE
using Windsock.Core.Control;
using Windsock.Core.Startup;
#endif

namespace Windsock.App.Services;

/// <summary>
/// An app, or one process, that someone asked to act on from a row: what to
/// call it, its file, and the processes it was made of when they asked.
/// </summary>
/// <param name="Name">What the row calls it.</param>
/// <param name="FilePath">The program's file, when Windows would say.</param>
/// <param name="Processes">Its processes, each with the start time seen, so a reused id is never touched.</param>
/// <param name="ServiceName">The service, for a row that is a service host running exactly one service.</param>
/// <param name="Hosted">
/// Every service its service hosts run, when it is one or more service hosts;
/// empty otherwise. More than one means a shared host, which is never blocked.
/// </param>
public sealed record AppTarget(
    string Name,
    string? FilePath,
    IReadOnlyList<EndTarget> Processes,
    string? ServiceName = null,
    IReadOnlyList<HostedService>? Hosted = null)
{
    /// <summary>
    /// What a row's processes stand for. A service host is told by its file or
    /// its name, whether or not the list of services has been read yet, so it
    /// can never fall through to a rule on svchost.exe; its one service is
    /// named only when it runs exactly one.
    /// </summary>
    public static AppTarget For(string name, string? path, IReadOnlyCollection<ProcessUsage> processes, ServiceHostMap map)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(map);

        List<HostedService> hosted = [.. processes.Where(IsHostProcess).SelectMany(usage => map.In(usage.ProcessId))];

        return new AppTarget(
            name,
            string.IsNullOrEmpty(path) ? null : path,
            [.. processes.Select(usage => new EndTarget(usage.ProcessId, usage.Name, usage.Identity.StartedAt, usage.Identity.FilePath))],
            hosted.Count == 1 ? hosted[0].Name : null,
            hosted);
    }

    /// <summary>Whether any of its processes is a Windows service host, by name or by file.</summary>
    public bool IsServiceHost =>
        Processes.Any(each => IsHostName(each.Name) || IsHostName(each.Path))
        || IsHostName(FilePath);

    private static bool IsHostProcess(ProcessUsage usage) =>
        IsHostName(usage.Name) || IsHostName(usage.Identity.Name) || IsHostName(usage.Identity.FilePath);

    private static bool IsHostName(string? nameOrPath) =>
        !string.IsNullOrWhiteSpace(nameOrPath)
        && string.Equals(System.IO.Path.GetFileNameWithoutExtension(nameOrPath.Trim()), "svchost", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The actions the Apps page, the details window and the Activity page offer
/// on a program: end it, look it up on VirusTotal, and in the full edition
/// block it through the firewall or close one of its connections. Each asks
/// first, says exactly what it will touch, and says how it went.
/// </summary>
public sealed class InsightActions
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>Whether Windsock is running as administrator, which ending others' programs, closing connections and the firewall need.</summary>
    public static bool IsElevated => Environment.IsPrivilegedProcess;

#if !STORE
    /// <summary>Raised after something was blocked or unblocked, so pages showing rules read them again.</summary>
    public event EventHandler? FirewallChanged;
#endif

    /// <summary>
    /// Asks, then ends every process of an app, and says what happened to
    /// each. The probing and the ending run off the window's thread, under one
    /// deadline for all of them, so the window never stops answering.
    /// </summary>
    public static async Task EndAsync(Window? owner, AppTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        List<EndTarget> offered = [.. target.Processes.Where(each => !ProcessEnder.IsNeverOffered(each.ProcessId, each.Name))];

        if (offered.Count == 0)
        {
            InsightDialog.Tell(
                owner,
                "End app",
                string.Concat(target.Name, " cannot be ended from here"),
                "It is part of Windows, or Windsock itself. Ending it would stop the PC or this window.",
                []);
            return;
        }

        List<EndResult> probes = await Task.Run(() => offered.Select(ProcessEnder.Probe).ToList()).ConfigureAwait(true);
        int refused = probes.Count(each => each.Outcome == EndOutcome.NeedsAdministrator);
        int paths = offered.Select(each => each.Path).Where(path => path is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        string body = string.Concat(
            "Windows stops it at once. It gets no chance to save anything, so unsaved work in it is lost.",
            paths > 1
                ? string.Format(CultureInfo.CurrentCulture, " These processes run {0} different files; each line says which.", paths)
                : string.Empty,
            refused > 0 && !IsElevated
                ? string.Format(CultureInfo.CurrentCulture, " {0} of its processes run as another account or with higher rights; only an administrator can end those, so they will be left running.", refused)
                : string.Empty);

        if (!InsightDialog.Ask(
            owner,
            "End app",
            string.Concat("End ", target.Name, "?"),
            body,
            [.. probes.Select(Line)],
            offered.Count == 1 ? "End it" : string.Format(CultureInfo.CurrentCulture, "End {0} processes", offered.Count)))
        {
            return;
        }

        IReadOnlyList<EndResult> results = await Task.Run(() => ProcessEnder.End(offered, Patience)).ConfigureAwait(true);
        int ended = results.Count(each => each.Outcome is EndOutcome.Ended or EndOutcome.AlreadyGone);

        InsightDialog.Tell(
            owner,
            "End app",
            ended == results.Count
                ? string.Concat(target.Name, " has ended")
                : string.Format(CultureInfo.CurrentCulture, "{0} of {1} processes ended", ended, results.Count),
            ended == results.Count ? string.Empty : "The rest are still running; each line says why.",
            [.. results.Select(Line)],
            warning: ended != results.Count);
    }

    /// <summary>Opens the VirusTotal window for a program. Nothing is sent until the person asks there.</summary>
    public static void CheckReputation(Window? owner, AppTarget target, Core.Security.ReputationService reputation)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(reputation);

        ReputationDialog.Show(owner, target, reputation);
    }

    /// <summary>Opens a web page in the browser, for a link the person pressed.</summary>
    public static void OpenLink(Uri link)
    {
        ArgumentNullException.ThrowIfNull(link);

        try
        {
            Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

#if !STORE
    /// <summary>The Windsock rule blocking a program or service, or null; reads the firewall, so not on every frame.</summary>
    public static FirewallRule? RuleFor(AppTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return target.ServiceName is { } service
            ? FirewallBlocks.ForService(service)
            : target.FilePath is { } path ? FirewallBlocks.ForProgram(path) : null;
    }

    /// <summary>Asks, then blocks a program or service through Windows Firewall, or removes Windsock's block.</summary>
    public void ToggleBlock(Window? owner, AppTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (RuleFor(target) is { } existing)
        {
            Unblock(owner, existing);
            return;
        }

        if (Refusal(target) is { } refusal)
        {
            InsightDialog.Tell(owner, "Block app", refusal.Heading, refusal.Body, refusal.Lines);
            return;
        }

        // What the rule is called and what it applies to, exactly as it will be made.
        string what = target.ServiceName is { } name ? string.Concat("the ", name, " service") : target.FilePath!;
        List<string> lines =
        [
            string.Concat("Rule: ", FirewallBlocks.RuleName(target.Name, target.ServiceName ?? target.FilePath!)),
            string.Concat("Applies to: ", what),
            "Direction: outbound, every network profile",
            "Group: Windsock",
        ];

        if (FirewallBlocks.Standing() is { Note: { } note })
        {
            lines.Add(note);
        }

        if (!FirewallBlocks.CanChange)
        {
            InsightDialog.Tell(
                owner,
                "Block app",
                "Blocking needs administrator rights",
                "Windows Firewall takes new rules only from an administrator. Restart Windsock as administrator, from the Apps page or Settings › Advanced, and ask again.",
                lines,
                warning: true);
            return;
        }

        if (!InsightDialog.Ask(
            owner,
            "Block app",
            string.Concat("Block ", target.Name, " from the network?"),
            "Windows Firewall will stop it reaching any network, whether or not Windsock is running, until the rule is removed - here, on the Activity page, or in Windows Defender Firewall. Uninstalling Windsock does not remove it.",
            lines,
            "Block it"))
        {
            return;
        }

        FirewallResult result = target.ServiceName is { } service
            ? FirewallBlocks.BlockService(service, target.Name)
            : FirewallBlocks.BlockProgram(target.FilePath!, target.Name);

        Report(owner, "Block app", result);
    }

    /// <summary>Asks, then removes one of Windsock's firewall rules.</summary>
    public void Unblock(Window? owner, FirewallRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!InsightDialog.Ask(
            owner,
            "Unblock",
            "Remove this block?",
            "The program can use the network again as soon as the rule is gone.",
            [rule.Name, rule.ServiceName ?? rule.ApplicationPath ?? string.Empty],
            "Remove the rule"))
        {
            return;
        }

        Report(owner, "Unblock", FirewallBlocks.Remove(rule.Name));
    }

    /// <summary>
    /// Why a target is never blocked, in words for a dialog, or null when it
    /// may be. A service host is blocked only by its one service; svchost.exe
    /// itself never, since a rule on it reaches every service host at once.
    /// </summary>
    private static (string Heading, string Body, IReadOnlyList<string> Lines)? Refusal(AppTarget target)
    {
        IReadOnlyList<HostedService> hosted = target.Hosted ?? [];

        if (target.ServiceName is { } service)
        {
            return FirewallBlocks.ServiceRefusal(service) is { } refused
                ? (string.Concat(target.Name, " is never blocked"), refused, [service])
                : null;
        }

        if (target.IsServiceHost)
        {
            return hosted.Count > 1
                ? (
                    "A shared service host is not blocked",
                    "This one process runs several Windows services. Windows Firewall can block a service by its name, but these share a host, and blocking them together could stop something Windows needs; blocking svchost.exe itself would block every service host on the PC. Windsock blocks a service only where its host runs that one service.",
                    [.. hosted.Select(each => string.Concat(each.DisplayName, " (", each.Name, ")"))])
                : (
                    "Windsock cannot tell which service this is",
                    "This is a Windows service host, and the list of services it runs has not been read yet or Windows would not give it. A rule on svchost.exe would block every service host on the PC at once - name lookups and Windows Update included - so Windsock blocks only one service by its name. Try again in a moment.",
                    []);
        }

        if (FirewallBlocks.ProgramRefusal(target.FilePath) is { } program)
        {
            return (string.Concat(target.Name, " is never blocked"), program, [target.FilePath!]);
        }

        return string.IsNullOrWhiteSpace(target.FilePath)
            ? ("This app cannot be blocked from here", "Windows Firewall blocks a program by its file, and Windows will not say which file this one runs.", [])
            : null;
    }

    /// <summary>Asks, then removes every firewall rule Windsock made, as should be done before uninstalling.</summary>
    public void RemoveAllBlocks(Window? owner)
    {
        if (!FirewallBlocks.TryList(out IReadOnlyList<FirewallRule> rules))
        {
            InsightDialog.Tell(
                owner,
                "Remove all",
                "Windows Firewall could not be read",
                "Windsock could not list its rules, so it cannot say whether any are left. Try again, or look for the group Windsock in Windows Defender Firewall with Advanced Security.",
                [],
                warning: true);
            return;
        }

        if (rules.Count == 0)
        {
            InsightDialog.Tell(owner, "Remove all", "Windsock has no firewall rules", "There is nothing to remove.", []);
            return;
        }

        if (!InsightDialog.Ask(
            owner,
            "Remove all",
            string.Format(CultureInfo.CurrentCulture, "Remove all {0} of Windsock's firewall rules?", rules.Count),
            "Every program and service Windsock blocked can use the network again.",
            [.. rules.Select(rule => rule.Name)],
            "Remove them all"))
        {
            return;
        }

        IReadOnlyList<FirewallResult> results = FirewallBlocks.RemoveAll();
        int done = results.Count(each => each.Outcome == FirewallOutcome.Done);

        InsightDialog.Tell(
            owner,
            "Remove all",
            done == results.Count ? "All removed" : string.Format(CultureInfo.CurrentCulture, "{0} of {1} removed", done, results.Count),
            done == results.Count ? string.Empty : "Windows refused the rest; each line says why.",
            [.. results.Select(each => each.Message)],
            warning: done != results.Count);

        FirewallChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks, then closes one TCP connection.</summary>
    public static void Close(Window? owner, ProcessConnection connection, string label)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (TcpConnectionCloser.CannotClose(connection) is { } refused)
        {
            InsightDialog.Tell(owner, "Close connection", "This one cannot be closed", refused.Message, []);
            return;
        }

        string line = string.Format(
            CultureInfo.CurrentCulture,
            "{0}:{1} to {2}:{3}",
            connection.LocalAddress,
            connection.LocalPort,
            label,
            connection.RemotePort);

        if (!Environment.IsPrivilegedProcess)
        {
            InsightDialog.Tell(
                owner,
                "Close connection",
                "Closing a connection needs administrator rights",
                "Windows closes a connection from outside its program only for an administrator. Restart Windsock as administrator and ask again.",
                [line],
                warning: true);
            return;
        }

        if (!InsightDialog.Ask(
            owner,
            "Close connection",
            "Close this connection?",
            "Windows resets it at once. The program sees it fail, and may simply connect again.",
            [line],
            "Close it"))
        {
            return;
        }

        CloseResult result = TcpConnectionCloser.Close(connection);
        InsightDialog.Tell(owner, "Close connection", result.Outcome == CloseOutcome.Closed ? "Closed" : "Not closed", result.Message, [line], warning: result.Outcome != CloseOutcome.Closed);
    }

    /// <summary>Starts Windsock again as administrator, after Windows asks for consent.</summary>
    public static void RestartElevated()
    {
        if (Environment.ProcessPath is not { } path)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = SingleInstance.ReplaceSwitch,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;
        }

        Application.Current?.Shutdown();
    }

    private void Report(Window? owner, string title, FirewallResult result)
    {
        InsightDialog.Tell(
            owner,
            title,
            result.Outcome switch
            {
                FirewallOutcome.Done => "Done",
                FirewallOutcome.AlreadyBlocked => "Already blocked",
                FirewallOutcome.NotAllowed => "Never blocked",
                _ => "Not done",
            },
            result.Message,
            result.Rule is { } rule ? [rule.Name] : [],
            warning: result.Outcome is not (FirewallOutcome.Done or FirewallOutcome.AlreadyBlocked));

        FirewallChanged?.Invoke(this, EventArgs.Empty);
    }
#endif

    private static string Line(EndResult result) => result.Target.Path is { } path
        ? string.Format(CultureInfo.CurrentCulture, "{0} (PID {1}, {2}): {3}", result.Target.Name, result.Target.ProcessId, path, result.Message)
        : string.Format(CultureInfo.CurrentCulture, "{0} (PID {1}): {2}", result.Target.Name, result.Target.ProcessId, result.Message);
}
