using Windsock.Core.Security;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class SignatureTests
{
    private static readonly string System32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

    [Fact]
    public void AWindowsFileIsSignedThroughItsCatalogue()
    {
        // cmd.exe carries no signature of its own: Windows lists it in a catalogue.
        SignatureInfo cmd = new WinTrustVerifier().Verify(Path.Combine(System32, "cmd.exe"));

        Assert.Equal(SignatureState.Trusted, cmd.State);
        Assert.Equal(SignatureSource.Catalog, cmd.Source);
        Assert.Contains("Microsoft", cmd.Publisher, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmbeddedSignatureNamesItsSigner()
    {
        string dotnet = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet",
            "dotnet.exe");

        if (!File.Exists(dotnet))
        {
            return;
        }

        SignatureInfo signature = new WinTrustVerifier().Verify(dotnet);

        Assert.Equal(SignatureState.Trusted, signature.State);
        Assert.Equal(SignatureSource.Embedded, signature.Source);
        Assert.False(string.IsNullOrWhiteSpace(signature.Publisher));
    }

    [Fact]
    public void AProgramNobodySignedIsUnsigned()
    {
        // The test runner's own launcher is built here and never signed.
        SignatureInfo signature = new WinTrustVerifier().Verify(Environment.ProcessPath!);

        Assert.Equal(SignatureState.NoSignature, signature.State);
        Assert.Null(signature.Publisher);
        Assert.True(signature.IsFlagged(talksToInternet: true));
        Assert.False(signature.IsFlagged(talksToInternet: false));
    }

    [Fact]
    public void ASignedFileChangedAfterSigningIsNotTrusted()
    {
        string dotnet = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet",
            "dotnet.exe");

        if (!File.Exists(dotnet))
        {
            return;
        }

        string copy = Path.Combine(Path.GetTempPath(), "windsock-tampered-" + Guid.NewGuid().ToString("N") + ".exe");
        byte[] bytes = File.ReadAllBytes(dotnet);

        // A byte in the middle of the code: the signature still parses, the digest no longer matches.
        bytes[bytes.Length / 3] ^= 0xFF;
        File.WriteAllBytes(copy, bytes);

        try
        {
            SignatureInfo signature = new WinTrustVerifier().Verify(copy);

            Assert.Equal(SignatureState.NotTrusted, signature.State);
            Assert.True(signature.IsFlagged(talksToInternet: true));
            Assert.Contains("does not hold", signature.Summary, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x800B0109))]
    [InlineData(unchecked((int)0x800B010A))]
    public void AChainNotFinishedOfflineIsNeitherTrustedNorMarked(int code)
    {
        // Windsock never lets the check go online, so a chain it could not
        // finish from what the PC holds is said to be unconfirmed, not broken.
        SignatureInfo signature = new(SignatureState.Unconfirmed, "Contoso", SignatureSource.Embedded, code);

        Assert.False(signature.IsFlagged(talksToInternet: true));
        Assert.Contains("Contoso", signature.Summary, StringComparison.Ordinal);
        Assert.Contains("without going online", signature.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("does not hold", signature.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotThereClaimsNothing()
    {
        SignatureInfo signature = new WinTrustVerifier().Verify(Path.Combine(Path.GetTempPath(), "windsock-missing-" + Guid.NewGuid().ToString("N") + ".exe"));

        Assert.Equal(SignatureState.Unreadable, signature.State);
        Assert.False(signature.IsFlagged(talksToInternet: true));
    }

    [Theory]
    [InlineData(unchecked((int)0x80096010), "the file was changed after it was signed")]
    [InlineData(unchecked((int)0x800B0109), "it ends at a root this PC does not trust")]
    [InlineData(unchecked((int)0x800B0101), "the certificate has expired")]
    [InlineData(unchecked((int)0x800B0111), "the signer is explicitly distrusted")]
    public void FailuresAreSaidInWords(int code, string words) =>
        Assert.Equal(words, SignatureInfo.Reason(code));

    [Fact]
    public void AFileIsCheckedOncePerVersion()
    {
        CountingVerifier verifier = new();
        Dictionary<string, FileStamp> disk = new(StringComparer.OrdinalIgnoreCase) { [@"C:\a.exe"] = new FileStamp(100, 1) };
        using SignatureBook book = new(verifier, path => disk.TryGetValue(path, out FileStamp stamp) ? stamp : null);

        Assert.Equal(SignatureState.Trusted, book.Check(@"C:\a.exe").State);
        Assert.Equal(SignatureState.Trusted, book.Check(@"c:\A.EXE").State);
        Assert.Equal(1, verifier.Checks);

        // An update changes the file, and it is checked again.
        disk[@"C:\a.exe"] = new FileStamp(120, 2);
        Assert.Equal(SignatureState.Trusted, book.Check(@"C:\a.exe").State);
        Assert.Equal(2, verifier.Checks);

        Assert.Equal(SignatureState.Unreadable, book.Check(@"C:\gone.exe").State);
        Assert.Equal(2, verifier.Checks);
    }

    [Fact]
    public void AFileAskedAboutIsCheckedInTheBackground()
    {
        CountingVerifier verifier = new();
        using SignatureBook book = new(verifier, _ => new FileStamp(1, 1));
        using ManualResetEventSlim done = new();
        book.Checked += (_, e) =>
        {
            if (e.Path == @"C:\b.exe")
            {
                done.Set();
            }
        };

        Assert.Null(book.Find(@"C:\b.exe"));
        Assert.True(done.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(SignatureState.Trusted, book.Find(@"C:\b.exe")!.State);
    }

    [Fact]
    public void TheWorkerEndsWhenIdleAndStartsAgainForTheNextFile()
    {
        using SignatureBook book = new(new CountingVerifier(), _ => new FileStamp(1, 1));

        Assert.Null(book.Find(@"C:\c.exe"));
        Assert.True(SpinWait.SpinUntil(() => !book.IsWorking && book.Find(@"C:\c.exe") is not null, TimeSpan.FromSeconds(5)));

        // Idle, no thread waits; the next file starts one, and it is checked.
        Assert.False(book.IsWorking);
        Assert.Null(book.Find(@"C:\d.exe"));
        Assert.True(SpinWait.SpinUntil(() => book.Find(@"C:\d.exe") is not null, TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => !book.IsWorking, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void APageThatLeftIsNotKeptAliveByTheWorker()
    {
        // The live check found a closed window's whole scope kept by the
        // worker's stack, through the last handler it had called.
        using SignatureBook book = new(new CountingVerifier(), _ => new FileStamp(1, 1));
        WeakReference page = SubscribeCheckAndLeave(book);

        Assert.True(SpinWait.SpinUntil(() => !book.IsWorking, TimeSpan.FromSeconds(5)));

        for (int attempt = 0; attempt < 10 && page.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(page.IsAlive);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference SubscribeCheckAndLeave(SignatureBook book)
    {
        Listener page = new();
        book.Checked += page.OnChecked;

        Assert.Null(book.Find(@"C:\leaving.exe"));
        Assert.True(page.Heard.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        book.Checked -= page.OnChecked;
        page.Heard.Dispose();
        return new WeakReference(page);
    }

    private sealed class Listener
    {
        public ManualResetEventSlim Heard { get; } = new();

        public void OnChecked(object? sender, SignatureCheckedEventArgs e) => Heard.Set();
    }

    private sealed class CountingVerifier : ISignatureVerifier
    {
        private int _checks;

        public int Checks => _checks;

        public SignatureInfo Verify(string path)
        {
            Interlocked.Increment(ref _checks);
            return new SignatureInfo(SignatureState.Trusted, "Someone", SignatureSource.Embedded, 0);
        }
    }
}
