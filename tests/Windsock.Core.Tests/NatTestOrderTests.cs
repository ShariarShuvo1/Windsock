using System.Buffers.Binary;
using System.Net;
using Windsock.Core.Lookups;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The NAT test against a simulated NAT and STUN server: the order of the
/// tests, and what happens when the socket cannot send.
/// </summary>
public sealed class NatTestOrderTests
{
    private static readonly IPAddress Primary = IPAddress.Parse("198.51.100.1");
    private static readonly IPAddress Alternate = IPAddress.Parse("198.51.100.2");
    private const int Port = 3478;
    private const int OtherPort = 3479;

    [Fact]
    public async Task AddressDependentFiltering_IsNotCalledOpen()
    {
        // Sending to the alternate address before the filtering tests would open
        // the NAT to it, and the change-address answer would get through.
        SimulatedNat nat = new(Filter.AddressDependent);

        NatTestResult result = await StunNatTest.RunAsync(nat, "test:3478", Primary, Port, TestContext.Current.CancellationToken);

        Assert.Equal(NatMapping.EndpointIndependent, result.Mapping);
        Assert.Equal(NatFiltering.AddressDependent, result.Filtering);
        Assert.Equal("Moderate", result.Summary);
        Assert.Null(result.Problem);
    }

    [Theory]
    [InlineData(Filter.EndpointIndependent, NatFiltering.EndpointIndependent, "Open")]
    [InlineData(Filter.AddressAndPortDependent, NatFiltering.AddressAndPortDependent, "Moderate")]
    public async Task OtherFilters_AreToldApart(Filter filter, NatFiltering expected, string summary)
    {
        NatTestResult result = await StunNatTest.RunAsync(new SimulatedNat(filter), "test:3478", Primary, Port, TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Filtering);
        Assert.Equal(summary, result.Summary);
    }

    [Fact]
    public async Task ASocketThatCannotSend_EndsTheTestWithAProblem()
    {
        SimulatedNat nat = new(Filter.EndpointIndependent) { SendFails = true };

        NatTestResult result = await StunNatTest.RunAsync(nat, "test:3478", Primary, Port, TestContext.Current.CancellationToken);

        Assert.Equal(StunNatTest.Unreachable, result.Problem);
        Assert.Equal(NatMapping.Unknown, result.Mapping);
        Assert.Equal(1, nat.Sends);
    }

    [Fact]
    public async Task SendingThatFailsPartWay_StopsAndSaysSo()
    {
        SimulatedNat nat = new(Filter.EndpointIndependent) { FailAfter = 1 };

        NatTestResult result = await StunNatTest.RunAsync(nat, "test:3478", Primary, Port, TestContext.Current.CancellationToken);

        Assert.Equal(StunNatTest.Unreachable, result.Problem);
        Assert.Equal(NatFiltering.Unknown, result.Filtering);
        Assert.Equal(2, nat.Sends);
    }

    public enum Filter
    {
        EndpointIndependent,
        AddressDependent,
        AddressAndPortDependent,
    }

    /// <summary>An endpoint-independent mapping NAT with the given filter, in front of an RFC 5780 server.</summary>
    private sealed class SimulatedNat(Filter filter) : IStunChannel
    {
        private static readonly IPEndPoint Outside = new(IPAddress.Parse("203.0.113.9"), 40000);
        private readonly HashSet<IPEndPoint> _sentTo = [];

        public bool SendFails { get; init; }

        public int FailAfter { get; init; } = int.MaxValue;

        public int Sends { get; private set; }

        public bool Failed { get; private set; }

        public bool Refused => false;

        public Task<byte[]?> ExchangeAsync(byte[] request, IPEndPoint to, byte[] transaction, TimeSpan wait, CancellationToken cancellationToken)
        {
            Sends++;

            if (SendFails || Sends > FailAfter)
            {
                Failed = true;
                return Task.FromResult<byte[]?>(null);
            }

            _sentTo.Add(to);

            (bool changeAddress, bool changePort) = ChangeOf(request);
            IPEndPoint from = new(changeAddress ? Other(to.Address) : to.Address, changePort ? OtherPortOf(to.Port) : to.Port);

            bool admitted = filter switch
            {
                Filter.EndpointIndependent => true,
                Filter.AddressDependent => _sentTo.Any(each => each.Address.Equals(from.Address)),
                _ => _sentTo.Contains(from),
            };

            return Task.FromResult(admitted ? Answer(transaction) : null);
        }

        public IPEndPoint? LocalTowards(IPAddress server) => new(IPAddress.Parse("192.168.1.20"), 50000);

        private static IPAddress Other(IPAddress address) => address.Equals(Primary) ? Alternate : Primary;

        private static int OtherPortOf(int port) => port == Port ? OtherPort : Port;

        private static (bool Address, bool Port) ChangeOf(byte[] request)
        {
            if (request.Length < 28)
            {
                return (false, false);
            }

            uint flags = BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(24));
            return ((flags & 0x4) != 0, (flags & 0x2) != 0);
        }

        private static byte[] Answer(byte[] transaction)
        {
            byte[] mapped = Attribute(StunMessage.MappedAddress, Outside);
            byte[] other = Attribute(StunMessage.OtherAddress, new IPEndPoint(Alternate, OtherPort));
            byte[] message = new byte[20 + mapped.Length + other.Length];

            BinaryPrimitives.WriteUInt16BigEndian(message, StunMessage.BindingSuccess);
            BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)(mapped.Length + other.Length));
            BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), StunMessage.Cookie);
            transaction.CopyTo(message, 8);
            mapped.CopyTo(message, 20);
            other.CopyTo(message, 20 + mapped.Length);
            return message;
        }

        private static byte[] Attribute(ushort kind, IPEndPoint endpoint)
        {
            byte[] attribute = new byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(attribute, kind);
            BinaryPrimitives.WriteUInt16BigEndian(attribute.AsSpan(2), 8);
            attribute[5] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(attribute.AsSpan(6), (ushort)endpoint.Port);
            endpoint.Address.GetAddressBytes().CopyTo(attribute, 8);
            return attribute;
        }
    }
}
