using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Windsock.Core.Alerts;
using Windsock.Core.Notifications;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The proxy alert's memory: what the proxy was when Windsock last looked, so a
/// proxy set while it was closed is told at the next start. Against registry
/// keys of the test's own; the real proxy settings are never touched.
/// </summary>
public sealed class ProxyAlertsTests : IDisposable
{
    private readonly string _root = @"Software\WindsockTests\" + Guid.NewGuid().ToString("N");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "windsock-proxy-alerts-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAlertLog _log;
    private readonly WindsockSettings _settings = new();
    private readonly List<Notice> _told = [];
    private readonly Clock _clock = new(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero));

    public ProxyAlertsTests()
    {
        Directory.CreateDirectory(_folder);
        _log = new SqliteAlertLog(Path.Combine(_folder, "Alerts.db"));

        using RegistryKey settings = Registry.CurrentUser.CreateSubKey(SettingsKey);
        using RegistryKey connections = Registry.CurrentUser.CreateSubKey(SettingsKey + @"\Connections");
        settings.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        settings.SetValue("ProxyServer", "127.0.0.1:9999");

        _settings.Alerts.Changes.Proxy = true;
    }

    private string SettingsKey => _root + @"\Internet Settings";

    public void Dispose()
    {
        _log.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        Directory.Delete(_folder, recursive: true);
    }

    private ProxyAlerts Alerts(ProxyWatch watch, AlertSettling settling) =>
        new(watch, _settings, new NoticeSink(notice =>
        {
            _told.Add(notice);
            return true;
        }), settling, _log, _clock);

    private ProxyWatch Watch() => new(NullLogger<ProxyWatch>.Instance, SettingsKey, SettingsKey + @"\Connections");

    [Fact]
    public void AProxySetWhileWindsockWasClosed_IsToldAtTheNextStart()
    {
        _log.WriteState(ProxyAlerts.StateKey, JsonSerializer.Serialize(ProxyPrint.None));
        AlertSettling settling = new(_clock);
        _clock.Advance(TimeSpan.FromMinutes(2));

        using ProxyWatch watch = Watch();
        using ProxyAlerts alerts = Alerts(watch, settling);
        alerts.Apply();

        Notice told = Assert.Single(_told);
        Assert.Equal(NoticeTopics.ProxyChanged, told.Topic);
        Assert.Contains("127.0.0.1:9999", told.Message, StringComparison.Ordinal);
        Assert.Contains("while Windsock was not running", told.Message, StringComparison.Ordinal);
        Assert.Contains("9999", _log.ReadState(ProxyAlerts.StateKey), StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchedOnForTheFirstTime_TakesTheProxyAsItStands()
    {
        AlertSettling settling = new(_clock);
        _clock.Advance(TimeSpan.FromMinutes(2));

        using ProxyWatch watch = Watch();
        using ProxyAlerts alerts = Alerts(watch, settling);
        alerts.Apply();

        Assert.Empty(_told);
        Assert.NotNull(_log.ReadState(ProxyAlerts.StateKey));
    }

    [Fact]
    public void SwitchedOff_ForgetsTheProxy_SoSwitchingOnAgainTellsNothing()
    {
        AlertSettling settling = new(_clock);
        _clock.Advance(TimeSpan.FromMinutes(2));

        using ProxyWatch watch = Watch();
        using ProxyAlerts alerts = Alerts(watch, settling);
        alerts.Apply();

        _settings.Alerts.Changes.Proxy = false;
        alerts.Apply();
        Assert.Null(_log.ReadState(ProxyAlerts.StateKey));

        _settings.Alerts.Changes.Proxy = true;
        alerts.Apply();
        Assert.Empty(_told);
    }

    [Fact]
    public void InTheMinuteAfterStarting_AChangeWaitsRatherThanGoing()
    {
        _log.WriteState(ProxyAlerts.StateKey, JsonSerializer.Serialize(ProxyPrint.None));
        AlertSettling settling = new(_clock);

        using ProxyWatch watch = Watch();
        using ProxyAlerts alerts = Alerts(watch, settling);
        alerts.Apply();

        // Held for the end of the minute, not dropped: nothing yet, and the proxy as it stands is remembered.
        Assert.Empty(_told);
        Assert.Contains("9999", _log.ReadState(ProxyAlerts.StateKey), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePrint_RoundTripsThroughTheRecord()
    {
        ProxyPrint print = new(true, "proxy:8080", "<local>", "http://wpad/proxy.pac", AutoDetect: true);

        ProxyPrint? read = JsonSerializer.Deserialize<ProxyPrint>(JsonSerializer.Serialize(print));

        Assert.Equal(print, read);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
