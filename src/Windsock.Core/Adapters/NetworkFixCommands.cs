#if !STORE
using System.Globalization;

namespace Windsock.Core.Adapters;

/// <summary>The quick fixes support lines ask people to type.</summary>
public enum NetworkFix
{
    /// <summary>ipconfig /flushdns: forget every name looked up.</summary>
    FlushDns,

    /// <summary>ipconfig /renew: ask the DHCP server to renew one adapter's address.</summary>
    RenewAddress,

    /// <summary>Restart-NetAdapter: switch one adapter off and on again.</summary>
    ResetAdapter,
}

/// <summary>One command line for a fix.</summary>
/// <param name="Program">The program, a name Windows finds in System32.</param>
/// <param name="Arguments">Its arguments as one string.</param>
/// <param name="NeedsElevation">Whether it needs an administrator.</param>
/// <param name="DropsConnection">Whether the connection goes for a moment.</param>
public sealed record FixCommand(string Program, string Arguments, bool NeedsElevation, bool DropsConnection);

/// <summary>
/// Builds the command lines behind the quick fixes. Full edition only: the
/// Store edition never asks for elevation and offers none of them.
/// </summary>
/// <remarks>
/// <para>
/// Measured on 2026-10-05: <c>ipconfig /flushdns</c> succeeded unelevated on
/// Windows 11 build 26200 (older Windows needed an administrator). Renewing and
/// restarting were not run here, since either could drop the development
/// machine's connection; Windows documents both as administrator tasks.
/// </para>
/// <para>
/// Nothing a person can type reaches a command line: renewing names the adapter
/// in a quoted argument, and adapters whose names contain a quote are refused;
/// restarting names the adapter by its interface index, a number.
/// </para>
/// </remarks>
public static class NetworkFixCommands
{
    /// <summary>The command for a fix, or nothing when the adapter cannot be named safely.</summary>
    public static FixCommand? For(NetworkFix fix, AdapterDetails? adapter = null) => fix switch
    {
        NetworkFix.FlushDns => new FixCommand("ipconfig.exe", "/flushdns", NeedsElevation: false, DropsConnection: false),
        NetworkFix.RenewAddress when adapter is not null && IsSafeName(adapter.Name) =>
            new FixCommand("ipconfig.exe", string.Concat("/renew \"", adapter.Name, "\""), NeedsElevation: true, DropsConnection: false),
        NetworkFix.ResetAdapter when adapter is { Index: > 0 } =>
            new FixCommand(
                "powershell.exe",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"-NoProfile -NonInteractive -Command \"Get-NetAdapter -InterfaceIndex {adapter.Index} | Restart-NetAdapter -Confirm:$false\""),
                NeedsElevation: true,
                DropsConnection: true),
        _ => null,
    };

    /// <summary>Whether a name can go inside a quoted argument without ending it or reaching a shell.</summary>
    public static bool IsSafeName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 256
        && name.IndexOfAny(['"', '\0', '\r', '\n', '%', '^', '&', '|', '<', '>']) < 0;

    /// <summary>What a fix does, in a sentence the page shows before it runs.</summary>
    public static string Explain(NetworkFix fix) => fix switch
    {
        NetworkFix.FlushDns => "Clears the names Windows has looked up, so the next visit to each site asks again. Harmless; nothing disconnects.",
        NetworkFix.RenewAddress => "Asks the router to renew this adapter's address. Usually nothing disconnects; Windows asks for administrator permission.",
        _ => "Switches this adapter off and on again. The connection drops for a few seconds; Windows asks for administrator permission.",
    };
}
#endif
