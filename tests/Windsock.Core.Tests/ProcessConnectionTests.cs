using Windsock.Core.Processes;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class ProcessConnectionTests
{
    private static ProcessConnection Tcp(
        string local = "192.168.0.10",
        int localPort = 51000,
        string? remote = "142.250.187.196",
        int remotePort = 443,
        TcpConnectionState state = TcpConnectionState.Established) =>
        new(1234, TransportProtocol.Tcp, IsIpv6: false, local, localPort, remote, remotePort, state, null);

    [Fact]
    public void TheSameSocket_KeepsTheSameKeyBetweenReadings()
    {
        Assert.Equal(Tcp().Key, Tcp(state: TcpConnectionState.CloseWait).Key);
    }

    [Theory]
    [InlineData("192.168.0.11", 51000, 443)]
    [InlineData("192.168.0.10", 51001, 443)]
    [InlineData("192.168.0.10", 51000, 80)]
    public void ADifferentSocket_GetsADifferentKey(string local, int localPort, int remotePort) =>
        Assert.NotEqual(Tcp().Key, Tcp(local, localPort, remotePort: remotePort).Key);

    [Fact]
    public void ATcpListener_IsListeningRatherThanTalking() =>
        Assert.True(Tcp(state: TcpConnectionState.Listening).IsListening);

    [Fact]
    public void AnEstablishedConnection_IsNot() =>
        Assert.False(Tcp().IsListening);

    [Fact]
    public void ABoundUdpSocket_CountsAsListening()
    {
        ProcessConnection udp = new(
            1234, TransportProtocol.Udp, IsIpv6: false, "0.0.0.0", 5353, null, 0, TcpConnectionState.Unknown, null);

        Assert.True(udp.IsListening);
    }
}
