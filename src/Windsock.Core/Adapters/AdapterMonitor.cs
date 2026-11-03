using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Windsock.Core.Adapters;

/// <summary>What Windows says about one adapter's network profile.</summary>
/// <param name="NetworkName">The profile's name, such as "Home" or "Network 3".</param>
/// <param name="Cost">How the network is charged.</param>
public sealed record AdapterProfile(string? NetworkName, AdapterCost? Cost);

/// <summary>
/// Reads the network profiles behind adapters - their names and whether they
/// are metered - which only Windows' WinRT networking API knows.
/// </summary>
public interface IAdapterProfileSource
{
    /// <summary>Each connected adapter's profile, by adapter GUID. Never throws.</summary>
    IReadOnlyDictionary<Guid, AdapterProfile> Read();
}

/// <summary>Everything the Adapters page shows, read at one moment.</summary>
/// <param name="Snapshot">Every adapter.</param>
/// <param name="Windows">Windows' connectivity flags.</param>
/// <param name="Ipv6">The IPv6 verdict drawn from both.</param>
public sealed record AdapterReading(AdapterSnapshot Snapshot, WindowsConnectivity Windows, Ipv6State Ipv6)
{
    /// <summary>Nothing read yet.</summary>
    public static AdapterReading Empty { get; } = new(AdapterSnapshot.Empty, new WindowsConnectivity(null), Ipv6State.Unknown);
}

/// <summary>
/// Keeps every adapter's details current while anyone is watching: read again
/// when Windows reports an address or availability change, and once a minute
/// for the link speeds, which change without telling anyone.
/// </summary>
/// <remarks>
/// Nothing is read while nobody watches, beyond a one-off <see cref="ReadNowAsync"/>.
/// Reads run on the thread pool and never overlap; <see cref="Changed"/> is
/// raised there too.
/// </remarks>
public sealed partial class AdapterMonitor : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    private readonly IAdapterReader _reader;
    private readonly IWindowsConnectivitySource _windows;
    private readonly IAdapterProfileSource? _profiles;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _reading = new(1, 1);

    private int _watchers;
    private Timer? _settle;
    private Timer? _beat;
    private bool _disposed;

    public AdapterMonitor(
        IAdapterReader reader,
        IWindowsConnectivitySource windows,
        IAdapterProfileSource? profiles = null,
        ILogger<AdapterMonitor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(windows);

        _reader = reader;
        _windows = windows;
        _profiles = profiles;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Raised on the thread pool after each read.</summary>
    public event EventHandler<AdapterReading>? Changed;

    /// <summary>The most recent read.</summary>
    public AdapterReading Latest { get; private set; } = AdapterReading.Empty;

    /// <summary>Asks for the adapters to be kept current until the token is let go.</summary>
    public IDisposable Watch()
    {
        bool first;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            first = _watchers++ == 0;

            if (first)
            {
                _settle = new Timer(_ => _ = ReadNowAsync(), null, Timeout.Infinite, Timeout.Infinite);
                _beat = new Timer(_ => _ = ReadNowAsync(), null, Every, Every);
            }
        }

        if (first)
        {
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
            _ = ReadNowAsync();
        }

        return new Watcher(this);
    }

    /// <summary>Reads every adapter now, whether or not anyone watches, and returns the reading.</summary>
    public async Task<AdapterReading> ReadNowAsync()
    {
        await _reading.WaitAsync().ConfigureAwait(false);

        try
        {
            AdapterReading reading = await Task.Run(Read).ConfigureAwait(false);
            Latest = reading;
            Changed?.Invoke(this, reading);
            return reading;
        }
        finally
        {
            _reading.Release();
        }
    }

    private AdapterReading Read()
    {
        try
        {
            IReadOnlyList<AdapterDetails> adapters = _reader.Read();
            IReadOnlyDictionary<Guid, AdapterProfile> profiles = ReadProfiles();

            if (profiles.Count > 0)
            {
                adapters = [.. adapters.Select(each =>
                    Guid.TryParse(each.Id, out Guid id) && profiles.TryGetValue(id, out AdapterProfile? profile)
                        ? each with { Cost = profile.Cost, NetworkName = profile.NetworkName }
                        : each)];
            }

            AdapterSnapshot snapshot = new(DateTimeOffset.Now, adapters);
            WindowsConnectivity windows = _windows.Read();

            return new AdapterReading(snapshot, windows, Ipv6Judge.Judge(windows, snapshot));
        }
#pragma warning disable CA1031 // A failed read is an empty reading; the page says so rather than the app falling over.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReadFailed(ex);
            return AdapterReading.Empty with { Snapshot = new AdapterSnapshot(DateTimeOffset.Now, []) };
        }
    }

    private IReadOnlyDictionary<Guid, AdapterProfile> ReadProfiles()
    {
        if (_profiles is null)
        {
            return new Dictionary<Guid, AdapterProfile>();
        }

        try
        {
            return _profiles.Read();
        }
#pragma warning disable CA1031 // Without the profiles the adapters are still worth showing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogProfilesFailed(ex);
            return new Dictionary<Guid, AdapterProfile>();
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Soon();

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Soon();

    private void Soon()
    {
        lock (_gate)
        {
            _settle?.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    private void Release()
    {
        bool last;
        Timer? settle;
        Timer? beat;

        lock (_gate)
        {
            last = _watchers > 0 && --_watchers == 0;
            settle = last ? _settle : null;
            beat = last ? _beat : null;

            if (last)
            {
                _settle = null;
                _beat = null;
            }
        }

        if (last)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
            settle?.Dispose();
            beat?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _watchers = 1;
        }

        Release();
        _reading.Dispose();
    }

    private sealed class Watcher(AdapterMonitor monitor) : IDisposable
    {
        private AdapterMonitor? _monitor = monitor;

        public void Dispose() => Interlocked.Exchange(ref _monitor, null)?.Release();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The adapters could not be read")]
    private partial void LogReadFailed(Exception error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Windows' network profiles could not be read")]
    private partial void LogProfilesFailed(Exception error);
}
