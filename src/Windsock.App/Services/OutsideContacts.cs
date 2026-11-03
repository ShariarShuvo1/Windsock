using Windsock.Core.Networking;
using Windsock.Core.Processes;
using Windsock.Core.Security;
using Windsock.Core.Settings;

namespace Windsock.App.Services;

/// <summary>
/// The switches for what Windsock sends beyond this PC on behalf of what it
/// shows: tracing the route to each place, and looking up names in reverse.
/// </summary>
/// <remarks>
/// <para>
/// One owner, so each switch is one setting wherever it is shown. Route
/// tracing appears on the map, in an app's own window and on the privacy page;
/// they all read and set this, and each hears when another changed it. There
/// used to be two route switches - a standing one in Settings, on by default,
/// and a per-map one under it - and the pair read as one thing while doing
/// two.
/// </para>
/// <para>
/// Both are off until somebody turns them on, and the Store edition offers
/// neither: it is built so that its connection checks are all it sends. The
/// update check and the connection checks have switches of their own, in the
/// settings they always had.
/// </para>
/// </remarks>
public sealed class OutsideContacts
{
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _store;
    private readonly NetworkRouteCache _routes;
    private readonly HostNameCache _hosts;
    private readonly Blocklists _blocklists;
    private readonly ReputationService _reputation;

    public OutsideContacts(
        WindsockSettings settings,
        ISettingsStore store,
        NetworkRouteCache routes,
        HostNameCache hosts,
        Blocklists blocklists,
        ReputationService reputation)
    {
        ArgumentNullException.ThrowIfNull(blocklists);
        ArgumentNullException.ThrowIfNull(reputation);

        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(hosts);

        _settings = settings;
        _store = store;
        _routes = routes;
        _hosts = hosts;
        _blocklists = blocklists;
        _reputation = reputation;

        _routes.IsEnabled = TraceRoutes;
        _hosts.AskResolver = ReverseLookups;
    }

    /// <summary>Raised when either switch changes, from whichever place changed it.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Whether this edition can trace routes or look names up at all. The Store
    /// edition cannot, and shows neither switch.
    /// </summary>
#if STORE
    public static bool AreOffered => false;
#else
    public static bool AreOffered => true;
#endif

    /// <summary>Whether the way to each place drawn is traced, hop by hop.</summary>
    public bool TraceRoutes
    {
        get => AreOffered && _settings.TraceRoutes;
        set
        {
            if (!AreOffered || value == _settings.TraceRoutes)
            {
                return;
            }

            _settings.TraceRoutes = value;
            _store.Save(_settings);
            _routes.IsEnabled = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Whether an address the DNS cache cannot name is looked up in reverse.</summary>
    public bool ReverseLookups
    {
        get => AreOffered && _settings.ReverseLookups;
        set
        {
            if (!AreOffered || value == _settings.ReverseLookups)
            {
                return;
            }

            _settings.ReverseLookups = value;
            _store.Save(_settings);
            _hosts.AskResolver = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Whether Spamhaus DROP is fetched from spamhaus.org, at most once an
    /// hour, and connections to its netblocks marked. Both editions; off
    /// until switched on. The same setting as the Activity page's.
    /// </summary>
    public bool SpamhausDrop
    {
        get => _settings.Insight.SpamhausDrop;
        set => SetList(value, _settings.Insight.SpamhausDrop, on => _settings.Insight.SpamhausDrop = on);
    }

    /// <summary>Whether abuse.ch's Feodo Tracker is fetched, likewise.</summary>
    public bool FeodoTracker
    {
        get => _settings.Insight.FeodoTracker;
        set => SetList(value, _settings.Insight.FeodoTracker, on => _settings.Insight.FeodoTracker = on);
    }

    /// <summary>
    /// Whether a VirusTotal key is kept, which is the standing permission to
    /// send a program's hash when someone asks. Not a switch: the key is.
    /// </summary>
    public bool HasVirusTotalKey => _reputation.HasKey;

    /// <summary>Keeps a VirusTotal key, protected for this Windows account. False when it could not be kept.</summary>
    public bool SetVirusTotalKey(string key)
    {
        bool kept = _reputation.SetKey(key);
        Changed?.Invoke(this, EventArgs.Empty);
        return kept;
    }

    /// <summary>Forgets the VirusTotal key and every answer kept.</summary>
    public void ClearVirusTotalKey()
    {
        _reputation.ClearKey();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetList(bool value, bool current, Action<bool> set)
    {
        if (value == current)
        {
            return;
        }

        set(value);
        _store.Save(_settings);

        // Fetches a list just switched on, or deletes the copy of one switched off, now.
        _blocklists.Apply();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
