using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.App.Services;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>
/// The meter's two outside contacts, as rows of Settings › Data and privacy:
/// the feeds its places may read, and keeping its public address current.
/// Both start off; each is the same setting the Taskbar page shows.
/// </summary>
public sealed partial class MeterPrivacyViewModel : ObservableObject
{
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _store;
    private readonly TaskbarMeter _meter;
    private bool _reading;

    public MeterPrivacyViewModel(WindsockSettings settings, ISettingsStore store, TaskbarMeter meter)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _store = store;
        _meter = meter;
        Refresh();
    }

    /// <summary>Whether places on the meter may read web and local feeds.</summary>
    [ObservableProperty]
    public partial bool Feeds { get; set; }

    /// <summary>Where the feeds on the meter now read from, in a line.</summary>
    [ObservableProperty]
    public partial string FeedsNote { get; private set; } = string.Empty;

    /// <summary>Whether the meter's public address keeps itself current.</summary>
    [ObservableProperty]
    public partial bool RefreshPublicAddress { get; set; }

    /// <summary>Whether that can be switched on: the public address service's own switch is on.</summary>
    [ObservableProperty]
    public partial bool CanRefreshPublicAddress { get; private set; }

    /// <summary>What keeping the address current depends on, in a line.</summary>
    [ObservableProperty]
    public partial string PublicAddressNote { get; private set; } = string.Empty;

    /// <summary>Reads both again, since the Taskbar page and Connection set the same switches.</summary>
    public void Refresh()
    {
        TaskbarSettings meter = _settings.Taskbar;
        List<MeterSlot> places = [.. meter.Slots.Take(meter.Rows * meter.Columns)];

        _reading = true;
        Feeds = meter.Feeds;
        RefreshPublicAddress = meter.RefreshPublicAddress;
        _reading = false;

        List<string> addresses =
        [
            .. places
                .Where(slot => slot is { Field: MeterField.Feed, Feed.Address.Length: > 0 })
                .Select(slot => slot.Feed!.Address)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

        FeedsNote = addresses.Count == 0
            ? "No place on the meter reads a feed."
            : string.Concat(meter.Feeds ? "Asks " : "Would ask ", string.Join(", ", addresses), ".");

        CanRefreshPublicAddress = _settings.ConnectionTools.PublicAddressLookups;
        bool shown = places.Any(slot => slot.Fields().Contains(MeterField.PublicAddress));

        PublicAddressNote = !CanRefreshPublicAddress
            ? "Needs the public address lookup, in this list, switched on first."
            : shown
                ? "The meter shows the public address."
                : "No place on the meter shows the public address.";
    }

    partial void OnFeedsChanged(bool value) => Save(meter => meter.Feeds = value);

    partial void OnRefreshPublicAddressChanged(bool value) => Save(meter => meter.RefreshPublicAddress = value);

    private void Save(Action<TaskbarSettings> edit)
    {
        if (_reading)
        {
            return;
        }

        edit(_settings.Taskbar);
        _store.Save(_settings);
        _meter.Apply();
        Refresh();
    }
}
