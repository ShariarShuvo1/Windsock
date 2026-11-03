using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace Windsock.Core.Insight;

/// <summary>How asking to end one process went.</summary>
public enum EndOutcome
{
    /// <summary>It has ended.</summary>
    Ended,

    /// <summary>It could be ended; nothing was done (a probe).</summary>
    CanEnd,

    /// <summary>It had already closed, or its id now belongs to another process, which was left alone.</summary>
    AlreadyGone,

    /// <summary>It runs as another account or with higher rights, so only an administrator may end it.</summary>
    NeedsAdministrator,

    /// <summary>Windows itself, or Windsock: never offered.</summary>
    NotAllowed,

    /// <summary>Asked to end, and still running when Windsock stopped waiting.</summary>
    StillRunning,

    /// <summary>Windows refused for another reason.</summary>
    Failed,

    /// <summary>
    /// Windsock could not tell when it started, so it cannot be sure the id
    /// still belongs to the process that was asked for; it was left alone.
    /// </summary>
    NotConfirmed,
}

/// <summary>A process somebody asked to end, as it was seen when they asked.</summary>
/// <param name="ProcessId">Its id.</param>
/// <param name="Name">Its name, for the result.</param>
/// <param name="StartedAt">
/// When it started. A process that started at another time holds a reused id
/// and is not the one that was asked for, so it is left alone; without a start
/// time nothing is ended at all.
/// </param>
/// <param name="Path">Its file, for saying exactly what will be ended.</param>
public sealed record EndTarget(int ProcessId, string Name, DateTimeOffset? StartedAt, string? Path = null);

/// <summary>What happened to one process.</summary>
public sealed record EndResult(EndTarget Target, EndOutcome Outcome, string Message);

/// <summary>
/// Ends processes for the person, from the Apps page's row: theirs without
/// administrator rights, anyone else's only while Windsock runs elevated.
/// </summary>
/// <remarks>
/// <para>
/// Windows grants a standard user exactly the processes they could open for
/// querying: their own, at their own integrity or lower. Measured on
/// 2026-10-05: 231 of 408 processes could be opened to end, 177 refused with
/// access denied - every service host but the user's own, the security
/// processes, vendor services, and an elevated process of the same user. A
/// refusal comes back as <see cref="EndOutcome.NeedsAdministrator"/>, said
/// plainly, so the page can explain rather than show a button that fails.
/// </para>
/// <para>
/// Ending is unconditional: the program gets no chance to save anything. A
/// service ended this way is restarted or not by its own recovery settings.
/// The page confirms first; this does not ask again.
/// </para>
/// </remarks>
public static class ProcessEnder
{
    private const int Gone = 87;            // ERROR_INVALID_PARAMETER: no such process
    private const int AccessDenied = 5;     // ERROR_ACCESS_DENIED

    /// <summary>How far a start time may differ and still be the same process.</summary>
    private static readonly TimeSpan SameStart = TimeSpan.FromMilliseconds(20);

    /// <summary>The longest <see cref="End"/> waits, whatever it is asked.</summary>
    private static readonly TimeSpan MostPatience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether a process could be ended now, without ending it: the handle is
    /// opened and closed again. For deciding what the row offers.
    /// </summary>
    public static EndResult Probe(EndTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!TryOpen(target, out SafeFileHandle? handle, out EndResult? refused))
        {
            return refused;
        }

        handle.Dispose();
        return new EndResult(target, EndOutcome.CanEnd, "It can be ended.");
    }

    /// <summary>
    /// Ends each process, then waits for them to be gone - up to
    /// <paramref name="patience"/> for all of them together, not each - and
    /// says what happened to every one. Blocks for that long: call it off the
    /// window's thread.
    /// </summary>
    public static IReadOnlyList<EndResult> End(IEnumerable<EndTarget> targets, TimeSpan patience)
    {
        ArgumentNullException.ThrowIfNull(targets);

        long deadline = Environment.TickCount64 + (long)Math.Clamp(patience.TotalMilliseconds, 0, MostPatience.TotalMilliseconds);
        List<EndResult?> results = [];
        List<(int Index, EndTarget Target, SafeFileHandle Handle)> asked = [];

        try
        {
            // Every one is asked first, so one slow to close does not hold up
            // the rest; then the wait is shared.
            foreach (EndTarget target in targets)
            {
                ArgumentNullException.ThrowIfNull(target);

                if (!TryOpen(target, out SafeFileHandle? handle, out EndResult? refused))
                {
                    results.Add(refused);
                    continue;
                }

                if (Terminate(target, handle) is { } failed)
                {
                    handle.Dispose();
                    results.Add(failed);
                    continue;
                }

                asked.Add((results.Count, target, handle));
                results.Add(null);
            }

            foreach ((int index, EndTarget target, SafeFileHandle handle) in asked)
            {
                TimeSpan left = TimeSpan.FromMilliseconds(Math.Max(0, deadline - Environment.TickCount64));

                results[index] = Exited(handle, left)
                    ? new EndResult(target, EndOutcome.Ended, "It has ended.")
                    : new EndResult(target, EndOutcome.StillRunning, "Asked to end, and still closing.");
            }
        }
        finally
        {
            foreach ((_, _, SafeFileHandle handle) in asked)
            {
                handle.Dispose();
            }
        }

        return [.. results.Select(static each => each!)];
    }

    /// <summary>Whether a process is one never offered, by id and name alone.</summary>
    public static bool IsNeverOffered(int processId, string? name) =>
        processId <= 4
        || processId == Environment.ProcessId
        || EssentialProcesses.IsEssential(name);

    /// <summary>
    /// Opens the process to end it, after every check: not Windows or Windsock
    /// by the name given and by the file the handle itself runs, and the same
    /// process that was asked for by its start time. True with the handle
    /// open, or false with the reason it was left alone.
    /// </summary>
    private static bool TryOpen(
        EndTarget target,
        [NotNullWhen(true)] out SafeFileHandle? handle,
        [NotNullWhen(false)] out EndResult? refused)
    {
        handle = null;
        refused = Check(target, out SafeFileHandle? opened);

        if (refused is not null)
        {
            return false;
        }

        handle = opened!;
        return true;
    }

    private static EndResult? Check(EndTarget target, out SafeFileHandle? handle)
    {
        handle = null;

        if (IsNeverOffered(target.ProcessId, target.Name))
        {
            return NotAllowed(target);
        }

        PROCESS_ACCESS_RIGHTS rights = PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE
            | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION
            | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE;

        SafeFileHandle opened = PInvoke.OpenProcess_SafeHandle(rights, false, (uint)target.ProcessId);

        if (opened.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            opened.Dispose();

            return error switch
            {
                AccessDenied => new EndResult(
                    target,
                    EndOutcome.NeedsAdministrator,
                    "It runs as another account or with higher rights, so only an administrator can end it."),
                Gone => new EndResult(target, EndOutcome.AlreadyGone, "It had already closed."),
                _ => new EndResult(target, EndOutcome.Failed, string.Concat("Windows refused (error ", error.ToString(CultureInfo.InvariantCulture), ").")),
            };
        }

        // The name a row gives can be anything ("PID 1234", a service's
        // name); the file the handle itself runs is what decides.
        if (EssentialProcesses.IsEssential(ImageOf(opened)))
        {
            opened.Dispose();
            return NotAllowed(target);
        }

        if (target.StartedAt is not { } expected || Started(opened) is not { } actual)
        {
            opened.Dispose();
            return new EndResult(
                target,
                EndOutcome.NotConfirmed,
                "Windsock could not tell when it started, so it cannot be sure this id still belongs to it. It was left alone.");
        }

        if ((actual - expected).Duration() > SameStart)
        {
            opened.Dispose();
            return new EndResult(target, EndOutcome.AlreadyGone, "It had already closed; another process now has its id, and was left alone.");
        }

        handle = opened;
        return null;
    }

    /// <summary>Asks Windows to end it: null once asked, or why it would not.</summary>
    private static EndResult? Terminate(EndTarget target, SafeFileHandle handle)
    {
        if (PInvoke.TerminateProcess(handle, 1))
        {
            return null;
        }

        int error = Marshal.GetLastPInvokeError();

        // Terminating a process that is already on its way out is refused
        // with access denied; it is going regardless.
        if (Exited(handle, TimeSpan.Zero))
        {
            return new EndResult(target, EndOutcome.Ended, "It has ended.");
        }

        return error == AccessDenied
            ? new EndResult(target, EndOutcome.NeedsAdministrator, "Windows would not let it be ended without administrator rights.")
            : new EndResult(target, EndOutcome.Failed, string.Concat("Windows refused (error ", error.ToString(CultureInfo.InvariantCulture), ")."));
    }

    private static EndResult NotAllowed(EndTarget target) => new(
        target,
        EndOutcome.NotAllowed,
        target.ProcessId == Environment.ProcessId
            ? "This is Windsock itself."
            : "This is part of Windows; ending it would stop the PC.");

    private static bool Exited(SafeFileHandle handle, TimeSpan patience) =>
        PInvoke.WaitForSingleObject(handle, (uint)Math.Clamp(patience.TotalMilliseconds, 0, MostPatience.TotalMilliseconds)) == WAIT_EVENT.WAIT_OBJECT_0;

    /// <summary>The file the process runs, from its own handle, or null.</summary>
    private static string? ImageOf(SafeFileHandle handle)
    {
        Span<char> buffer = stackalloc char[1024];
        uint length = (uint)buffer.Length;

        return PInvoke.QueryFullProcessImageName(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref length)
            && length > 0 && length <= buffer.Length
            ? buffer[..(int)length].ToString()
            : null;
    }

    private static DateTimeOffset? Started(SafeFileHandle handle)
    {
        if (!PInvoke.GetProcessTimes(handle, out FILETIME created, out _, out _, out _))
        {
            return null;
        }

        long ticks = ((long)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
        return ticks > 0 ? DateTimeOffset.FromFileTime(ticks) : null;
    }
}
