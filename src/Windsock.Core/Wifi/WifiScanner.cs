namespace Windsock.Core.Wifi;

/// <summary>What a scan of nearby networks found.</summary>
/// <param name="At">When the scan finished.</param>
/// <param name="Status">How it went; <see cref="WlanStatus.Denied"/> means Windows wants location permission.</param>
/// <param name="Networks">Every access point heard, strongest first.</param>
/// <param name="Loads">How busy each candidate channel is, per band.</param>
/// <param name="Suggestions">The quietest channel in each band where networks were heard.</param>
/// <param name="Fresh">Whether the adapter finished a new scan; otherwise the list is what it last heard.</param>
public sealed record WifiScan(
    DateTimeOffset At,
    WlanStatus Status,
    IReadOnlyList<NearbyNetwork> Networks,
    IReadOnlyDictionary<WifiBand, IReadOnlyList<ChannelLoad>> Loads,
    IReadOnlyDictionary<WifiBand, ChannelSuggestion> Suggestions,
    bool Fresh);

/// <summary>Scans for nearby networks, only when asked.</summary>
/// <remarks>
/// A scan needs location permission since Windows 11 24H2, takes up to four
/// seconds (the time Microsoft allows a certified driver), and while it runs
/// the adapter leaves its channel to listen, which raises latency. So it never
/// runs by itself, never twice within <see cref="Cooldown"/>, and the
/// connection tools do not measure while one is under way.
/// </remarks>
public sealed class WifiScanner(IWlanClient client, TimeProvider time) : IDisposable
{
    /// <summary>How long Windows gives a driver to finish a scan.</summary>
    public static readonly TimeSpan ScanTime = TimeSpan.FromSeconds(4);

    /// <summary>The shortest gap between two scans Windsock asks for.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _one = new(1, 1);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>Whether a scan is running.</summary>
    public bool IsScanning => _one.CurrentCount == 0;

    /// <summary>
    /// Scans with the Wi-Fi adapter and reports what was heard. A scan asked
    /// for within <see cref="Cooldown"/> of the last reads what the adapter
    /// already holds instead of scanning again.
    /// </summary>
    /// <param name="own">Whether a network is the person's own, left out of the crowding.</param>
    /// <param name="ownChannels">The person's own channel in each band, when known.</param>
    /// <param name="widthMhz">The width the person's network uses, for judging 5 and 6 GHz channels.</param>
    /// <param name="busy">
    /// Whether something the scan would disturb is running - a speed test. Asked
    /// after this scan holds its own lock, and a test takes its lock before asking
    /// whether a scan runs, so the two can never both start.
    /// </param>
    /// <param name="cancellationToken">Stops waiting.</param>
    public async Task<WifiScan> ScanAsync(
        Func<NearbyNetwork, bool>? own = null,
        IReadOnlyDictionary<WifiBand, int>? ownChannels = null,
        int widthMhz = 20,
        Func<bool>? busy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (busy?.Invoke() == true)
            {
                return Empty(WlanStatus.Busy);
            }

            (WlanStatus status, IReadOnlyList<WlanInterface> adapters) = client.Interfaces();
            WlanInterface? adapter = adapters.FirstOrDefault(each => each.IsConnected) ?? (adapters.Count > 0 ? adapters[0] : null);

            if (status != WlanStatus.Ok || adapter is null)
            {
                return Empty(status == WlanStatus.Ok ? WlanStatus.NoService : status);
            }

            bool fresh = false;

            if (time.GetUtcNow() - _last >= Cooldown)
            {
                WlanStatus asked = client.Scan(adapter.Id);

                if (asked == WlanStatus.Denied)
                {
                    return Empty(asked);
                }

                if (asked == WlanStatus.Ok)
                {
                    _last = time.GetUtcNow();
                    fresh = true;
                    await Task.Delay(ScanTime, time, cancellationToken).ConfigureAwait(false);
                }
            }

            (WlanStatus listed, byte[]? data) = client.NetworkList(adapter.Id);

            if (listed != WlanStatus.Ok || data is null)
            {
                return Empty(listed);
            }

            return Build(NearbyNetworks.Decode(data), own, ownChannels, widthMhz, time.GetLocalNow(), fresh);
        }
        finally
        {
            _one.Release();
        }

        WifiScan Empty(WlanStatus status) =>
            new(time.GetLocalNow(), status, [], new Dictionary<WifiBand, IReadOnlyList<ChannelLoad>>(), new Dictionary<WifiBand, ChannelSuggestion>(), false);
    }

    public void Dispose() => _one.Dispose();

    /// <summary>Works out the crowding and suggestions for a list of networks.</summary>
    public static WifiScan Build(
        IReadOnlyList<NearbyNetwork> networks,
        Func<NearbyNetwork, bool>? own,
        IReadOnlyDictionary<WifiBand, int>? ownChannels,
        int widthMhz,
        DateTimeOffset at,
        bool fresh)
    {
        ArgumentNullException.ThrowIfNull(networks);

        Dictionary<WifiBand, IReadOnlyList<ChannelLoad>> loads = [];
        Dictionary<WifiBand, ChannelSuggestion> suggestions = [];

        foreach (WifiBand band in new[] { WifiBand.TwoPointFour, WifiBand.Five, WifiBand.Six })
        {
            if (!networks.Any(each => each.Band == band))
            {
                continue;
            }

            IReadOnlyList<ChannelLoad> bandLoads = ChannelCrowding.Loads(networks, band, widthMhz, own);
            loads[band] = bandLoads;

            int? current = ownChannels is not null && ownChannels.TryGetValue(band, out int channel) ? channel : null;

            if (ChannelCrowding.Suggest(bandLoads, current) is { } suggestion)
            {
                suggestions[band] = suggestion;
            }
        }

        return new WifiScan(at, WlanStatus.Ok, [.. networks.OrderByDescending(each => each.Rssi)], loads, suggestions, fresh);
    }
}
