#if !STORE
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Windsock.Core.Adapters;
using Windsock.Core.Startup;

namespace Windsock.App.Services.Network;

/// <summary>How a fix went.</summary>
/// <param name="Fix">Which fix.</param>
/// <param name="Succeeded">Whether the command reported success.</param>
/// <param name="Message">What to tell the person.</param>
public sealed record FixResult(NetworkFix Fix, bool Succeeded, string Message);

/// <summary>
/// Runs the quick fixes - flush DNS, renew an address, restart an adapter - on
/// the person's click, asking Windows for administrator permission for just
/// that command when it needs it. Full edition only.
/// </summary>
/// <remarks>
/// A command run elevated from an unelevated Windsock goes through UAC with
/// ShellExecute, which cannot capture its output, so its result is its exit
/// code alone; the page then reads the adapters again to show what changed.
/// Declining the UAC prompt is reported as such, not as a failure of the fix.
/// None of these were run on the development machine apart from reasoning:
/// renewing and restarting could have dropped its connection.
/// </remarks>
public sealed partial class NetworkFixes(ILogger<NetworkFixes> logger)
{
    private const int Cancelled = 1223;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>Runs a fix.</summary>
    /// <param name="fix">Which.</param>
    /// <param name="adapter">The adapter, for renewing and restarting.</param>
    /// <param name="cancellationToken">Stops waiting; the command itself carries on.</param>
    public async Task<FixResult> RunAsync(NetworkFix fix, AdapterDetails? adapter, CancellationToken cancellationToken = default)
    {
        if (NetworkFixCommands.For(fix, adapter) is not { } command)
        {
            return new FixResult(fix, false, "That adapter cannot be named to Windows safely, so nothing was run.");
        }

        bool elevated = Elevation.IsElevated();
        bool viaUac = command.NeedsElevation && !elevated;

        ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, command.Program), command.Arguments)
        {
            UseShellExecute = viaUac,
            Verb = viaUac ? "runas" : string.Empty,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = !viaUac,
            RedirectStandardError = !viaUac,
        };

        if (command.Program == "powershell.exe")
        {
            start.FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        }

        try
        {
            using Process? process = Process.Start(start);

            if (process is null)
            {
                return new FixResult(fix, false, "Windows did not start the command.");
            }

            // Both streams are read as they fill: a redirected stream nobody
            // reads stops the command once its pipe buffer is full.
            Task<string> output = viaUac ? Task.FromResult(string.Empty) : process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errors = viaUac ? Task.FromResult(string.Empty) : process.StandardError.ReadToEndAsync(cancellationToken);

            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(Patience);
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            string said = (await output.ConfigureAwait(false)).Trim();
            string complained = (await errors.ConfigureAwait(false)).Trim();

            if (said.Length == 0)
            {
                said = complained;
            }

            bool succeeded = process.ExitCode == 0;
            LogRan(fix, process.ExitCode);

            return new FixResult(fix, succeeded, succeeded ? Done(fix) : string.Concat("It did not work", said.Length > 0 ? string.Concat(": ", said) : "."));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
            return new FixResult(fix, false, "Administrator permission was not given, so nothing was changed.");
        }
        catch (Win32Exception ex)
        {
            LogFailed(fix, ex);
            return new FixResult(fix, false, "Windows could not run the command.");
        }
        catch (OperationCanceledException)
        {
            return new FixResult(fix, false, "It is taking a while; check the adapter's state in a moment.");
        }
    }

    private static string Done(NetworkFix fix) => fix switch
    {
        NetworkFix.FlushDns => "Windows' DNS cache was cleared.",
        NetworkFix.RenewAddress => "The address was renewed.",
        _ => "The adapter was restarted.",
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Network fix {Fix} exited with {Code}")]
    private partial void LogRan(NetworkFix fix, int code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Network fix {Fix} could not start")]
    private partial void LogFailed(NetworkFix fix, Exception error);
}
#endif
