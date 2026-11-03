using System.Globalization;
using Windsock.Core.Startup;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class StartupExitTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 21, 4, 5, TimeSpan.FromHours(6));

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "Windsock-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void EveryReason_HasACodeOfItsOwn()
    {
        int[] codes =
        [
            StartupExit.Closed,
            StartupExit.HandedOff,
            StartupExit.LockRefused,
            StartupExit.FailedBeforeLogging,
            StartupExit.Unanswered,
        ];

        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.Equal(0, StartupExit.Closed);
    }

    [Fact]
    public void AKeptNote_IsALineInTheDataRoot()
    {
        string? kept = StartupExit.Note(_directory, StartupExit.Unanswered, "nobody answered\r\nat all", keep: true, Now);

        Assert.Equal(Path.Combine(_directory, StartupExit.NoteFile), kept);
        string line = Assert.Single(File.ReadAllLines(kept!));
        Assert.StartsWith("2026-10-05 21:04:05.000 +06:00", line, StringComparison.Ordinal);
        Assert.Contains("exit 5: nobody answered | at all", line, StringComparison.Ordinal);
        Assert.Contains("pid " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture), line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEverydayHandOff_LeavesNoFile()
    {
        Assert.Null(StartupExit.Note(_directory, StartupExit.HandedOff, "handed over", keep: false, Now));
        Assert.False(File.Exists(Path.Combine(_directory, StartupExit.NoteFile)));
    }

    [Fact]
    public void Notes_AddUp_UntilTheFileGrowsTooLarge()
    {
        string path = Path.Combine(_directory, StartupExit.NoteFile);

        StartupExit.Note(_directory, StartupExit.LockRefused, "one", keep: true, Now);
        StartupExit.Note(_directory, StartupExit.LockRefused, "two", keep: true, Now);
        Assert.Equal(2, File.ReadAllLines(path).Length);

        File.AppendAllText(path, new string('x', (int)StartupExit.LargestNote));
        StartupExit.Note(_directory, StartupExit.LockRefused, "three", keep: true, Now);

        string line = Assert.Single(File.ReadAllLines(path));
        Assert.Contains("three", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A data root that cannot be written - here a file where the folder should
    /// be - is passed over for the next place, never thrown from.
    /// </summary>
    [Fact]
    public void AnUnwritableRoot_IsPassedOver()
    {
        Directory.CreateDirectory(_directory);
        string notAFolder = Path.Combine(_directory, "file");
        File.WriteAllText(notAFolder, "not a folder");

        string? kept = StartupExit.Keep([notAFolder, _directory], "a line");

        Assert.Equal(Path.Combine(_directory, StartupExit.NoteFile), kept);
        Assert.Null(StartupExit.Keep([notAFolder], "a line"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
