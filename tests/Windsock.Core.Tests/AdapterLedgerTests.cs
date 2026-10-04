using Windsock.Core.Networking;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class AdapterLedgerTests
{
    private const ulong Ethernet = 0x06_0000_0001;
    private const ulong WiFi = 0x47_0000_0002;

    private const long Gigabyte = 1024L * 1024 * 1024;

    [Fact]
    public void TheFirstReading_CountsNothing()
    {
        AdapterLedger ledger = new();

        NetworkTotals first = Read(ledger, (Ethernet, 112 * Gigabyte, 4 * Gigabyte));

        Assert.Equal(0, first.BytesReceived);
        Assert.Equal(0, first.BytesSent);
    }

    [Fact]
    public void GrowthOnAnAdapter_IsCounted()
    {
        AdapterLedger ledger = new();

        Read(ledger, (Ethernet, 1000, 100));
        NetworkTotals second = Read(ledger, (Ethernet, 1500, 160));

        Assert.Equal(500, second.BytesReceived);
        Assert.Equal(60, second.BytesSent);
    }

    [Fact]
    public void AnAdapterThatComesBackUp_BringsNoneOfItsPast()
    {
        // 2026-09-12: the link dropped for sleep and came back on wake.
        AdapterLedger ledger = new();

        Read(ledger, (Ethernet, 112 * Gigabyte, 4 * Gigabyte));
        NetworkTotals beforeSleep = Read(ledger, (Ethernet, (112 * Gigabyte) + 2048, (4 * Gigabyte) + 512));

        Read(ledger);
        NetworkTotals onWake = Read(ledger, (Ethernet, (112 * Gigabyte) + 9000, (4 * Gigabyte) + 900));

        Assert.Equal(beforeSleep, onWake);

        NetworkTotals afterWake = Read(ledger, (Ethernet, (112 * Gigabyte) + 10_000, (4 * Gigabyte) + 1000));

        Assert.Equal(beforeSleep.BytesReceived + 1000, afterWake.BytesReceived);
        Assert.Equal(beforeSleep.BytesSent + 100, afterWake.BytesSent);
    }

    [Fact]
    public void ASecondAdapterJoining_AddsOnlyWhatItMovesAfterwards()
    {
        AdapterLedger ledger = new();

        Read(ledger, (Ethernet, 1000, 100));
        NetworkTotals joined = Read(ledger, (Ethernet, 1100, 110), (WiFi, 50 * Gigabyte, Gigabyte));
        NetworkTotals both = Read(ledger, (Ethernet, 1200, 120), (WiFi, (50 * Gigabyte) + 300, Gigabyte + 30));

        Assert.Equal(100, joined.BytesReceived);
        Assert.Equal(500, both.BytesReceived);
        Assert.Equal(50, both.BytesSent);
    }

    [Fact]
    public void AnAdapterLeaving_TakesNothingAway()
    {
        AdapterLedger ledger = new();

        Read(ledger, (Ethernet, 1000, 100), (WiFi, 5000, 500));
        NetworkTotals both = Read(ledger, (Ethernet, 1100, 110), (WiFi, 5200, 520));
        NetworkTotals alone = Read(ledger, (Ethernet, 1150, 115));

        Assert.Equal(300, both.BytesReceived);
        Assert.Equal(350, alone.BytesReceived);
        Assert.Equal(35, alone.BytesSent);
    }

    [Fact]
    public void ACounterThatGoesBackwards_CountsNothing_AndRestartsFromThere()
    {
        AdapterLedger ledger = new();

        Read(ledger, (Ethernet, 5000, 500));
        NetworkTotals reset = Read(ledger, (Ethernet, 100, 10));
        NetworkTotals after = Read(ledger, (Ethernet, 400, 40));

        Assert.Equal(0, reset.BytesReceived);
        Assert.Equal(300, after.BytesReceived);
        Assert.Equal(30, after.BytesSent);
    }

    [Fact]
    public void TheTotal_IsWhatTheLastReadingReturned()
    {
        AdapterLedger ledger = new();

        Read(ledger, (Ethernet, 1000, 100));
        NetworkTotals last = Read(ledger, (Ethernet, 1700, 170));

        Assert.Equal(last, ledger.Total);
    }

    private static NetworkTotals Read(AdapterLedger ledger, params (ulong Adapter, long Received, long Sent)[] adapters)
    {
        ledger.Begin();

        foreach ((ulong adapter, long received, long sent) in adapters)
        {
            ledger.Count(adapter, received, sent);
        }

        return ledger.End();
    }
}
