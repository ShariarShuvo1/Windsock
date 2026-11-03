using Windsock.Core.Processes;

namespace Windsock.Core.Timeline;

/// <summary>
/// What one process moved to and from one far end since the last time flows
/// were taken.
/// </summary>
/// <param name="ProcessId">The process.</param>
/// <param name="Remote">The far end; an empty key for traffic folded together past the cap.</param>
/// <param name="Sent">Bytes sent to it.</param>
/// <param name="Received">Bytes received from it.</param>
/// <param name="Packets">Packets either way.</param>
/// <param name="FirstTicks">UTC ticks of the first packet in this stretch.</param>
/// <param name="LastTicks">UTC ticks of the last packet in this stretch.</param>
/// <param name="Image">The kernel's path of the process's file, when the trace saw it start.</param>
public readonly record struct FlowSample(
    int ProcessId,
    EndpointKey Remote,
    long Sent,
    long Received,
    long Packets,
    long FirstTicks,
    long LastTicks,
    string? Image);

/// <summary>
/// The kernel trace's record of who talked to whom, for the timeline and each
/// day's places. Full edition, as administrator.
/// </summary>
public interface IFlowLedger
{
    /// <summary>Whether the trace is attributing packets now.</summary>
    bool CanAttribute { get; }

    /// <summary>
    /// Whether flows are being kept. Off unless something asked: keeping them
    /// costs a lookup a packet, and the timeline is off by default.
    /// </summary>
    bool CollectFlows { get; set; }

    /// <summary>Every flow since the last call, and forgets them; loopback is never in them.</summary>
    void TakeFlows(List<FlowSample> into);
}

/// <summary>
/// Running totals of what crossed the local network and what crossed the
/// internet connection, since the trace started.
/// </summary>
/// <param name="LocalSent">Bytes sent to addresses on a local network.</param>
/// <param name="LocalReceived">Bytes received from them.</param>
/// <param name="InternetSent">Bytes sent anywhere else.</param>
/// <param name="InternetReceived">Bytes received from anywhere else.</param>
/// <remarks>
/// The trace counts payload, not packets: headers are in neither figure, so
/// the two together read below the adapter's count (about 90-97% down and
/// 55-65% up, measured 2026-09-15). Loopback is in neither.
/// </remarks>
public readonly record struct TrafficSplit(long LocalSent, long LocalReceived, long InternetSent, long InternetReceived)
{
    /// <summary>The bytes moved between two readings, never negative.</summary>
    public TrafficSplit Since(TrafficSplit before) => new(
        Math.Max(0, LocalSent - before.LocalSent),
        Math.Max(0, LocalReceived - before.LocalReceived),
        Math.Max(0, InternetSent - before.InternetSent),
        Math.Max(0, InternetReceived - before.InternetReceived));

    /// <summary>Both directions on the local network.</summary>
    public long Local => LocalSent + LocalReceived;

    /// <summary>Both directions over the internet connection.</summary>
    public long Internet => InternetSent + InternetReceived;
}

/// <summary>Splits traffic by where it went. Full edition, as administrator.</summary>
public interface ITrafficSplitSource
{
    /// <summary>Whether the trace is attributing packets now; the totals stand still otherwise.</summary>
    bool CanAttribute { get; }

    /// <summary>The running totals.</summary>
    TrafficSplit ReadSplit();
}
