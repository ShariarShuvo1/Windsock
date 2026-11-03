using Windsock.Core.Meter;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The meter's two outside contacts - reading feeds, keeping the public
/// address current - are this machine's consent: a settings file or a layout
/// file brings the places that would use them, never the consent itself.
/// </summary>
public sealed class MeterConsentTests
{
    private static WindsockSettings Consenting() => new()
    {
        Taskbar = new TaskbarSettings
        {
            Feeds = true,
            RefreshPublicAddress = true,
            MiniSpot = new MeterSpot { Monitor = "here", X = 1, Y = 2 },
        },
    };

    [Fact]
    public void ImportedSettings_DoNotSwitchConsentOn()
    {
        WindsockSettings here = new();
        WindsockSettings file = Consenting();

        here.CopyFrom(file);

        Assert.False(here.Taskbar.Feeds);
        Assert.False(here.Taskbar.RefreshPublicAddress);
        Assert.Null(here.Taskbar.MiniSpot);
    }

    [Fact]
    public void ImportedSettings_DoNotTakeConsentAway()
    {
        WindsockSettings here = Consenting();
        WindsockSettings file = new();
        file.Taskbar.Rows = 1;

        here.CopyFrom(file);

        Assert.True(here.Taskbar.Feeds);
        Assert.True(here.Taskbar.RefreshPublicAddress);
        Assert.Equal("here", here.Taskbar.MiniSpot?.Monitor);
        Assert.Equal(1, here.Taskbar.Rows);
    }

    private static MeterSlot FeedAt(string address, string path) => new()
    {
        Field = MeterField.Feed,
        Feed = new MeterFeed { Address = address, Path = path, Label = "BTC" },
    };

    private static WindsockSettings Reading(params MeterSlot[] slots)
    {
        WindsockSettings settings = new();
        settings.Taskbar.Rows = 1;
        settings.Taskbar.Columns = slots.Length;
        settings.Taskbar.Slots = [.. slots];

        return settings;
    }

    /// <summary>
    /// A feed's address is a contact this PC would make the moment its feeds
    /// are allowed, so it is never exported or imported: a feed place takes
    /// the address this PC's meter already reads in the same place, or none,
    /// and the import says so.
    /// </summary>
    [Fact]
    public void ImportedSettings_TakeNoFeedAddress_ButThisMachinesOwn()
    {
        WindsockSettings there = Reading(
            FeedAt("https://there.example/price?key=secret", "$.price"),
            FeedAt("https://other.example/rate", "$.rate"));

        WindsockSettings here = Reading(FeedAt("https://here.example/price", "$.price"));
        here.Taskbar.Feeds = true;

        string file = SettingsTransfer.Export(there, DateTimeOffset.UnixEpoch);

        Assert.DoesNotContain("there.example", file, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", file, StringComparison.Ordinal);
        Assert.DoesNotContain("other.example", file, StringComparison.Ordinal);
        Assert.Contains("$.rate", file, StringComparison.Ordinal);

        SettingsImport read = SettingsTransfer.Read(file, here);

        Assert.True(read.IsValid, read.Problem);
        Assert.Equal("https://here.example/price", read.Settings!.Taskbar.Slots[0].Feed?.Address);
        Assert.Equal(string.Empty, read.Settings.Taskbar.Slots[1].Feed?.Address);
        Assert.Equal("$.rate", read.Settings.Taskbar.Slots[1].Feed?.Path);
        Assert.True(read.Settings.Taskbar.Feeds);
        Assert.Contains(read.Notes, note => note.Contains("One feed place", StringComparison.Ordinal));

        // A settings object handed over whole keeps the same rule.
        here.CopyFrom(there);

        Assert.Equal("https://here.example/price", here.Taskbar.Slots[0].Feed?.Address);
        Assert.Equal(string.Empty, here.Taskbar.Slots[1].Feed?.Address);
    }

    /// <summary>A file's address, written in by hand, is not read either.</summary>
    [Fact]
    public void AFeedAddressWrittenIntoAFile_IsNotTaken()
    {
        string file = """
            { "Format": "Windsock settings", "Version": 1,
              "Settings": { "Taskbar": { "Rows": 1, "Columns": 1, "Slots": [ { "Field": "Feed", "Feed": { "Address": "https://there.example/x", "Path": "$.v" } } ] } } }
            """;

        SettingsImport read = SettingsTransfer.Read(file, new WindsockSettings());

        Assert.True(read.IsValid, read.Problem);
        Assert.Equal(string.Empty, read.Settings!.Taskbar.Slots[0].Feed?.Address);
        Assert.Equal("$.v", read.Settings.Taskbar.Slots[0].Feed?.Path);
    }

    /// <summary>
    /// A meter made on a laptop, or in the full edition, cannot put on this
    /// one a reading this PC cannot give: those places are emptied as loading
    /// a layout file empties them, and the import says which.
    /// </summary>
    [Fact]
    public void ImportedSettings_LeaveOutReadingsThisPcCannotShow()
    {
        WindsockSettings there = Reading(
            new MeterSlot { Field = MeterField.BatteryCharge },
            new MeterSlot { Field = MeterField.Download, Rotate = [MeterField.NpuLoad, MeterField.Upload] });

        SettingsImport read = SettingsTransfer.Read(
            SettingsTransfer.Export(there, DateTimeOffset.UnixEpoch),
            new WindsockSettings(),
            seen: null,
            offered: field => field is not (MeterField.BatteryCharge or MeterField.NpuLoad));

        Assert.True(read.IsValid, read.Problem);
        Assert.Equal(MeterField.Empty, read.Settings!.Taskbar.Slots[0].Field);
        Assert.Equal([MeterField.Upload], read.Settings.Taskbar.Slots[1].Rotate);
        Assert.Contains(read.Notes, note => note.Contains("Battery charge", StringComparison.Ordinal) && note.Contains("One place", StringComparison.Ordinal));
    }

    [Fact]
    public void ALayoutFile_NeverCarriesConsent()
    {
        TaskbarSettings source = Consenting().Taskbar;
        string written = MeterLayoutFile.Write(source);

        Assert.DoesNotContain("Feeds\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshPublicAddress", written, StringComparison.Ordinal);

        TaskbarSettings target = new();
        LayoutRead read = MeterLayoutFile.Read(written);

        Assert.NotNull(read.Layout);
        MeterLayoutFile.Apply(read.Layout!, target);

        Assert.False(target.Feeds);
        Assert.False(target.RefreshPublicAddress);
    }

    [Fact]
    public void Consent_CountsAsAChange_AndResetTakesItAway()
    {
        TaskbarSettings meter = Consenting().Taskbar;

        Assert.False(meter.IsDefault);

        meter.Reset();

        Assert.False(meter.Feeds);
        Assert.False(meter.RefreshPublicAddress);
        Assert.True(meter.IsDefault);
    }
}
