using System.Buffers.Binary;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace Windsock.Core.Alerts;

/// <summary>
/// The system proxy as programs using Windows' own internet settings see it:
/// a proxy typed in, a setup script, and automatic detection.
/// </summary>
/// <param name="Manual">Whether a proxy server is switched on.</param>
/// <param name="Server">The proxy server, as written: "127.0.0.1:8080", or per protocol.</param>
/// <param name="Bypass">The addresses that skip the proxy.</param>
/// <param name="Script">The setup script's address, or empty.</param>
/// <param name="AutoDetect">Whether Windows looks for a proxy by itself.</param>
public sealed record ProxyPrint(bool Manual, string Server, string Bypass, string Script, bool AutoDetect)
{
    /// <summary>No proxy, no script, no detection.</summary>
    public static ProxyPrint None { get; } = new(false, string.Empty, string.Empty, string.Empty, false);

    /// <summary>
    /// What takes effect, so the same settings written twice, or a server kept
    /// while switched off, compare equal.
    /// </summary>
    [JsonIgnore]
    public ProxyPrint Effective => new(
        Manual && Server.Length > 0,
        Manual ? Server.Trim() : string.Empty,
        Manual ? Bypass.Trim() : string.Empty,
        Script.Trim(),
        AutoDetect);
}

/// <summary>A change to the system proxy.</summary>
/// <param name="Was">What it was.</param>
/// <param name="Now">What it is now.</param>
/// <param name="ByWindowsSettings">
/// Whether Windows' own proxy settings, or Internet Options, were in front when
/// it changed - which is how a person changes it. Anything else was a program.
/// </param>
public sealed record ProxyChange(ProxyPrint Was, ProxyPrint Now, bool ByWindowsSettings)
{
    /// <summary>
    /// Whether it changed while nothing was watching - Windsock closed, or the
    /// alert off - so who changed it cannot be said.
    /// </summary>
    public bool WhileAway { get; init; }
}

/// <summary>Reads and compares proxy settings, and says what changed.</summary>
public static class ProxyJudge
{
    /// <summary>Where Windows keeps the per-user internet settings.</summary>
    public const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <summary>Where it keeps the connection blob that holds the automatic-detection flag.</summary>
    public const string ConnectionsKey = SettingsKey + @"\Connections";

    // The flags word in DefaultConnectionSettings, as written by Internet Options
    // and Windows' proxy settings since Internet Explorer 5: a version word, a
    // change counter, then the flags; 0x08 is "automatically detect settings".
    private const int FlagsOffset = 8;
    private const uint AutoDetectFlag = 0x08;

    /// <summary>The programs a person changes the proxy with: Windows' Settings, and Internet Options.</summary>
    private static readonly string[] SettingsApps = ["SystemSettings", "rundll32", "control", "inetcpl"];

    /// <summary>Whether two readings differ in anything that takes effect.</summary>
    public static bool Differ(ProxyPrint was, ProxyPrint now)
    {
        ArgumentNullException.ThrowIfNull(was);
        ArgumentNullException.ThrowIfNull(now);

        ProxyPrint a = was.Effective;
        ProxyPrint b = now.Effective;

        return a.Manual != b.Manual
            || a.AutoDetect != b.AutoDetect
            || !string.Equals(a.Server, b.Server, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(a.Bypass, b.Bypass, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(a.Script, b.Script, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether automatic detection is on, from the connection blob, or nothing
    /// when the blob is missing or too short to say.
    /// </summary>
    public static bool? AutoDetect(byte[]? blob) =>
        blob is { Length: >= FlagsOffset + 4 }
            ? (BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(FlagsOffset, 4)) & AutoDetectFlag) != 0
            : null;

    /// <summary>Whether a process, by name, is one a person changes the proxy with.</summary>
    public static bool IsSettingsApp(string? processName) =>
        processName is not null && SettingsApps.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The proxy in words: "a proxy at 127.0.0.1:8080", "no proxy".</summary>
    public static string Describe(ProxyPrint print)
    {
        ArgumentNullException.ThrowIfNull(print);

        ProxyPrint effective = print.Effective;
        List<string> parts = [];

        if (effective.Manual)
        {
            parts.Add(string.Concat("a proxy at ", effective.Server));
        }

        if (effective.Script.Length > 0)
        {
            parts.Add(string.Concat("the setup script at ", effective.Script));
        }

        if (effective.AutoDetect)
        {
            parts.Add("automatic detection");
        }

        return parts.Count switch
        {
            0 => "no proxy",
            1 => parts[0],
            _ => string.Concat(string.Join(", ", parts[..^1]), " and ", parts[^1]),
        };
    }

    /// <summary>Reads the proxy settings as they stand. Never throws; unreadable reads as none.</summary>
    public static ProxyPrint Read() => Read(SettingsKey, ConnectionsKey);

    /// <summary>Reads proxy settings from a pair of keys under the current user; tests point it elsewhere.</summary>
    internal static ProxyPrint Read(string settingsKey, string connectionsKey)
    {
        try
        {
            using RegistryKey? settings = Registry.CurrentUser.OpenSubKey(settingsKey, writable: false);
            using RegistryKey? connections = Registry.CurrentUser.OpenSubKey(connectionsKey, writable: false);

            if (settings is null)
            {
                return ProxyPrint.None;
            }

            return new ProxyPrint(
                settings.GetValue("ProxyEnable") is int enabled && enabled != 0,
                settings.GetValue("ProxyServer") as string ?? string.Empty,
                settings.GetValue("ProxyOverride") as string ?? string.Empty,
                settings.GetValue("AutoConfigURL") as string ?? string.Empty,
                AutoDetect(connections?.GetValue("DefaultConnectionSettings") as byte[]) ?? false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ProxyPrint.None;
        }
    }
}

/// <summary>
/// Hears the system proxy change, the moment Windows writes it, without
/// looking every so often.
/// </summary>
/// <remarks>
/// <para>
/// Windows signals an event when a value under a registry key is written
/// (<c>RegNotifyChangeKeyValue</c>), so nothing is read until something
/// changes: the internet settings key itself, for the proxy server and script,
/// and its Connections key, for automatic detection. Neither is watched below
/// itself, so the zone and cache keys nearby, which change all the time, wake
/// nothing. Writes come in bursts, so a change is read once things settle.
/// </para>
/// <para>
/// <see cref="Changed"/> is raised on the thread pool, only when what takes
/// effect differs. Who made the change is for the caller to judge.
/// </para>
/// </remarks>
public sealed partial class ProxyWatch : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);

    private readonly ILogger<ProxyWatch> _logger;
    private readonly string _settingsKey;
    private readonly string _connectionsKey;
    private readonly Lock _gate = new();
    private readonly List<Watched> _watched = [];

    private Timer? _settle;
    private ProxyPrint _current = ProxyPrint.None;
    private bool _byWindowsSettings;
    private bool _disposed;

    public ProxyWatch(ILogger<ProxyWatch> logger)
        : this(logger, ProxyJudge.SettingsKey, ProxyJudge.ConnectionsKey)
    {
    }

    /// <summary>Watches a pair of keys under the current user; tests point it at keys of their own.</summary>
    internal ProxyWatch(ILogger<ProxyWatch> logger, string settingsKey, string connectionsKey)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _settingsKey = settingsKey;
        _connectionsKey = connectionsKey;
    }

    /// <summary>Raised on the thread pool when the proxy that takes effect changes.</summary>
    public event EventHandler<ProxyChange>? Changed;

    /// <summary>The proxy settings as last read.</summary>
    public ProxyPrint Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Says whether Windows' own proxy settings are in front, asked the moment
    /// a change comes in, while whoever made it is still there.
    /// </summary>
    public Func<bool>? ByWindowsSettings { get; set; }

    /// <summary>Whether it is listening.</summary>
    public bool IsWatching
    {
        get
        {
            lock (_gate)
            {
                return _watched.Count > 0;
            }
        }
    }

    /// <summary>Starts listening, taking the settings as they stand as the starting point.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _watched.Count > 0)
            {
                return;
            }

            _current = ProxyJudge.Read(_settingsKey, _connectionsKey);
            _settle = new Timer(_ => Look(), null, Timeout.Infinite, Timeout.Infinite);

            foreach (string path in new[] { _settingsKey, _connectionsKey })
            {
                if (Watched.Open(path, this) is { } watched)
                {
                    _watched.Add(watched);
                }
            }

            if (_watched.Count == 0)
            {
                LogNotWatched();
            }
        }
    }

    /// <summary>Stops listening.</summary>
    public void Stop()
    {
        Watched[] watched;
        Timer? settle;

        lock (_gate)
        {
            watched = [.. _watched];
            _watched.Clear();
            settle = _settle;
            _settle = null;
        }

        foreach (Watched each in watched)
        {
            each.Dispose();
        }

        settle?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Stop();
    }

    private void OnSignalled()
    {
        // Judged now, while whoever made the change is still in front.
        bool bySettings = ByWindowsSettings?.Invoke() ?? false;

        lock (_gate)
        {
            _byWindowsSettings |= bySettings;
            _settle?.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    private void Look()
    {
        ProxyPrint now = ProxyJudge.Read(_settingsKey, _connectionsKey);
        ProxyPrint was;
        bool bySettings;

        lock (_gate)
        {
            was = _current;
            _current = now;
            bySettings = _byWindowsSettings;
            _byWindowsSettings = false;
        }

        if (ProxyJudge.Differ(was, now))
        {
            Changed?.Invoke(this, new ProxyChange(was, now, bySettings));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The proxy settings could not be watched; proxy changes will not be noticed")]
    private partial void LogNotWatched();

    /// <summary>One key watched, with the event Windows signals and the wait on it.</summary>
    private sealed class Watched : IDisposable
    {
        private readonly RegistryKey _key;
        private readonly AutoResetEvent _signal = new(false);
        private readonly ProxyWatch _owner;
        private RegisteredWaitHandle? _wait;

        private Watched(RegistryKey key, ProxyWatch owner)
        {
            _key = key;
            _owner = owner;
        }

        public static Watched? Open(string path, ProxyWatch owner)
        {
            RegistryKey? key;

            try
            {
                key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }

            if (key is null)
            {
                return null;
            }

            Watched watched = new(key, owner);

            if (!watched.Arm())
            {
                watched.Dispose();
                return null;
            }

            watched._wait = ThreadPool.RegisterWaitForSingleObject(watched._signal, (_, _) => watched.Signalled(), null, Timeout.Infinite, executeOnlyOnce: false);
            return watched;
        }

        public void Dispose()
        {
            _wait?.Unregister(null);
            _wait = null;

            // Closing the key ends the registration Windows holds on it.
            _key.Dispose();
            _signal.Dispose();
        }

        private void Signalled()
        {
            // Each registration fires once; ask again before reading, so a change
            // made while reading is not missed.
            if (Arm())
            {
                _owner.OnSignalled();
            }
        }

        private bool Arm()
        {
            try
            {
                return PInvoke.RegNotifyChangeKeyValue(
                    _key.Handle,
                    false,
                    REG_NOTIFY_FILTER.REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_FILTER.REG_NOTIFY_THREAD_AGNOSTIC,
                    _signal.SafeWaitHandle,
                    true) == WIN32_ERROR.NO_ERROR;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }
}
