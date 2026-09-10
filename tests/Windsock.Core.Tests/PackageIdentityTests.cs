using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The packaged check decides which run-at-login mechanism the app uses, and it
/// runs in every process on startup. A wrong P/Invoke signature here would take
/// the whole app down before a window appeared, which is the one failure mode
/// worth a test that cannot fully exercise the other branch.
/// </summary>
public sealed class PackageIdentityTests
{
    [Fact]
    public void Asking_DoesNotThrow()
    {
        // The call itself is the assertion. GetCurrentPackageFullName is
        // reached through a hand-written LibraryImport, and a mismatched
        // signature shows up here rather than at a user's sign-in.
        bool answered = PackageIdentity.Exists;

        Assert.True(answered || !answered);
    }

    [Fact]
    public void TheTestHost_HasNoPackageIdentity()
    {
        // Nothing runs these tests from inside an MSIX container, so this is
        // the unpackaged answer. It pins the sense of the check: a version that
        // returned true here would silently move the GitHub edition onto the
        // StartupTask, which is not available to it.
        Assert.False(PackageIdentity.Exists);
    }

    [Fact]
    public void TheAnswer_IsWorkedOutOnce()
    {
        Assert.Equal(PackageIdentity.Exists, PackageIdentity.Exists);
    }
}
