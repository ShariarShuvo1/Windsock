using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Connectivity;
using Windsock.Core.Settings;
using Windsock.Core.SpeedTests;

namespace Windsock.App.ViewModels;

/// <summary>
/// The connection tools' rows on Settings › Data and privacy: each outside
/// contact the Connection pages can make, where it would go, and its switch.
/// </summary>
/// <remarks>
/// The rows set the same settings the Connection pages do, and those pages read
/// them again when they come on screen. The M-Lab agreement and the LibreSpeed
/// server can only be switched off here: agreeing belongs beside what is agreed
/// to, and a server needs an address typed, both on Connection › Speed test.
/// </remarks>
public sealed partial class ConnectionToolsPrivacyViewModel(
    WindsockSettings settings,
    ISettingsStore store,
    SpeedTestScheduler scheduler) : ObservableObject
{
    public ObservableCollection<ContactRowViewModel> Rows { get; } = [];

    /// <summary>Builds the rows from the settings as they are now.</summary>
    public void Refresh()
    {
        Rows.Clear();

        foreach (OutsideContact contact in OutsideContactCatalogue.All)
        {
#if STORE
            if (contact.FullEditionOnly)
            {
                continue;
            }
#endif
            Rows.Add(new ContactRowViewModel(contact, settings, Apply));
        }
    }

    private void Apply(OutsideContact contact, bool on)
    {
        contact.Allow(settings, on);
        store.Save(settings);
        scheduler.Reschedule();

        // One switch can turn another off: no agreement, no automatic M-Lab tests.
        foreach (ContactRowViewModel row in Rows)
        {
            row.Reread();
        }
    }
}

/// <summary>One outside contact, as a row with a switch.</summary>
public sealed partial class ContactRowViewModel : ObservableObject
{
    private readonly OutsideContact _contact;
    private readonly WindsockSettings _settings;
    private readonly Action<OutsideContact, bool> _apply;
    private bool _reading;

    internal ContactRowViewModel(OutsideContact contact, WindsockSettings settings, Action<OutsideContact, bool> apply)
    {
        _contact = contact;
        _settings = settings;
        _apply = apply;

        Title = contact.Title;
        Description = contact.Description;
        Page = contact.Key.StartsWith("speed-test", StringComparison.Ordinal) ? "speedtest" : "status";
        PageName = Page == "speedtest" ? "Connection › Speed test" : "Connection › Status";
        Reread();
    }

    public string Title { get; }

    public string Description { get; }

    /// <summary>The link name of the page the contact is made from.</summary>
    public string Page { get; }

    public string PageName { get; }

    public string SwitchText => _contact.Key switch
    {
        "public-address" => "Allow looking it up",
        "speed-test-mlab" => "I agree",
        "speed-test-librespeed" => "Use my server",
        "speed-test-schedule" => "Run automatically",
        "nat-test" => "Allow asking",
        _ => "Allow timing",
    };

    public string OpenName => string.Concat("Open ", PageName);

    /// <summary>
    /// The page button's name for a screen reader: every row has one, so each
    /// says which contact it is for.
    /// </summary>
    public string OpenFor => string.Concat(OpenName, " for ", Title);

    /// <summary>What a screen reader names the row by: the contact and whether it is allowed.</summary>
    public override string ToString() => string.Concat(Title, IsOn ? ", allowed" : ", off");

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    /// <summary>Whether the switch can be pressed: always to turn off, and to turn on only where the row may.</summary>
    [ObservableProperty]
    public partial bool CanSwitch { get; private set; }

    [ObservableProperty]
    public partial string Note { get; private set; } = string.Empty;

    internal void Reread()
    {
        _reading = true;

        try
        {
            IsOn = _contact.IsAllowed(_settings);
            CanSwitch = IsOn || _contact.CanAllowHere;
            string where = _contact.Destination(_settings);
            Note = IsOn || _contact.CanAllowHere
                ? string.Concat("Goes to ", where, " · from ", PageName)
                : _contact.Key == OutsideContactCatalogue.MeasurementLab.Key
                    ? string.Concat("Agree on ", PageName, ", beside what M-Lab publishes")
                    : string.Concat("Type your server's address on ", PageName);
        }
        finally
        {
            _reading = false;
        }
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_reading)
        {
            _apply(_contact, value);
        }
    }
}
