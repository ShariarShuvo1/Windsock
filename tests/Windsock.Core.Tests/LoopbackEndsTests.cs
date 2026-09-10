using Windsock.Core.Processes;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class LoopbackEndsTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.5.6.7", true)]
    [InlineData("::1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("192.168.0.14", false)]
    [InlineData("::", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void KnowsWhatIsThisMachine(string? address, bool loopback) =>
        Assert.Equal(loopback, LoopbackEnds.IsLoopback(address));

    [Fact]
    public void NamesTheProcessHoldingThePortAConnectionIsAimedAt()
    {
        LoopbackEnds ends = new(
        [
            Talking(20880, 58748, 49761),
            Talking(4172, 49761, 58748),
        ]);

        Assert.Equal(4172, ends.Owner(TransportProtocol.Tcp, ipv6: false, 49761));
        Assert.Equal(20880, ends.Owner(TransportProtocol.Tcp, ipv6: false, 58748));
    }

    [Fact]
    public void FallsBackToWhoeverIsListening()
    {
        LoopbackEnds ends = new(
        [
            Talking(20880, 58747, 1337),
            Listening(9001, 1337),
        ]);

        Assert.Equal(9001, ends.Owner(TransportProtocol.Tcp, ipv6: false, 1337));
    }

    [Fact]
    public void PrefersTheConversationToTheOfferOfOne()
    {
        LoopbackEnds ends = new(
        [
            Listening(9001, 8080),
            Talking(4242, 8080, 58750),
        ]);

        Assert.Equal(4242, ends.Owner(TransportProtocol.Tcp, ipv6: false, 8080));
    }

    [Fact]
    public void ListenersOnEveryAddressCountToo()
    {
        // A server on 0.0.0.0 is reached at 127.0.0.1 like any other.
        LoopbackEnds ends = new([Listening(9001, 1042, "0.0.0.0")]);

        Assert.Equal(9001, ends.Owner(TransportProtocol.Tcp, ipv6: false, 1042));
    }

    [Fact]
    public void ASocketOutOnTheNetworkIsNotAFarEnd()
    {
        LoopbackEnds ends = new(
        [
            new ProcessConnection(
                20880,
                TransportProtocol.Tcp,
                IsIpv6: false,
                "192.168.0.14",
                58748,
                "142.250.187.196",
                443,
                TcpConnectionState.Established,
                null),
        ]);

        Assert.Null(ends.Owner(TransportProtocol.Tcp, ipv6: false, 58748));
    }

    [Fact]
    public void AProtocolIsNotTheOther()
    {
        LoopbackEnds ends = new([Talking(4172, 49761, 58748)]);

        Assert.Equal(4172, ends.Owner(TransportProtocol.Tcp, ipv6: false, 49761));
        Assert.Null(ends.Owner(TransportProtocol.Udp, ipv6: false, 49761));
    }

    [Fact]
    public void AnIpv6ListenerAnswersAnIpv4Caller()
    {
        LoopbackEnds ends = new([Listening(9001, 5000, "::", ipv6: true)]);

        Assert.Equal(9001, ends.Owner(TransportProtocol.Tcp, ipv6: false, 5000));
    }

    [Fact]
    public void NobodyHoldingThePortMeansNoAnswer()
    {
        LoopbackEnds ends = new([Talking(20880, 58748, 49761)]);

        Assert.Null(ends.Owner(TransportProtocol.Tcp, ipv6: false, 65000));
    }

    [Fact]
    public void UdpIsAnsweredByWhoeverIsBoundToThePort()
    {
        LoopbackEnds ends = new(
        [
            new ProcessConnection(
                7100,
                TransportProtocol.Udp,
                IsIpv6: false,
                "127.0.0.1",
                53,
                null,
                0,
                TcpConnectionState.Unknown,
                null),
        ]);

        Assert.Equal(7100, ends.Owner(ipv6: false, 53));
    }

    private static ProcessConnection Talking(int processId, int localPort, int remotePort) =>
        new(
            processId,
            TransportProtocol.Tcp,
            IsIpv6: false,
            "127.0.0.1",
            localPort,
            "127.0.0.1",
            remotePort,
            TcpConnectionState.Established,
            null);

    private static ProcessConnection Listening(
        int processId,
        int port,
        string address = "127.0.0.1",
        bool ipv6 = false) =>
        new(
            processId,
            TransportProtocol.Tcp,
            ipv6,
            address,
            port,
            null,
            0,
            TcpConnectionState.Listening,
            null);
}
