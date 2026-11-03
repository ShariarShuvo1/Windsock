using Microsoft.Extensions.Hosting;
using Windsock.Core.Notifications;
using Windsock.Core.Settings;

namespace Windsock.Core.Home;

/// <summary>
/// Says when a device joins the home network for the first time, through the
/// app's notifications, while <see cref="HomeSettings.NotifyNewDevices"/> is on.
/// </summary>
/// <remarks>
/// The notice is phrased as news, not a warning: a new phone, a guest, a smart
/// plug. Each device's notice carries a tag of its own, so a device that comes
/// and goes replaces its own notice rather than stacking them.
/// </remarks>
public sealed class DeviceJoinedNotices(HomeDevices devices, INoticeSink notices, WindsockSettings settings) : IHostedService
{
    /// <summary>The topic these notices are sent under, which can be switched off on its own.</summary>
    public const string Topic = "device-joined";

    private readonly HomeDevices _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    private readonly INoticeSink _notices = notices ?? throw new ArgumentNullException(nameof(notices));
    private readonly WindsockSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _devices.Joined += OnJoined;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _devices.Joined -= OnJoined;
        return Task.CompletedTask;
    }

    /// <summary>The notice for a device that joined.</summary>
    public static Notice For(DeviceJoined joined)
    {
        ArgumentNullException.ThrowIfNull(joined);

        HomeDevice device = joined.Device;
        string who = device.Name ?? device.Maker ?? (device.HasPrivateAddress ? "A device with a private address" : "A device");
        string where = device.Address?.ToString() ?? device.Mac.ToString();
        string maker = device.Name is not null && device.Maker is not null ? string.Concat(" (", device.Maker, ")") : string.Empty;

        return new Notice(
            Topic,
            string.Concat("New device on ", joined.NetworkName),
            string.Concat(who, maker, " joined at ", where, "."))
        {
            Tag = string.Concat("device-", device.Mac.ToString()),
        };
    }

    private void OnJoined(object? sender, DeviceJoined joined)
    {
        if (_settings.Home.NotifyNewDevices)
        {
            _ = _notices.Send(For(joined));
        }
    }
}
