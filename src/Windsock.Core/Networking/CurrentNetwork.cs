using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.Settings;

namespace Windsock.Core.Networking;

/// <summary>
/// Knows which network this PC is on, for tagging history's minutes, choosing
/// a data plan, and telling when the network is a phone's hotspot.
/// </summary>
/// <remarks>
/// <para>
/// Windows is asked when it says the network changed, and once a minute in case
/// a change went unsaid. Asking sends nothing; it reads the connection profile
/// Windows already has. This runs whether or not anything is on screen, because
/// every recorded minute needs its network; it is a read a minute.
/// </para>
/// <para>
/// Raises <see cref="Changed"/> on its own thread when the network or anything
/// Windows says about it changes.
/// </para>
/// </remarks>
public sealed partial class CurrentNetwork : BackgroundService, INetworkTagSource
{
    private static readonly TimeSpan ReadEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1.5);

    private readonly INetworkSightingSource _source;
    private readonly WindsockSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<CurrentNetwork> _logger;
    private readonly SemaphoreSlim _nudge = new(0, 1);
    private volatile NetworkSighting? _sighting;
    private bool _failed;

    public CurrentNetwork(INetworkSightingSource source, WindsockSettings settings, TimeProvider clock, ILogger<CurrentNetwork> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _source.Changed += OnSourceChanged;
    }

    /// <summary>Raised, on any thread, when the network in use or what Windows says about it changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The network in use and what Windows says about it, or null when there is none or it is not known yet.</summary>
    public NetworkSighting? Sighting => _sighting;

    /// <inheritdoc />
    public NetworkTag? Current => _sighting?.Network;

    /// <summary>Whether the network in use is taken for a phone's hotspot, and why.</summary>
    public HotspotVerdict Hotspot => HotspotFor(_sighting);

    /// <summary>The name to show for the network in use: the person's own, else Windows'.</summary>
    public string? DisplayName => _sighting is { } seen ? NameOf(seen.Network) : null;

    /// <summary>The name to show for a network: the person's own, else Windows'.</summary>
    public string NameOf(NetworkTag network)
    {
        ArgumentNullException.ThrowIfNull(network);
        string? label = _settings.Usage.ChoiceFor(network.Key)?.Label;
        return string.IsNullOrWhiteSpace(label) ? network.Name : label.Trim();
    }

    /// <summary>Whether a network is taken for a phone's hotspot, with the person's marking.</summary>
    public HotspotVerdict HotspotFor(NetworkSighting? sighting) =>
        HotspotJudge.Judge(sighting, sighting is null ? null : _settings.Usage.ChoiceFor(sighting.Network.Key)?.Hotspot);

    /// <summary>Asks Windows again soon, after the person changed a marking or a name.</summary>
    public void Refresh()
    {
        _nudge.Signal();
    }

    private void OnSourceChanged(object? sender, EventArgs e) => Refresh();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            ReadNow(tellAnyway: false);

            while (!stoppingToken.IsCancellationRequested)
            {
                bool nudged = await _nudge.WaitAsync(ReadEvery, stoppingToken).ConfigureAwait(false);

                if (nudged)
                {
                    // Windows reports a change as a burst; one read after it settles.
                    await Task.Delay(SettleDelay, _clock, stoppingToken).ConfigureAwait(false);
                }

                // A nudge may be the person renaming or marking a network, which
                // changes what is shown without the network changing.
                ReadNow(tellAnyway: nudged);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Reads the network once. Internal so tests can drive it.</summary>
    /// <param name="tellAnyway">Whether to raise <see cref="Changed"/> even when Windows says nothing new.</param>
    internal void ReadNow(bool tellAnyway = false)
    {
        NetworkSighting? seen;

        try
        {
            seen = _source.Read();
            _failed = false;
        }
#pragma warning disable CA1031 // A failed read is an unknown network, not a reason to stop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (!_failed)
            {
                _failed = true;
                LogReadFailed(ex);
            }

            seen = null;
        }

        NetworkSighting? before = _sighting;

        if (Equals(before, seen) && !tellAnyway)
        {
            return;
        }

        _sighting = seen;

        // The kind only: a Wi-Fi name says where somebody is, and logs are kept.
        if (!string.Equals(before?.Network.Key, seen?.Network.Key, StringComparison.Ordinal))
        {
            LogNetworkChanged(seen is null ? "none" : seen.Network.Kind.ToString());
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public override void Dispose()
    {
        _source.Changed -= OnSourceChanged;
        base.Dispose();
        _nudge.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read which network is in use; minutes are kept without one")]
    private partial void LogReadFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "The network in use changed (now {Kind})")]
    private partial void LogNetworkChanged(string kind);
}
