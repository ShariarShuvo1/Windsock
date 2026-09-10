using Windsock.Core.Processes;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class PortNamesTests
{
    [Theory]
    [InlineData(443, "443 (https)")]
    [InlineData(80, "80 (http)")]
    [InlineData(53, "53 (dns)")]
    [InlineData(22, "22 (ssh)")]
    public void AWellKnownPort_CarriesItsService(int port, string expected) =>
        Assert.Equal(expected, PortNames.Describe(port));

    [Theory]
    [InlineData(51234)]
    [InlineData(0)]
    [InlineData(65535)]
    public void AnEphemeralPort_IsJustTheNumber(int port)
    {
        Assert.False(PortNames.IsKnown(port));
        Assert.DoesNotContain("(", PortNames.Describe(port), StringComparison.Ordinal);
    }
}
