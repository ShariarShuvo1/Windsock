using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.App.Services;
using Windsock.App.Views;
using Windsock.Core.Security;

namespace Windsock.App.ViewModels;

/// <summary>
/// The privacy page's rows for what step 7 can send: the two blocklists and
/// VirusTotal lookups. Each blocklist switch is <see cref="OutsideContacts"/>'
/// own setting; VirusTotal's permission is the key itself.
/// </summary>
public sealed partial class InsightPrivacyViewModel : ObservableObject, IDisposable
{
    private static readonly Uri KeyPage = new("https://www.virustotal.com/gui/my-apikey");

    private readonly OutsideContacts _contacts;
    private readonly Blocklists _blocklists;
    private readonly ReputationService _reputation;
    private bool _reading;

    public InsightPrivacyViewModel(OutsideContacts contacts, Blocklists blocklists, ReputationService reputation)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(blocklists);
        ArgumentNullException.ThrowIfNull(reputation);

        _contacts = contacts;
        _blocklists = blocklists;
        _reputation = reputation;

        _contacts.Changed += OnChanged;
        _blocklists.Changed += OnChanged;
        Read();
    }

    /// <summary>Whether Spamhaus DROP is fetched and its netblocks marked.</summary>
    [ObservableProperty]
    public partial bool SpamhausDrop { get; set; }

    /// <summary>Whether Feodo Tracker is fetched and its addresses marked.</summary>
    [ObservableProperty]
    public partial bool FeodoTracker { get; set; }

    /// <summary>When each list was last fetched and how much it holds, or why not.</summary>
    [ObservableProperty]
    public partial string SpamhausNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string FeodoNote { get; private set; } = string.Empty;

    /// <summary>Whether a VirusTotal key is kept.</summary>
    [ObservableProperty]
    public partial bool HasKey { get; private set; }

    /// <summary>What the key row says: how to get one, or that one is kept and how much of today's allowance is used.</summary>
    [ObservableProperty]
    public partial string KeyNote { get; private set; } = string.Empty;

    public void Dispose()
    {
        _contacts.Changed -= OnChanged;
        _blocklists.Changed -= OnChanged;
    }

    partial void OnSpamhausDropChanged(bool value)
    {
        if (!_reading)
        {
            _contacts.SpamhausDrop = value;
        }
    }

    partial void OnFeodoTrackerChanged(bool value)
    {
        if (!_reading)
        {
            _contacts.FeodoTracker = value;
        }
    }

    [RelayCommand]
    private void AddKey()
    {
        System.Windows.Window? owner = System.Windows.Application.Current?.MainWindow;

        if (ApiKeyDialog.Ask(owner) is { } key && !_contacts.SetVirusTotalKey(key))
        {
            InsightDialog.Tell(
                owner,
                "VirusTotal key",
                "The key was not kept",
                "Windows could not protect it for your account, or it could not be written to the data folder. Nothing was saved; try again.",
                [],
                warning: true);
        }
    }

    [RelayCommand]
    private void RemoveKey() => _contacts.ClearVirusTotalKey();

    [RelayCommand]
    private static void GetKey() => InsightActions.OpenLink(KeyPage);

    private void OnChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(Read);

    private void Read()
    {
        _reading = true;
        SpamhausDrop = _contacts.SpamhausDrop;
        FeodoTracker = _contacts.FeodoTracker;
        _reading = false;

        IReadOnlyList<BlocklistStatus> lists = _blocklists.Status();
        SpamhausNote = Note(lists.First(each => each.Source == BlocklistSources.SpamhausDrop));
        FeodoNote = Note(lists.First(each => each.Source == BlocklistSources.FeodoTracker));

        HasKey = _contacts.HasVirusTotalKey;
        KeyNote = HasKey
            ? string.Format(CultureInfo.CurrentCulture, "Your key is kept for your Windows account only, never in an exported settings file. {0} of 500 lookups used today.", _reputation.UsedToday)
            : "Add your own free VirusTotal key to look a program up from its row. Only the program's hash is sent, and only when you ask.";
    }

    private static string Note(BlocklistStatus status) => status switch
    {
        { Wanted: false } => string.Concat("From ", status.Source.Publisher, ". ", status.Source.Licence, "."),
        { Problem: { } problem } => string.Concat("Could not fetch: ", problem),
        { FetchedAt: { } fetched } => string.Format(
            CultureInfo.CurrentCulture,
            "{0:N0} networks, fetched {1}. © {2}.",
            status.Networks,
            Windsock.Core.Formatting.ClockFormat.Current.Day(fetched.ToLocalTime()),
            status.Source.Publisher),
        _ => "Fetching…",
    };
}
