using Windsock.Core.Networking;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class NetworkInterfaceFilterTests
{
    private const uint Up = 1;
    private const uint Down = 2;
    private const uint NotPresent = 6;

    [Fact]
    public void ARealAdapterThatIsUp_Counts() =>
        Assert.True(IpHelperTotalsSource.Counts(Up, hardware: true, filter: false));

    [Fact]
    public void AFilterBoundToAnAdapter_DoesNot()
    {
        Assert.False(IpHelperTotalsSource.Counts(Up, hardware: false, filter: true));

        // Belt and braces: a row flagged as both is still a copy.
        Assert.False(IpHelperTotalsSource.Counts(Up, hardware: true, filter: true));
    }

    [Fact]
    public void AVirtualAdapter_DoesNot() =>
        Assert.False(IpHelperTotalsSource.Counts(Up, hardware: false, filter: false));

    [Theory]
    [InlineData(Down)]
    [InlineData(NotPresent)]
    public void AnAdapterThatIsNotUp_DoesNot(uint status) =>
        Assert.False(IpHelperTotalsSource.Counts(status, hardware: true, filter: false));

    [Fact]
    public void TheMachineTotal_MatchesTheAdaptersWindowsReports()
    {
        var source = new IpHelperTotalsSource();

        NetworkTotals first = source.ReadTotals();
        NetworkTotals second = source.ReadTotals();

        Assert.True(second.BytesReceived >= first.BytesReceived);
        Assert.True(second.BytesSent >= first.BytesSent);
    }
}
