namespace Windsock.Core.Networking;

/// <summary>
/// Adds up byte counters that each belong to one adapter into a single total
/// that grows only by what those adapters actually moved.
/// </summary>
/// <remarks>
/// <para>
/// Summing the raw counters and differencing the sum is wrong whenever the set
/// of adapters being summed changes. An adapter's counter runs from when its
/// driver started, not from when Windsock did, so an adapter that joins the sum
/// brings its whole past with it and the sampler reads that as one interval's
/// traffic.
/// </para>
/// <para>
/// That is not rare. An adapter leaves the sum whenever it stops being up -
/// sleep, a cable, a Wi-Fi reconnect - and whenever it is deselected. Measured
/// on 2026-09-12: the Ethernet link dropped at sleep and came back on wake,
/// and the first minute after wake was recorded as 112 GB down at a peak of
/// 222 GB/s, which was the adapter's counter since boot arriving in a single
/// half-second tick.
/// </para>
/// <para>
/// So each adapter is measured only against its own previous reading. One seen
/// for the first time, or again after missing a reading, sets its baseline and
/// counts nothing. A counter that goes backwards has been reset and also counts
/// nothing. The cost is at most one interval of traffic on that one adapter.
/// </para>
/// </remarks>
internal sealed class AdapterLedger
{
    private readonly Dictionary<ulong, Reading> _last = [];
    private readonly List<ulong> _gone = [];
    private long _received;
    private long _sent;
    private int _pass;

    /// <summary>Everything counted so far.</summary>
    public NetworkTotals Total => new(_received, _sent);

    /// <summary>Starts a reading of every adapter.</summary>
    public void Begin() => _pass++;

    /// <summary>Takes one adapter's counters in the reading under way.</summary>
    public void Count(ulong adapter, long received, long sent)
    {
        if (_last.TryGetValue(adapter, out Reading was))
        {
            _received += Grown(received - was.Received);
            _sent += Grown(sent - was.Sent);
        }

        _last[adapter] = new Reading(received, sent, _pass);
    }

    /// <summary>
    /// Finishes the reading, and forgets every adapter it did not include so
    /// that one coming back starts again from its own baseline.
    /// </summary>
    public NetworkTotals End()
    {
        foreach ((ulong adapter, Reading reading) in _last)
        {
            if (reading.Pass != _pass)
            {
                _gone.Add(adapter);
            }
        }

        foreach (ulong adapter in _gone)
        {
            _last.Remove(adapter);
        }

        _gone.Clear();

        return Total;
    }

    private static long Grown(long delta) => delta > 0 ? delta : 0;

    private readonly record struct Reading(long Received, long Sent, int Pass);
}
