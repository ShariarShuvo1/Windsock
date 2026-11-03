using System.Globalization;
using Microsoft.Extensions.Hosting;
using Windsock.Core.Notifications;
using Windsock.Core.Security;
using Windsock.Core.Settings;

namespace Windsock.Core.Insight;

/// <summary>
/// Turns a program's first use of the network into an alert, while that alert
/// is switched on.
/// </summary>
/// <remarks>
/// Sent through the one notice sink, so the alert desk applies quiet hours,
/// the snooze, the record and forwarding to it like any other alert. The
/// watcher keeps watching whether or not the alert is on - its list of apps
/// seen must stay current, or switching the alert on later would announce every
/// app that started using the network in between.
/// </remarks>
public sealed class NewAppNotices : BackgroundService
{
    private readonly NewAppWatcher _watcher;
    private readonly INoticeSink _sink;
    private readonly WindsockSettings _settings;

    public NewAppNotices(NewAppWatcher watcher, INoticeSink sink, WindsockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(settings);

        _watcher = watcher;
        _sink = sink;
        _settings = settings;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _watcher.Sighted += OnSighted;
        stoppingToken.Register(() => _watcher.Sighted -= OnSighted);
        return Task.CompletedTask;
    }

    private void OnSighted(object? sender, NewAppSighting sighting)
    {
        if (_settings.Insight.NewAppNotices)
        {
            _sink.Send(For(sighting));
        }
    }

    /// <summary>The notice for one sighting: the app, who signed it, and where it went, in two lines.</summary>
    public static Notice For(NewAppSighting sighting)
    {
        ArgumentNullException.ThrowIfNull(sighting);

        string signer = sighting.Signature switch
        {
            { State: SignatureState.Trusted, Publisher: { } publisher } => string.Concat("Signed by ", publisher, "."),
            { State: SignatureState.NoSignature } => "It is not signed, so nothing says who made it.",
            { State: SignatureState.NotTrusted } => "It is signed, but the signature does not hold.",
            { State: SignatureState.Unconfirmed } => "It is signed, but Windows could not confirm the signature offline.",
            _ => "Who made it is not known.",
        };

        string message = sighting.RemoteAddress is { } address
            ? string.Format(CultureInfo.CurrentCulture, "{0} used the network for the first time, talking to {1}. {2}", sighting.Name, address, signer)
            : string.Format(CultureInfo.CurrentCulture, "{0} used the network for the first time. {1}", sighting.Name, signer);

        return new Notice(NoticeTopics.NewApp, "New app online", message)
        {
            Opens = "apps",
            Quiet = sighting.Signature?.State == SignatureState.Trusted,
        };
    }
}
