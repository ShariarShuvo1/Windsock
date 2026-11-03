using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.History;
using Windsock.Core.Insight;

namespace Windsock.Core.Home;

/// <summary>
/// Erasing history takes the home device list and the router reports with it:
/// all of it, or the devices first seen in an erased period.
/// </summary>
/// <remarks>
/// History's <c>Erased</c> event says only that something was erased, so this
/// acts on it when nothing is left - the "delete all history" case - as the
/// connection timeline does. A period erased on the History page reaches it as
/// an <see cref="IErasedWithHistory"/>, which the page calls with the period.
/// </remarks>
public sealed partial class HomeEraseFollower(
    IUsageHistoryStore history,
    IHomeStore home,
    HomeDevices devices,
    ILogger<HomeEraseFollower> logger) : IHostedService, IErasedWithHistory
{
    private readonly IUsageHistoryStore _history = history ?? throw new ArgumentNullException(nameof(history));
    private readonly IHomeStore _home = home ?? throw new ArgumentNullException(nameof(home));
    private readonly HomeDevices _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    private readonly ILogger<HomeEraseFollower> _logger = logger;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _history.Erased += OnErased;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _history.Erased -= OnErased;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public int Delete(DateTimeOffset from, DateTimeOffset until)
    {
        int gone = _home.Delete(from, until);
        _devices.Refresh();
        return gone;
    }

    private void OnErased(object? sender, EventArgs e)
    {
        try
        {
            if (_history.Extent().HasData)
            {
                return;
            }

            _home.RemoveEverything();
            _devices.Refresh();
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogEraseFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The home device list could not be erased with history")]
    private partial void LogEraseFailed(Exception exception);
}
