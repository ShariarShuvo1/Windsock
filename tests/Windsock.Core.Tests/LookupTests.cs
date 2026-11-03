using System.Buffers.Binary;
using System.Net;
using Windsock.Core.Asn;
using Windsock.Core.Lookups;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>DNS, NAT-PMP and STUN messages against their RFCs, the public address parser, and the offline AS table.</summary>
public sealed class LookupTests
{
    [Fact]
    public void DnsQuery_ForTheRoot_IsTheRfc1035Bytes()
    {
        byte[] query = DnsPacket.Query(0xBEEF, ".", DnsPacket.NameServers);

        Assert.Equal(new byte[] { 0xBE, 0xEF, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0x00, 0x00, 0x02, 0x00, 0x01 }, query);
    }

    [Fact]
    public void DnsQuery_ForAName_WritesLabels()
    {
        byte[] query = DnsPacket.Query(1, "www.example.com", DnsPacket.FourAddress);

        Assert.Equal(new byte[] { 3, (byte)'w', (byte)'w', (byte)'w', 7 }, query[12..17]);
        Assert.Equal(new byte[] { 0, 0x00, 0x01, 0x00, 0x01 }, query[^5..]);
        Assert.Throws<ArgumentException>(() => DnsPacket.Query(1, "bad..name", 1));
    }

    [Fact]
    public void DnsReply_ReadsIdCodeAndAnswers()
    {
        byte[] reply = [0xBE, 0xEF, 0x81, 0x80, 0, 1, 0, 13, 0, 0, 0, 0];

        Assert.Equal(new DnsReply(0xBEEF, 0, 13), DnsPacket.Read(reply));
        Assert.Equal(3, DnsPacket.Read([0, 1, 0x81, 0x83, 0, 1, 0, 0, 0, 0, 0, 0])!.Value.ResponseCode);
        Assert.Null(DnsPacket.Read([0, 1, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void Median_OfEvenAndOdd()
    {
        Assert.Equal(2, DnsTimer.Median([3, 1, 2]));
        Assert.Equal(2.5, DnsTimer.Median([4, 1, 2, 3]));
        Assert.Null(DnsTimer.Median([]));
    }

    [Fact]
    public void NatPmp_RequestAndAnswer_FollowRfc6886()
    {
        Assert.Equal(new byte[] { 0, 0 }, NatPmp.Request.ToArray());

        byte[] answer = [0, 128, 0, 0, 0, 0, 0x1C, 0x20, 100, 72, 1, 2];
        RouterOutside read = NatPmp.Read(answer)!;

        Assert.True(read.Answered);
        Assert.Equal(IPAddress.Parse("100.72.1.2"), read.Address);
        Assert.Equal(2, NatPmp.Read([0, 128, 0, 2, 0, 0, 0, 0])!.ResultCode);
        Assert.Null(NatPmp.Read([0, 129, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4]));
    }

    [Fact]
    public void StunBinding_IsTwentyBytesWithTheCookie()
    {
        byte[] transaction = [0xB7, 0xE7, 0xA7, 0x01, 0xBC, 0x34, 0xD6, 0x86, 0xFA, 0x87, 0xDF, 0xAE];
        byte[] request = StunMessage.Binding(transaction);

        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x21, 0x12, 0xA4, 0x42 }, request[..8]);
        Assert.Equal(transaction, request[8..20]);

        byte[] change = StunMessage.Binding(transaction, changeAddress: true, changePort: true);
        Assert.Equal(28, change.Length);
        Assert.Equal(new byte[] { 0x00, 0x08 }, change[2..4]);
        Assert.Equal(new byte[] { 0x00, 0x03, 0x00, 0x04, 0x00, 0x00, 0x00, 0x06 }, change[20..28]);
    }

    [Fact]
    public void StunAnswer_DecodesXorMappedAddress_Ipv4()
    {
        // The worked example from RFC 8489's attribute description: 192.0.2.1:32853
        // XORed with the cookie gives port A147 and address E112A643.
        byte[] transaction = [0xB7, 0xE7, 0xA7, 0x01, 0xBC, 0x34, 0xD6, 0x86, 0xFA, 0x87, 0xDF, 0xAE];
        byte[] answer = Answer(transaction, [0x00, 0x20, 0x00, 0x08, 0x00, 0x01, 0xA1, 0x47, 0xE1, 0x12, 0xA6, 0x43]);

        StunAnswer read = StunMessage.Read(answer, transaction)!;

        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 32853), read.Mapped);
        Assert.Null(read.ErrorCode);
    }

    [Fact]
    public void StunAnswer_DecodesXorMappedAddress_Ipv6_AsRfc5769Has()
    {
        // RFC 5769 2.3: 2001:db8:1234:5678:11:2233:4455:6677 port 32853.
        byte[] transaction = [0xB7, 0xE7, 0xA7, 0x01, 0xBC, 0x34, 0xD6, 0x86, 0xFA, 0x87, 0xDF, 0xAE];
        byte[] answer = Answer(
            transaction,
            [0x80, 0x22, 0x00, 0x0B, (byte)'t', (byte)'e', (byte)'s', (byte)'t', (byte)' ', (byte)'v', (byte)'e', (byte)'c', (byte)'t', (byte)'o', (byte)'r', 0x20,
             0x00, 0x20, 0x00, 0x14, 0x00, 0x02, 0xA1, 0x47,
             0x01, 0x13, 0xA9, 0xFA, 0xA5, 0xD3, 0xF1, 0x79, 0xBC, 0x25, 0xF4, 0xB5, 0xBE, 0xD2, 0xB9, 0xD9]);

        StunAnswer read = StunMessage.Read(answer, transaction)!;

        Assert.Equal(new IPEndPoint(IPAddress.Parse("2001:db8:1234:5678:11:2233:4455:6677"), 32853), read.Mapped);
        Assert.Equal("test vector", read.Software);
    }

    [Fact]
    public void StunAnswer_ForAnotherTransaction_IsIgnored()
    {
        byte[] transaction = new byte[12];
        byte[] other = new byte[12];
        other[0] = 1;

        Assert.Null(StunMessage.Read(Answer(other, []), transaction));
    }

    [Fact]
    public void StunAnswer_ReadsOtherAddressAndErrors()
    {
        byte[] transaction = new byte[12];
        byte[] answer = Answer(transaction, [0x80, 0x2C, 0x00, 0x08, 0x00, 0x01, 0x0D, 0x96, 203, 0, 113, 7]);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 3478), StunMessage.Read(answer, transaction)!.Other);

        byte[] error = Answer(transaction, [0x00, 0x09, 0x00, 0x04, 0x00, 0x00, 0x04, 0x14], type: StunMessage.BindingError);
        Assert.Equal(420, StunMessage.Read(error, transaction)!.ErrorCode);
    }

    [Theory]
    [InlineData("stun.example.org", "stun.example.org", 3478)]
    [InlineData("stun.example.org:19302", "stun.example.org", 19302)]
    [InlineData("  stun.example.org:99999 ", "stun.example.org:99999", 3478)]
    public void StunServers_AreParsed(string text, string host, int port)
    {
        Assert.Equal((host, port), StunNatTest.ParseServer(text));
    }

    [Fact]
    public void NatSummary_UsesTheCommonReading()
    {
        Assert.Equal("Open", new NatTestResult("s", null, null, NatMapping.EndpointIndependent, NatFiltering.EndpointIndependent, null).Summary);
        Assert.Equal("Moderate", new NatTestResult("s", null, null, NatMapping.EndpointIndependent, NatFiltering.AddressAndPortDependent, null).Summary);
        Assert.Equal("Strict", new NatTestResult("s", null, null, NatMapping.AddressAndPortDependent, NatFiltering.Unknown, null).Summary);
        Assert.Null(new NatTestResult("s", null, null, NatMapping.Unknown, NatFiltering.Unknown, null).Summary);
    }

    [Theory]
    [InlineData("203.0.113.9", null)]
    [InlineData("81.2.69.160", "81.2.69.160")]
    [InlineData("81.2.69.160\n", "81.2.69.160")]
    [InlineData("2a02:1234::20", "2a02:1234::20")]
    [InlineData("192.168.0.20", null)]
    [InlineData("{\"ip\":\"81.2.69.160\"}", null)]
    [InlineData("<html>81.2.69.160</html>", null)]
    [InlineData("", null)]
    public void PublicAddress_OnlyABarePublicAddressIsAnAnswer(string body, string? expected)
    {
        Assert.Equal(expected is null ? null : IPAddress.Parse(expected), PublicAddressLookup.Parse(body));
    }

    [Fact]
    public void PublicAddress_Destinations_NameEachServiceAndRefusePlainHttp()
    {
        ConnectionToolsSettings tools = new();

        Assert.Equal("api.ipify.org and api6.ipify.org", PublicAddressLookup.Describe(tools));

        tools.PublicAddressService = PublicAddressService.Custom;
        tools.PublicAddressCustomUrl = "http://example.org/ip";
        Assert.Empty(PublicAddressLookup.Destinations(tools));

        tools.PublicAddressCustomUrl = "https://ip.example.org/";
        Assert.Equal("ip.example.org", Assert.Single(PublicAddressLookup.Destinations(tools)).Host);
    }

    [Fact]
    public async Task PublicAddress_SwitchedOff_SendsNothing()
    {
        WindsockSettings settings = new();
        CountingHandler handler = new("81.2.69.160");
        using PublicAddressLookup lookup = new(settings, null, TimeProvider.System, handler: handler);

        PublicAddressResult result = await lookup.LookUpAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LookupOutcome.SwitchedOff, result.Outcome);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task PublicAddress_SwitchedOn_AsksEachFamilyOnceAndKeepsTheAnswer()
    {
        WindsockSettings settings = new();
        settings.ConnectionTools.PublicAddressLookups = true;
        CountingHandler handler = new("81.2.69.160");
        using PublicAddressLookup lookup = new(settings, null, TimeProvider.System, handler: handler);

        PublicAddressResult first = await lookup.LookUpAsync(cancellationToken: TestContext.Current.CancellationToken);
        PublicAddressResult second = await lookup.LookUpAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LookupOutcome.Answered, first.Outcome);
        Assert.Equal(IPAddress.Parse("81.2.69.160"), first.Four);
        Assert.Same(first, second);
        Assert.Equal(2, handler.Requests);
        Assert.All(handler.Agents, agent => Assert.StartsWith("Windsock/", agent, StringComparison.Ordinal));

        lookup.Forget();
        await lookup.LookUpAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, handler.Requests);
    }

    [Fact]
    public void AsnTable_RoundTripsAndFindsBothFamilies()
    {
        string path = Path.Combine(Path.GetTempPath(), "windsock-asn-" + Guid.NewGuid().ToString("N") + ".dat");

        try
        {
            string tsv = string.Join('\n',
                "1.0.0.0\t1.0.0.255\t13335\tUS\tCLOUDFLARENET",
                "1.0.1.0\t1.0.3.255\t0\tNone\tNot routed",
                "1.0.4.0\t1.0.7.255\t38803\tAU\tGTELECOM-AS-AP Gtelecom Pty Ltd",
                "1.0.8.0\t1.0.8.255\t38803\tAU\tGTELECOM-AS-AP Gtelecom Pty Ltd",
                "8.8.8.0\t8.8.8.255\t15169\tUS\tGOOGLE",
                "2606:4700::\t2606:4700:ffff:ffff:ffff:ffff:ffff:ffff\t13335\tUS\tCLOUDFLARENET",
                "2c0f:fff1::\tfebf:ffff:ffff:ffff:ffff:ffff:ffff:ffff\t0\tNone\tNot routed");

            using (FileStream output = File.Create(path))
            {
                (int four, int six, int systems) = AsnTableWriter.Write(AsnTableWriter.ReadIptoasn(new StringReader(tsv)), output, new DateOnly(2026, 10, 4));

                // 1.0.4.0 and 1.0.8.0 join; the gaps before 8.8.8.0 and after become unrouted rows.
                Assert.Equal(6, four);
                Assert.Equal(3, systems);
                Assert.True(six >= 2);
            }

            using AsnTable table = AsnTable.Open(path)!;

            Assert.Equal(20261004u, table.Date);
            Assert.Equal("Cloudflare", table.Find("1.0.0.1")!.Organisation);
            Assert.Null(table.Find("1.0.2.1"));
            Assert.Equal(38803u, table.Find("1.0.8.200")!.Number);
            Assert.Equal("Gtelecom Pty Ltd", table.Find("1.0.5.5")!.Organisation);
            Assert.Equal("AU", table.Find("1.0.5.5")!.Country);
            Assert.Null(table.Find("1.0.9.1"));
            Assert.Equal("AS15169", table.Find("8.8.8.8")!.Label);
            Assert.Null(table.Find("8.8.9.1"));
            Assert.Equal(13335u, table.Find("2606:4700:4700::1111")!.Number);
            Assert.Null(table.Find("192.168.0.1"));
            Assert.Null(table.Find("not an address"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AsnTableWriter_ALastRangeEndingAtTheTop_LeavesLookupsWhole()
    {
        string path = Path.Combine(Path.GetTempPath(), "windsock-asn-" + Guid.NewGuid().ToString("N") + ".dat");

        try
        {
            string tsv = string.Join('\n',
                "1.0.0.0\t1.0.0.255\t13335\tUS\tCLOUDFLARENET",
                "8.8.8.0\t8.8.8.255\t15169\tUS\tGOOGLE",
                "224.0.0.0\t255.255.255.255\t0\tNone\tNot routed");

            using (FileStream output = File.Create(path))
            {
                AsnTableWriter.Write(AsnTableWriter.ReadIptoasn(new StringReader(tsv)), output, new DateOnly(2026, 10, 4));
            }

            using AsnTable table = AsnTable.Open(path)!;

            Assert.Equal(13335u, table.Find("1.0.0.1")!.Number);
            Assert.Equal(15169u, table.Find("8.8.8.8")!.Number);
            Assert.Null(table.Find("9.9.9.9"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AsnTableWriter_RangesStartingTogether_LaterReplacesEarlier()
    {
        List<(ulong Start, ulong End, int System)> ranges = [];

        // Enough ties that an unstable sort would reorder some of them.
        for (int each = 0; each < 200; each++)
        {
            ranges.Add(((ulong)each * 1000, ((ulong)each * 1000) + 999, 1));
            ranges.Add(((ulong)each * 1000, ((ulong)each * 1000) + 999, 2));
        }

        List<(ulong Start, int System)> rows = AsnTableWriter.Merge(ranges, uint.MaxValue);

        Assert.Equal(2, rows[0].System);
        Assert.DoesNotContain(rows, each => each.System == 1);
    }

    [Fact]
    public void AsnTable_DamagedSections_AreRefusedOrGiveNoName()
    {
        string path = Path.Combine(Path.GetTempPath(), "windsock-asn-" + Guid.NewGuid().ToString("N") + ".dat");

        try
        {
            string tsv = string.Join('\n',
                "1.0.0.0\t1.0.0.255\t13335\tUS\tCLOUDFLARENET",
                "8.8.8.0\t8.8.8.255\t15169\tUS\tGOOGLE");

            using (FileStream output = File.Create(path))
            {
                AsnTableWriter.Write(AsnTableWriter.ReadIptoasn(new StringReader(tsv)), output, new DateOnly(2026, 10, 4));
            }

            byte[] good = File.ReadAllBytes(path);

            // An IPv4 data section said to start past the end of the file.
            byte[] pastTheEnd = (byte[])good.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(pastTheEnd.AsSpan(44), (uint)good.Length + 100);
            File.WriteAllBytes(path, pastTheEnd);
            Assert.Null(AsnTable.Open(path));

            // A file cut short of the length its header gives.
            File.WriteAllBytes(path, good.AsSpan(0, good.Length - 4).ToArray());
            Assert.Null(AsnTable.Open(path));

            // Rows that run on past the end: a lookup finds no name, and does not throw.
            byte[] runOn = (byte[])good.Clone();
            uint dataAt = BinaryPrimitives.ReadUInt32LittleEndian(good.AsSpan(44));
            runOn.AsSpan((int)dataAt).Fill(0xFF);
            File.WriteAllBytes(path, runOn);

            using AsnTable table = AsnTable.Open(path)!;
            Assert.Null(table.Find("8.8.8.8"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AsnTable_RefusesAFileThatIsNotOne()
    {
        string path = Path.Combine(Path.GetTempPath(), "windsock-asn-" + Guid.NewGuid().ToString("N") + ".dat");

        try
        {
            File.WriteAllBytes(path, new byte[128]);
            Assert.Null(AsnTable.Open(path));
            Assert.Null(AsnTable.Open(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ShippedAsnTable_IsThereAndNamesTheBigNetworks()
    {
        using AsnTable? table = AsnTable.Open(Path.Combine(AppContext.BaseDirectory, "Data", AsnTable.FileName));

        Assert.NotNull(table);
        Assert.True(table.Systems > 50_000);
        Assert.Equal(13335u, table.Find("1.1.1.1")!.Number);
        Assert.Equal("Google", table.Find("8.8.8.8")!.Organisation);
    }

    [Theory]
    [InlineData(13335u, "CLOUDFLARENET", "Cloudflare")]
    [InlineData(15169u, "GOOGLE", "Google")]
    [InlineData(396982u, "GOOGLE-CLOUD-PLATFORM", "Google Cloud")]
    [InlineData(8075u, "MICROSOFT-CORP-MSN-AS-BLOCK", "Microsoft")]
    [InlineData(16509u, "AMAZON-02", "Amazon")]
    [InlineData(8987u, "AWS-GOVCLOUD AWS-GOVCLOUD", "Amazon")]
    [InlineData(58717u, "SUMMITCOMMUNICATIONS-BD Summit Communications Ltd", "Summit Communications Ltd")]
    [InlineData(1u, "HOMEMAKERS UPSTATE GROUP - Homemakers Upstate Group, Inc.", "Homemakers Upstate Group, Inc.")]
    [InlineData(1u, "AS-NNI AS-NNI", "AS-NNI")]
    [InlineData(145050u, "AS145050", "AS145050")]
    [InlineData(7922u, "Comcast Cable Communications", "Comcast Cable Communications")]
    public void AsnNames_ReadAsOrganisations(uint number, string name, string organisation)
    {
        Assert.Equal(organisation, AsnNames.Organisation(number, name));
    }

    [Theory]
    [InlineData("EQUINIX BRASIL", "EQUINIX BRASIL")]
    [InlineData("NNI NNI", "NNI NNI")]
    [InlineData("TELIA COMPANY AB", "TELIA COMPANY AB")]
    [InlineData("AS9498 Bharti Airtel", "Bharti Airtel")]
    [InlineData("AIRTEL_BD Airtel Bangladesh", "Airtel Bangladesh")]
    public void AsnNames_KeepAPlainCapitalFirstWord(string name, string organisation)
    {
        // A first word without a hyphen, underscore or digit is the organisation's, not a handle.
        Assert.Equal(organisation, AsnNames.Organisation(name));
    }

    [Theory]
    [InlineData(64500u, "APPLEBEE-NETWORKS", "APPLEBEE-NETWORKS")]
    [InlineData(64501u, "VALVERA", "VALVERA")]
    [InlineData(64502u, "GOOGLEFIBER-LIKE-AS Example Fibre Ltd", "Example Fibre Ltd")]
    public void AsnNames_NameBrandsByNumberOnly(uint number, string name, string organisation)
    {
        Assert.Equal(organisation, AsnNames.Organisation(number, name));
    }

    private static byte[] Answer(byte[] transaction, byte[] attributes, ushort type = StunMessage.BindingSuccess)
    {
        byte[] message = new byte[20 + attributes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(message, type);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)attributes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), StunMessage.Cookie);
        transaction.CopyTo(message, 8);
        attributes.CopyTo(message, 20);
        return message;
    }

    private sealed class CountingHandler(string body) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        public List<string> Agents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);

            lock (Agents)
            {
                Agents.Add(request.Headers.UserAgent.ToString());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
