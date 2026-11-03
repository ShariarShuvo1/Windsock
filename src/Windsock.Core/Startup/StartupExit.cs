using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Windsock.Core.Startup;

/// <summary>
/// Why a Windsock process ended before it ran, as its exit code, and the note
/// it leaves when the reason is not the everyday one.
/// </summary>
/// <remarks>
/// <para>
/// Every way out of a start that ends before the log is open used to end with
/// 0 and say nothing anywhere, so a process seen to leave half a second after
/// starting (2026-10-05, a Store build on a fresh data root) could not be told
/// apart from a second launch handing over. Each now has its own code, written
/// to the debugger output too (<see cref="Trace"/>); every one but the
/// everyday hand-off also leaves a line in <see cref="NoteFile"/>, in the data
/// root when it can be written, else in the temp folder.
/// </para>
/// <para>
/// Not here: a Velopack hook run (<c>--veloapp-install</c> and the like) ends
/// inside <c>VelopackApp.Run</c> with Velopack's own code, which its
/// Update.exe reads as the hook's result and so is left alone; a failure
/// loading App.xaml, before any of Windsock's code runs, crashes with the
/// runtime's code and a record in Windows' Application log. A Windsock that
/// ran ends with 0, whether quit or restarted into a replacement.
/// </para>
/// </remarks>
public static class StartupExit
{
    /// <summary>Ran, and was closed - or restarted into a replacement.</summary>
    public const int Closed = 0;

    /// <summary>
    /// A Windsock was already running on this data root and answered: it was
    /// asked to show itself, or the page named. The everyday second launch.
    /// </summary>
    public const int HandedOff = 2;

    /// <summary>
    /// The single-instance lock could not be opened at all - made by a
    /// Windsock running as administrator, or under another account's rights.
    /// The running one was asked to show itself anyway.
    /// </summary>
    public const int LockRefused = 3;

    /// <summary>
    /// Something threw before the log was open: in Velopack's start, the
    /// single-instance lock, the data folder or the log itself.
    /// </summary>
    public const int FailedBeforeLogging = 4;

    /// <summary>
    /// A Windsock held the lock but did not answer the hand-off, and did not let
    /// go either within the wait - starting slowly, or hung. Its show-yourself
    /// event was set, which a Windsock still starting acts on.
    /// </summary>
    public const int Unanswered = 5;

    /// <summary>The file the notes go in, in the data root.</summary>
    public const string NoteFile = "startup-failure.txt";

    /// <summary>Past this size the file starts again, so notes never pile up.</summary>
    internal const long LargestNote = 64 * 1024;

    /// <summary>
    /// Says why this process is leaving: to the debugger output always, and,
    /// when <paramref name="keep"/>, as a line in <see cref="NoteFile"/>.
    /// Never throws; returns where the line was kept, if it was.
    /// </summary>
    public static string? Note(string root, int code, string reason, bool keep, DateTimeOffset now)
    {
        string line = Line(code, reason, now);

        try
        {
            Trace.WriteLine("Windsock: " + line);
        }
#pragma warning disable CA1031 // Saying why must never be a new reason to fail.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return keep ? Keep([root, Path.GetTempPath()], line) : null;
    }

    /// <summary>Appends the line to the note in the first of the folders that takes it; where, or nothing.</summary>
    internal static string? Keep(IEnumerable<string> folders, string line)
    {
        foreach (string folder in folders)
        {
            if (KeepIn(folder, line) is { } path)
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>One line: when, which process, the code and why, with the command line.</summary>
    internal static string Line(int code, string reason, DateTimeOffset now)
    {
        string arguments = string.Join(' ', Environment.GetCommandLineArgs().Skip(1));
        string flat = reason.ReplaceLineEndings(" | ");

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} pid {Environment.ProcessId} exit {code}: {flat} [args: {arguments}]");
    }

    private static string? KeepIn(string folder, string line)
    {
        try
        {
            if (string.IsNullOrEmpty(folder))
            {
                return null;
            }

            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, NoteFile);
            FileInfo existing = new(path);
            bool startAgain = existing.Exists && existing.Length > LargestNote;

            using FileStream stream = new(path, startAgain ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
            stream.Write(bytes);
            return path;
        }
#pragma warning disable CA1031 // A folder that cannot be written is the next folder's turn.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
