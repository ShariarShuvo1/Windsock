using System.Xml.Linq;
using Microsoft.Win32;
using Windsock.Core.Notifications;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class ToastXmlTests
{
    private static readonly Notice Plain = new("connection", "Connection lost", "Your internet stopped answering.");

    [Fact]
    public void ANotice_BecomesTheTwoLinesWindowsDraws()
    {
        XElement toast = XElement.Parse(ToastXml.Build(Plain, sound: true));

        string[] lines = [.. toast.Descendants("text").Select(text => text.Value)];

        Assert.Equal(["Connection lost", "Your internet stopped answering."], lines);
        Assert.Equal("ToastGeneric", toast.Descendants("binding").Single().Attribute("template")?.Value);
    }

    [Fact]
    public void TextFromElsewhere_CannotBreakTheMarkup()
    {
        Notice hostile = new("connection", "Café & <Friends>", "Network \"Home\" & 'Office' </text><audio silent=\"true\"/>");

        XElement toast = XElement.Parse(ToastXml.Build(hostile, sound: true));

        Assert.Equal("Café & <Friends>", toast.Descendants("text").First().Value);
        Assert.Empty(toast.Descendants("audio"));
    }

    [Fact]
    public void ControlCharacters_AreDropped()
    {
        Notice odd = new("test", "A\u0007B", "C\u0000D");

        XElement toast = XElement.Parse(ToastXml.Build(odd, sound: true));

        Assert.Equal(["AB", "CD"], toast.Descendants("text").Select(text => text.Value));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Silence_FollowsTheSettingAndTheNotice(bool sound, bool quiet, bool silent)
    {
        XElement toast = XElement.Parse(ToastXml.Build(Plain with { Quiet = quiet }, sound));

        Assert.Equal(silent, toast.Descendants("audio").Any(audio => (string?)audio.Attribute("silent") == "true"));
    }

    [Fact]
    public void TheLaunchText_NamesTheSectionToOpen()
    {
        XElement toast = XElement.Parse(ToastXml.Build(Plain with { Opens = "history" }, sound: true));

        Assert.Equal("history", NoticeLaunch.Section((string?)toast.Attribute("launch")));
    }

    [Fact]
    public void LongText_IsCutAtAWordWithAnEllipsis()
    {
        string clipped = ToastXml.Clip("The quick brown fox jumps over the lazy dog", 20);

        Assert.True(clipped.Length <= 20);
        Assert.EndsWith("…", clipped, StringComparison.Ordinal);
        Assert.StartsWith("The quick brown", clipped, StringComparison.Ordinal);
    }

    [Fact]
    public void TextThatFits_IsLeftAlone() =>
        Assert.Equal("Back online", ToastXml.Clip("  Back online ", 20));
}

public sealed class NoticeLaunchTests
{
    [Theory]
    [InlineData("settings")]
    [InlineData("history")]
    [InlineData("connection")]
    public void ASection_SurvivesTheRoundTrip(string section) =>
        Assert.Equal(section, NoticeLaunch.Section(NoticeLaunch.Format(section)));

    [Fact]
    public void NoSection_MeansTheWindowAsItWas()
    {
        Assert.Equal("action=open", NoticeLaunch.Format(null));
        Assert.Null(NoticeLaunch.Section(NoticeLaunch.Format(null)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("section=history")]
    [InlineData("action=delete&section=history")]
    [InlineData("action=open&section=../../etc")]
    [InlineData("action=open&section=History")]
    [InlineData("action=open&section=http://example.com")]
    [InlineData("action=open&section=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void AnythingElse_OpensNoSection(string? launch) =>
        Assert.Null(NoticeLaunch.Section(launch));

    [Fact]
    public void AnUnsafeSectionName_IsNotWritten() =>
        Assert.Equal("action=open", NoticeLaunch.Format("Settings & more"));
}

public sealed class NoticePacerTests
{
    private static Notice Say(string message, string topic = "connection") => new(topic, "Title", message);

    [Fact]
    public void TheSameNotice_IsNotRepeatedWithinAMinute()
    {
        Clock clock = new();
        NoticePacer pacer = new(clock);

        Assert.True(pacer.Admit(Say("Lost")));
        clock.Now += TimeSpan.FromSeconds(30);
        Assert.False(pacer.Admit(Say("Lost")));

        clock.Now += TimeSpan.FromSeconds(31);
        Assert.True(pacer.Admit(Say("Lost")));
    }

    [Fact]
    public void DifferentNotices_AreNotHeldBackByEachOther()
    {
        NoticePacer pacer = new(new Clock());

        Assert.True(pacer.Admit(Say("Lost")));
        Assert.True(pacer.Admit(Say("Back online")));
    }

    [Fact]
    public void ABurst_IsCappedAndThenLetThroughAgain()
    {
        Clock clock = new();
        NoticePacer pacer = new(clock);

        for (int each = 0; each < NoticePacer.BurstLimit; each++)
        {
            Assert.True(pacer.Admit(Say("Message " + each)));
        }

        Assert.False(pacer.Admit(Say("One too many")));

        clock.Now += NoticePacer.BurstWindow;
        Assert.True(pacer.Admit(Say("After the window")));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 13, 21, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

public sealed class NotificationRegistrationTests : IDisposable
{
    private readonly string _path = @"Software\WindsockTests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _classes;

    public NotificationRegistrationTests() => _classes = Registry.CurrentUser.CreateSubKey(_path, writable: true);

    public void Dispose()
    {
        _classes.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
    }

    [Fact]
    public void TheRecord_NamesTheAppAndTheClickClass()
    {
        NotificationRegistration.Write(_classes, @"C:\Apps\Windsock\Windsock.exe", @"C:\Apps\Windsock\Assets\Notification.png");

        using RegistryKey? app = _classes.OpenSubKey(@"AppUserModelId\" + NotificationRegistration.AppId);
        using RegistryKey? server = _classes.OpenSubKey(@"CLSID\" + NotificationRegistration.Activator.ToString("B").ToUpperInvariant() + @"\LocalServer32");

        Assert.NotNull(app);
        Assert.Equal("Windsock", app.GetValue("DisplayName"));
        Assert.Equal(@"C:\Apps\Windsock\Assets\Notification.png", app.GetValue("IconUri"));
        Assert.Equal(NotificationRegistration.Activator.ToString("B").ToUpperInvariant(), app.GetValue("CustomActivator"));

        Assert.NotNull(server);
        Assert.Equal("\"C:\\Apps\\Windsock\\Windsock.exe\" -ToastActivated", server.GetValue(string.Empty));
    }

    [Fact]
    public void Rewriting_FollowsAProgramThatMoved_AndDropsAMissingIcon()
    {
        NotificationRegistration.Write(_classes, @"C:\Old\Windsock.exe", @"C:\Old\Notification.png");
        NotificationRegistration.Write(_classes, @"D:\Portable Apps\Windsock.exe", icon: null);

        using RegistryKey? app = _classes.OpenSubKey(@"AppUserModelId\" + NotificationRegistration.AppId);
        using RegistryKey? server = _classes.OpenSubKey(@"CLSID\" + NotificationRegistration.Activator.ToString("B").ToUpperInvariant() + @"\LocalServer32");

        Assert.Null(app?.GetValue("IconUri"));
        Assert.Equal("\"D:\\Portable Apps\\Windsock.exe\" -ToastActivated", server?.GetValue(string.Empty));
    }

    [Fact]
    public void Removing_LeavesNothingBehind_AndIsSafeTwice()
    {
        NotificationRegistration.Write(_classes, @"C:\Apps\Windsock.exe", icon: null);
        Assert.True(NotificationRegistration.Exists(_classes));

        NotificationRegistration.Remove(_classes);
        NotificationRegistration.Remove(_classes);

        Assert.False(NotificationRegistration.Exists(_classes));
        Assert.Null(_classes.OpenSubKey("CLSID\\" + NotificationRegistration.Activator.ToString("B").ToUpperInvariant()));
    }

    [Theory]
    [InlineData(new[] { "-ToastActivated" }, true)]
    [InlineData(new[] { "-toastactivated" }, true)]
    [InlineData(new[] { "--background" }, false)]
    [InlineData(new string[0], false)]
    public void AClickLaunch_IsRecognised(string[] arguments, bool expected) =>
        Assert.Equal(expected, NotificationRegistration.IsActivation(arguments));
}

public sealed class NotificationSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Windsock-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Notifications_StartOnWithSound()
    {
        WindsockSettings settings = new();

        Assert.True(settings.Notifications.Enabled);
        Assert.True(settings.Notifications.Sound);
    }

    [Fact]
    public void TheChoices_SurviveBeingSaved()
    {
        SettingsStore store = new(Path.Combine(_directory, "settings.json"));

        store.Save(new WindsockSettings { Notifications = new NotificationSettings { Enabled = false, Sound = false } });
        WindsockSettings loaded = store.Load();

        Assert.False(loaded.Notifications.Enabled);
        Assert.False(loaded.Notifications.Sound);
    }

    [Fact]
    public void AFileSavedBeforeNotifications_OpensWithThemOn()
    {
        Directory.CreateDirectory(_directory);
        string file = Path.Combine(_directory, "settings.json");
        File.WriteAllText(file, """{ "Theme": "Dark" }""");

        WindsockSettings loaded = new SettingsStore(file).Load();

        Assert.True(loaded.Notifications.Enabled);
        Assert.True(loaded.Notifications.Sound);
    }

    [Fact]
    public void ANullSection_FallsBackToTheDefaults()
    {
        WindsockSettings settings = new() { Notifications = null! };

        Assert.True(settings.Notifications.Enabled);
    }
}
