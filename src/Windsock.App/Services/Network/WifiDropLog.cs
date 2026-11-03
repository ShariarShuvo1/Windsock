using System.Diagnostics.Eventing.Reader;
using Microsoft.Extensions.Logging;
using Windsock.Core.Wifi;

namespace Windsock.App.Services.Network;

/// <summary>Whether Windows' Wi-Fi log could be read.</summary>
public enum WifiLogState
{
    Readable,

    /// <summary>The log is switched off or missing (some editions, or Group Policy).</summary>
    Missing,

    /// <summary>Windows refused: a service account, or a policy, without read access.</summary>
    Refused,
}

/// <summary>
/// Reads Wi-Fi connects, failures and disconnects from WLAN AutoConfig's
/// Operational log, with Windows' own reason for each - on request, and as
/// they happen while someone watches.
/// </summary>
/// <remarks>
/// Read unelevated on the development machine on 2026-10-05; the channel
/// grants read to interactive users. The log keeps about a megabyte, weeks on
/// a quiet PC. Network names in it are kept or dropped by the caller, which
/// follows the location permission the Wi-Fi details follow.
/// </remarks>
public sealed partial class WifiDropLog(ILogger<WifiDropLog> logger)
{
    private const int MostEvents = 4000;

    /// <summary>How the last read went.</summary>
    public WifiLogState State { get; private set; } = WifiLogState.Readable;

    /// <summary>The events of a recent stretch, newest first.</summary>
    /// <param name="lookBack">How far back.</param>
    /// <param name="withNames">Whether to keep network names.</param>
    public IReadOnlyList<WifiEvent> Read(TimeSpan lookBack, bool withNames)
    {
        List<WifiEvent> events = [];

        try
        {
            EventLogQuery query = new(WifiEvents.LogName, PathType.LogName, WifiEvents.Query(lookBack)) { ReverseDirection = true };
            using EventLogReader reader = new(query);

            while (events.Count < MostEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    if (WifiEvents.Parse(record.ToXml()) is { } parsed)
                    {
                        events.Add(withNames ? parsed : parsed.WithoutName());
                    }
                }
            }

            State = WifiLogState.Readable;
        }
        catch (EventLogNotFoundException)
        {
            State = WifiLogState.Missing;
        }
        catch (UnauthorizedAccessException ex)
        {
            State = WifiLogState.Refused;
            LogRefused(ex);
        }
        catch (EventLogException ex)
        {
            State = WifiLogState.Missing;
            LogFailed(ex);
        }

        return events;
    }

    /// <summary>The drops of a recent stretch, newest first.</summary>
    public IReadOnlyList<WifiDrop> Drops(TimeSpan lookBack, bool withNames) =>
        WifiEvents.Drops(Read(lookBack, withNames), withNames);

    /// <summary>
    /// Hears new connects and disconnects as Windows logs them, until the token
    /// is let go. Nothing, when the log cannot be watched.
    /// </summary>
    /// <param name="heard">Called on a worker thread with each event.</param>
    /// <param name="withNames">Whether to keep network names.</param>
    public IDisposable? Watch(Action<WifiEvent> heard, bool withNames)
    {
        ArgumentNullException.ThrowIfNull(heard);

        EventLogWatcher? watcher = null;

        try
        {
            EventLogQuery query = new(WifiEvents.LogName, PathType.LogName, WifiEvents.WatchQuery);
            watcher = new(query);

            watcher.EventRecordWritten += (_, written) =>
            {
                using EventRecord? record = written.EventRecord;

                if (record is not null && WifiEvents.Parse(record.ToXml()) is { } parsed)
                {
                    heard(withNames ? parsed : parsed.WithoutName());
                }
            };

            watcher.Enabled = true;
            EventLogWatcher started = watcher;
            watcher = null;
            return started;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            LogFailed(ex);
            return null;
        }
        finally
        {
            // Only a watcher that failed to start is still held here.
            watcher?.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Windows refused to let the Wi-Fi log be read")]
    private partial void LogRefused(Exception error);

    [LoggerMessage(Level = LogLevel.Information, Message = "The Wi-Fi log could not be read")]
    private partial void LogFailed(Exception error);
}
