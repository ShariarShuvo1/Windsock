using Windows.Networking.Connectivity;
using Windsock.Core.Apps;
using Windsock.Core.History;

namespace Windsock.App.Services.Usage;

/// <summary>
/// Reads each app's use of the network from Windows' own data usage - the
/// figures Settings shows under Network and internet, Data usage.
/// </summary>
/// <remarks>
/// <para>
/// Windows keeps these per connection profile: every network the machine has
/// joined, whether or not it is joined now. Each window is read from every
/// profile and added together, so a laptop that moved from Wi-Fi to a phone's
/// hotspot has both counted. A profile with nothing in the whole span is asked
/// once and then left alone, which on a desktop with one cable and a list of
/// old networks saves most of the calls.
/// </para>
/// <para>
/// A profile that refuses is left out of the read instead of failing it, and
/// named in <see cref="LeftOut"/>; the collector goes back for what it missed
/// once it answers (<see cref="NetworkProfileRefusals"/>). Only when no profile
/// answers at all is Windows taken as not answering.
/// </para>
/// <para>
/// No privilege is involved. The Store package declares
/// <c>networkDataUsageManagement</c>, which Microsoft's capability list names
/// for this call; the unpackaged editions need nothing.
/// </para>
/// </remarks>
public sealed class WindowsAppUsageReader : IAppUsageReader
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    private const string Unnamed = "an unnamed network";

    private readonly NetworkProfileRefusals _refusals;

    public WindowsAppUsageReader()
        : this(TimeProvider.System)
    {
    }

    public WindowsAppUsageReader(TimeProvider clock) => _refusals = new NetworkProfileRefusals(clock);

    /// <inheritdoc />
    public IReadOnlyList<LeftOutNetwork> LeftOut => _refusals.LeftOut;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppUsageReading>> ReadAsync(IReadOnlyList<UsageWindow> windows, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(windows);

        if (windows.Count == 0)
        {
            return [];
        }

        IReadOnlyList<ConnectionProfile> profiles;

        try
        {
            profiles = NetworkInformation.GetConnectionProfiles();
        }
#pragma warning disable CA1031 // WinRT fails in more ways than it documents; each is "Windows would not answer".
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new AppUsageUnavailableException("Windows would not list the networks it keeps data usage for.", ex);
        }

        List<(ConnectionProfile Profile, string Name)> named = [.. profiles.Select(profile => (profile, NameOf(profile)))];
        _refusals.KeepOnly(named.Select(each => each.Name));

        NetworkUsageStates states = new() { Roaming = TriStates.DoNotCare, Shared = TriStates.DoNotCare };
        long first = windows.Min(window => window.StartMinute);
        long last = windows.Max(window => window.EndMinute);

        List<(ConnectionProfile Profile, string Name)> busy = [];
        int answered = 0;
        int asked = 0;
        Exception? refusal = null;

        foreach ((ConnectionProfile profile, string name) in named)
        {
            if (!_refusals.ShouldAsk(name))
            {
                continue;
            }

            asked++;

            try
            {
                IReadOnlyList<AttributedNetworkUsage> any = await Ask(profile, first, last, states, token).ConfigureAwait(false);

                if (any.Count > 0)
                {
                    busy.Add((profile, name));
                }
                else
                {
                    _refusals.Answered(name);
                    answered++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A long span is the likeliest call to run out of patience, and
                // a busy profile the likeliest to be slow. The windows are
                // shorter; they decide whether this profile really refuses.
                refusal ??= ex;
                busy.Add((profile, name));
            }
        }

        Dictionary<string, (long Received, long Sent)>[] sums = [.. windows.Select(_ => new Dictionary<string, (long, long)>(StringComparer.Ordinal))];

        foreach ((ConnectionProfile profile, string name) in busy)
        {
            List<IReadOnlyList<AttributedNetworkUsage>> read = new(windows.Count);

            try
            {
                foreach (UsageWindow window in windows)
                {
                    read.Add(await Ask(profile, window.StartMinute, window.EndMinute, states, token).ConfigureAwait(false));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // All of this profile or none of it: a window read short would
                // be kept short, where one read without the profile is known to
                // be missing it and is read again.
                refusal ??= ex;
                _refusals.Refused(name, first);
                continue;
            }

            for (int index = 0; index < windows.Count; index++)
            {
                foreach (AttributedNetworkUsage entry in read[index])
                {
                    string id = AppAttribution.Classify(entry.AttributionId).Id;
                    (long received, long sent) = sums[index].GetValueOrDefault(id);
                    sums[index][id] = (Add(received, entry.BytesReceived), Add(sent, entry.BytesSent));
                }
            }

            _refusals.Answered(name);
            answered++;
        }

        // Some network was asked and not one answered: that is Windows not
        // answering, and nothing about any window is known.
        if (asked > 0 && answered == 0)
        {
            throw refusal is null
                ? new AppUsageUnavailableException("Windows would not say how much each app used the network.")
                : new AppUsageUnavailableException("Windows would not say how much each app used the network.", refusal);
        }

        // Every network is waiting out a refusal and none was asked: no window
        // can be read without all of them missing.
        if (asked == 0 && named.Count > 0)
        {
            throw new AppUsageUnavailableException("Windows would not say how much each app used the network.");
        }

        List<AppUsageReading> readings = new(windows.Count);

        for (int index = 0; index < windows.Count; index++)
        {
            readings.Add(new AppUsageReading(
                windows[index],
                [.. sums[index].Select(pair => new AppUsageRecord(pair.Key, pair.Value.Received, pair.Value.Sent))]));
        }

        return readings;
    }

    private static string NameOf(ConnectionProfile profile)
    {
        try
        {
            return string.IsNullOrWhiteSpace(profile.ProfileName) ? Unnamed : profile.ProfileName.Trim();
        }
#pragma warning disable CA1031 // A profile that cannot say its name is still a profile.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Unnamed;
        }
    }

    private static async Task<IReadOnlyList<AttributedNetworkUsage>> Ask(
        ConnectionProfile profile,
        long fromMinute,
        long untilMinute,
        NetworkUsageStates states,
        CancellationToken token)
    {
        // Cancelling the operation itself, not only waiting less for it, so a
        // call Windows never answers is not left running behind.
        using CancellationTokenSource patience = CancellationTokenSource.CreateLinkedTokenSource(token);
        patience.CancelAfter(Patience);

        try
        {
            return await profile
                .GetAttributedNetworkUsageAsync(EpochMinute.ToUtc(fromMinute), EpochMinute.ToUtc(untilMinute), states)
                .AsTask(patience.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Windows did not answer in time.");
        }
    }

    private static long Add(long total, ulong more) =>
        more > (ulong)(long.MaxValue - total) ? long.MaxValue : total + (long)more;
}
