using System.Globalization;
using System.Runtime.InteropServices;
using Windsock.Core.Insight;

namespace Windsock.Core.Control;

/// <summary>How a firewall change went.</summary>
public enum FirewallOutcome
{
    /// <summary>Done.</summary>
    Done,

    /// <summary>Only an administrator may change the firewall, and Windsock is not running as one.</summary>
    NeedsAdministrator,

    /// <summary>A Windsock rule already blocks it.</summary>
    AlreadyBlocked,

    /// <summary>No Windsock rule of that name exists.</summary>
    NotFound,

    /// <summary>Windows refused, or the firewall could not be reached.</summary>
    Failed,

    /// <summary>
    /// Never done, whatever the rights: blocking it would cut the PC off -
    /// svchost.exe as a program (every service host at once), a part of
    /// Windows, or a service Windows needs to reach any network.
    /// </summary>
    NotAllowed,
}

/// <summary>One outbound block rule Windsock made, as Windows Firewall holds it.</summary>
/// <param name="Name">The rule's name, which identifies it.</param>
/// <param name="ApplicationPath">The program it blocks, or null for a service rule.</param>
/// <param name="ServiceName">The service it blocks, or null for a program rule.</param>
/// <param name="Enabled">Whether it is switched on.</param>
/// <param name="Description">What it says about itself.</param>
public sealed record FirewallRule(string Name, string? ApplicationPath, string? ServiceName, bool Enabled, string? Description);

/// <summary>What a firewall change did.</summary>
public sealed record FirewallResult(FirewallOutcome Outcome, string Message, FirewallRule? Rule = null);

/// <summary>Whether a block rule can take effect on the networks the PC is on now.</summary>
/// <param name="FirewallOn">Whether the firewall is on for every current profile.</param>
/// <param name="OutboundAllowedByDefault">
/// Whether outbound traffic is allowed unless a rule blocks it, as Windows
/// ships. When it is blocked by default a block rule changes nothing.
/// </param>
/// <param name="LocalRulesIgnored">
/// Whether Group Policy overrides the PC's own rules
/// (<c>LocalPolicyModifyState</c> is GP override), so a rule Windsock adds is
/// kept but not applied.
/// </param>
public sealed record FirewallStanding(bool FirewallOn, bool OutboundAllowedByDefault, bool LocalRulesIgnored = false)
{
    /// <summary>Whether a block rule would stop anything now.</summary>
    public bool RulesTakeEffect => FirewallOn && OutboundAllowedByDefault && !LocalRulesIgnored;

    /// <summary>Why a rule would not stop anything now, in words, or null when it would.</summary>
    public string? Note => LocalRulesIgnored
        ? "Group Policy on this PC overrides its own firewall rules, so Windows keeps this rule but does not apply it."
        : !FirewallOn
            ? "Windows Firewall is off on this network, so the rule does nothing until it is turned back on."
            : !OutboundAllowedByDefault
                ? "Outbound traffic is already blocked unless allowed on this network, so this rule changes nothing until that changes."
                : null;
}

/// <summary>
/// Blocks a program or a service from the network through Windows Defender
/// Firewall: an outbound rule in a group called Windsock, listed in one place
/// and removable. Full edition, as administrator.
/// </summary>
/// <remarks>
/// <para>
/// Through the firewall's own COM interface (<c>HNetCfg.FwPolicy2</c>), late
/// bound, so no interop assembly ships. Reading the rules works for a standard
/// user (583 rules read on 2026-10-05); adding or removing one is refused with
/// access denied unless the caller is elevated, so that is checked first and
/// never tried. No driver: the rule is Windows' own and works whether or not
/// Windsock runs.
/// </para>
/// <para>
/// <b>An uninstall cannot remove these rules.</b> They were made by an
/// elevated process; the uninstaller runs as the person, unelevated. Every
/// rule carries the group name Windsock and a description saying so, and
/// <see cref="RemoveAll"/> takes them all out, which the page offers before
/// anyone uninstalls. Group Policy or another firewall product can override
/// them, and a rule changes nothing while the firewall is off.
/// </para>
/// </remarks>
public static class FirewallBlocks
{
    /// <summary>The group every Windsock rule carries, so they can be found together.</summary>
    public const string Group = "Windsock";

    private const int BlockAction = 0;          // NET_FW_ACTION_BLOCK
    private const int Outbound = 2;              // NET_FW_RULE_DIRECTION_OUT
    private const int AllProfiles = 0x7FFFFFFF;  // NET_FW_PROFILE2_ALL
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int GroupPolicyOverride = 1;   // NET_FW_MODIFY_STATE_GP_OVERRIDE

    /// <summary>Whether this process may change the firewall.</summary>
    public static bool CanChange => Environment.IsPrivilegedProcess;

    /// <summary>Every rule Windsock made. Works without administrator rights; empty when the firewall cannot be read.</summary>
    public static IReadOnlyList<FirewallRule> List() => TryList(out IReadOnlyList<FirewallRule> rules) ? rules : [];

    /// <summary>
    /// Every rule Windsock made, and whether the firewall could be read at all,
    /// so "Windsock has no rules" is never said of a firewall nobody could read.
    /// </summary>
    public static bool TryList(out IReadOnlyList<FirewallRule> rules)
    {
        List<FirewallRule> found = [];

        try
        {
            dynamic policy = Policy();

            foreach (dynamic rule in policy.Rules)
            {
                if (IsOurs(rule))
                {
                    found.Add(Read(rule));
                }
            }
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            rules = [];
            return false;
        }

        rules = found;
        return true;
    }

    /// <summary>
    /// Why a program must never be blocked by its file, or null when it may:
    /// svchost.exe is every service host at once, and the rest are Windows
    /// itself.
    /// </summary>
    public static string? ProgramRefusal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (string.Equals(Path.GetFileNameWithoutExtension(path.Trim()), "svchost", StringComparison.OrdinalIgnoreCase))
        {
            return "This is a Windows service host. A rule on svchost.exe would block every service host on the PC at once - name lookups, address renewal and Windows Update included - so Windsock only ever blocks one service by its name.";
        }

        return EssentialProcesses.IsEssential(path)
            ? "This is part of Windows. Blocking it would cut the PC off, so it is never done."
            : null;
    }

    /// <summary>Why a service must never be blocked, or null when it may.</summary>
    public static string? ServiceRefusal(string? serviceName) =>
        EssentialProcesses.IsNetworkService(serviceName)
            ? "Windows needs this service to reach any network at all, so blocking it would cut the PC off. It is never done."
            : null;

    /// <summary>The Windsock rule blocking a program, or null.</summary>
    public static FirewallRule? ForProgram(string path) =>
        List().FirstOrDefault(rule => rule.ServiceName is null
            && string.Equals(rule.ApplicationPath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Windsock rule blocking a service, or null.</summary>
    public static FirewallRule? ForService(string serviceName) =>
        List().FirstOrDefault(rule => string.Equals(rule.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether block rules take effect on the current networks, or null when the firewall cannot be read.</summary>
    public static FirewallStanding? Standing()
    {
        try
        {
            dynamic policy = Policy();
            int current = (int)policy.CurrentProfileTypes;
            bool on = true;
            bool allowed = true;
            bool ignored = false;

            // Readable unelevated (checked 2026-10-05). Read on its own so a
            // firewall that will not say leaves the rest of the answer standing.
            try
            {
                ignored = (int)policy.LocalPolicyModifyState == GroupPolicyOverride;
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
            }

            foreach (int profile in new[] { 1, 2, 4 })
            {
                if ((current & profile) == 0)
                {
                    continue;
                }

                on &= (bool)policy.FirewallEnabled[profile];
                allowed &= (int)policy.DefaultOutboundAction[profile] != BlockAction;
            }

            return new FirewallStanding(on, allowed, ignored);
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return null;
        }
    }

    /// <summary>Blocks a program's outbound traffic on every network.</summary>
    /// <param name="path">The program's full Win32 path.</param>
    /// <param name="displayName">What to call it in the rule's name.</param>
    public static FirewallResult BlockProgram(string path, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (ProgramRefusal(path) is { } refused)
        {
            return new FirewallResult(FirewallOutcome.NotAllowed, refused);
        }

        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            return new FirewallResult(FirewallOutcome.Failed, "A program is blocked by its full path, and this one has none Windows Firewall can use.");
        }

        if (ForProgram(path) is { } existing)
        {
            return new FirewallResult(FirewallOutcome.AlreadyBlocked, "Windsock already blocks it.", existing);
        }

        return Add(RuleName(displayName, path), Description(path), path, serviceName: null);
    }

    /// <summary>
    /// Blocks one service's outbound traffic, by the service rather than its
    /// shared host, so the other services in the host go on working.
    /// </summary>
    public static FirewallResult BlockService(string serviceName, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (ServiceRefusal(serviceName) is { } refused)
        {
            return new FirewallResult(FirewallOutcome.NotAllowed, refused);
        }

        if (ForService(serviceName) is { } existing)
        {
            return new FirewallResult(FirewallOutcome.AlreadyBlocked, "Windsock already blocks it.", existing);
        }

        string host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "svchost.exe");
        return Add(RuleName(displayName, serviceName), Description("the " + serviceName + " service"), host, serviceName);
    }

    /// <summary>
    /// Removes one Windsock rule. A rule outside the Windsock group is never
    /// touched, even one with the same name: the firewall removes by name, so
    /// the rule itself is first given a name nothing else has.
    /// </summary>
    public static FirewallResult Remove(string ruleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleName);

        if (!CanChange)
        {
            return List().Any(rule => string.Equals(rule.Name, ruleName, StringComparison.Ordinal))
                ? NeedsAdministrator()
                : NotFound();
        }

        try
        {
            dynamic policy = Policy();
            dynamic? ours = null;

            foreach (dynamic rule in policy.Rules)
            {
                if (IsOurs(rule) && string.Equals((string?)rule.Name, ruleName, StringComparison.Ordinal))
                {
                    ours = rule;
                    break;
                }
            }

            if (ours is null)
            {
                return NotFound();
            }

            FirewallRule removed = Read(ours);
            string unique = string.Concat("Windsock: removing ", Guid.NewGuid().ToString("N"));

            ours.Name = unique;
            policy.Rules.Remove(unique);

            return new FirewallResult(FirewallOutcome.Done, "Removed. The program can use the network again.", removed);
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return Failure(ex);
        }
    }

    /// <summary>Removes every Windsock rule, as should be done before uninstalling.</summary>
    public static IReadOnlyList<FirewallResult> RemoveAll() =>
        [.. List().Select(rule => Remove(rule.Name))];

    /// <summary>
    /// A rule's name: unique to what it blocks, since a rule added under a name
    /// already used replaces the first, and free of the bar character the
    /// firewall refuses in names.
    /// </summary>
    public static string RuleName(string displayName, string target) =>
        string.Format(CultureInfo.InvariantCulture, "Windsock: block {0} ({1})", Clean(displayName), Clean(target));

    private static string Clean(string text) => text.Replace('|', '/').Trim();

    private static string Description(string target) => string.Format(
        CultureInfo.InvariantCulture,
        "Added by Windsock on {0:yyyy-MM-dd} to stop {1} reaching the network. Remove it in Windsock, or in Windows Defender Firewall with Advanced Security (group Windsock).",
        DateTime.Now,
        target);

    private static FirewallResult Add(string name, string description, string path, string? serviceName)
    {
        if (!CanChange)
        {
            return NeedsAdministrator();
        }

        try
        {
            dynamic policy = Policy();
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;

            // Every property is set before Add: once a rule is in the
            // collection each set is committed and checked on its own.
            rule.Name = name;
            rule.Description = description;
            rule.Grouping = Group;
            rule.ApplicationName = path;

            if (serviceName is not null)
            {
                rule.ServiceName = serviceName;
            }

            rule.Direction = Outbound;
            rule.Action = BlockAction;
            rule.Profiles = AllProfiles;
            rule.Enabled = true;

            policy.Rules.Add(rule);

            return new FirewallResult(
                FirewallOutcome.Done,
                "Blocked. Windows Firewall now stops it reaching the network, whether or not Windsock is running.",
                new FirewallRule(name, serviceName is null ? path : null, serviceName, true, description));
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return Failure(ex);
        }
    }

    private static FirewallRule Read(dynamic rule)
    {
        string? service = rule.ServiceName;
        string? application = rule.ApplicationName;

        return new FirewallRule(
            (string)rule.Name,
            string.IsNullOrWhiteSpace(service) ? application : null,
            string.IsNullOrWhiteSpace(service) ? null : service,
            (bool)rule.Enabled,
            (string?)rule.Description);
    }

    private static bool IsOurs(dynamic rule) =>
        string.Equals((string?)rule.Grouping, Group, StringComparison.OrdinalIgnoreCase);

    private static FirewallResult NotFound() => new(
        FirewallOutcome.NotFound,
        "There is no Windsock rule of that name; it may have been removed in Windows already.");

    private static dynamic Policy() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;

    private static FirewallResult NeedsAdministrator() => new(
        FirewallOutcome.NeedsAdministrator,
        "Changing Windows Firewall needs administrator rights. Restart Windsock as administrator to do it.");

    private static FirewallResult Failure(Exception ex) =>
        ex.HResult == AccessDenied || ex is UnauthorizedAccessException
            ? NeedsAdministrator()
            : new FirewallResult(FirewallOutcome.Failed, string.Concat("Windows Firewall refused: ", ex.Message));

    private static bool IsComFailure(Exception ex) =>
        ex is COMException or UnauthorizedAccessException or InvalidCastException or ArgumentException
            or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or TypeLoadException;
}
