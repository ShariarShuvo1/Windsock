using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windsock.App.Views;
#if !STORE
using Windsock.Core.Hardware;
#endif
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Settings;
using Windsock.Core.Startup;

namespace Windsock.App.ViewModels;

/// <summary>
/// Settings' pages, the privacy page's outside contacts, the Advanced page and
/// moving the settings to another PC.
/// </summary>
public sealed partial class SettingsPanelViewModel
{
    private const string PawnIoSite = "https://pawnio.eu";

    private const string PrivacyPolicy = "https://github.com/ShariarShuvo1/Windsock/blob/main/PRIVACY.md";

#if STORE
    private const string AdvancedSubtitle = "Moving your settings to another PC";
#else
    private const string AdvancedSubtitle = "Administrator mode, the processor temperature, and the settings file";
#endif

    private bool _readingContacts;

    /// <summary>The pages, in the order their tabs run.</summary>
    public IReadOnlyList<SettingsPageOption> Pages { get; }

    /// <summary>The page on screen. General when Settings opens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageSubtitle))]
    public partial SettingsPage SelectedPage { get; set; } = SettingsPage.General;

    /// <summary>The line under the title: what the page on screen holds.</summary>
    public string PageSubtitle => Pages.First(page => page.Page == SelectedPage).Subtitle;

    /// <summary>Why the update check cannot be switched on, when this copy cannot update itself.</summary>
    public string UpdateCheckNote => CanUpdate ? string.Empty : NoUpdateReason;

    // ------------------------------------------------------- data and privacy

    /// <summary>Whether the route to each place on the map is traced. The map's own Routes switch.</summary>
    [ObservableProperty]
    public partial bool TraceRoutes { get; set; }

    /// <summary>Whether addresses the DNS cache cannot name are looked up in reverse.</summary>
    [ObservableProperty]
    public partial bool ReverseLookups { get; set; }

    /// <summary>
    /// Whether the connection checks ping an address on the internet as well as
    /// the router. The same setting as Connection › Status's "How to check".
    /// </summary>
    [ObservableProperty]
    public partial bool PingsInternet { get; set; }

    /// <summary>Which address the connection checks ping, or that they ping none.</summary>
    [ObservableProperty]
    public partial string InternetCheckNote { get; private set; } = string.Empty;

    partial void OnTraceRoutesChanged(bool value)
    {
        if (!_readingContacts)
        {
            _contacts.TraceRoutes = value;
        }
    }

    partial void OnReverseLookupsChanged(bool value)
    {
        if (!_readingContacts)
        {
            _contacts.ReverseLookups = value;
        }
    }

    partial void OnPingsInternetChanged(bool value)
    {
        if (_readingContacts)
        {
            return;
        }

        ConnectionSettings choices = _settings.Connection;

        // Turned on here, the check pings Cloudflare's resolver, or the address
        // last typed if there is one; which address is chosen on the Status page.
        InternetCheck wanted = !value
            ? InternetCheck.WindowsOnly
            : ConnectionSettings.IsTarget(choices.CustomTarget)
                ? InternetCheck.Custom
                : InternetCheck.Cloudflare;

        if (choices.InternetCheck == wanted)
        {
            return;
        }

        choices.InternetCheck = wanted;
        _store.Save(_settings);

        // A different target draws a different line; the old one would be read as this one.
        _connection.History.Clear();
        _connection.Nudge();

        ReadContacts();
    }

    private void OnContactsChanged(object? sender, EventArgs e) => ReadContacts();

    /// <summary>Shows every outside contact as it stands, without saving anything back.</summary>
    private void ReadContacts()
    {
        _readingContacts = true;

        TraceRoutes = _contacts.TraceRoutes;
        ReverseLookups = _contacts.ReverseLookups;

        ConnectionSettings choices = _settings.Connection;
        string? target = choices.InternetTarget;

        PingsInternet = target is not null;
        InternetCheckNote = target is null
            ? "Off: Windsock asks Windows, and pings your router, which never leaves your network."
            : string.Concat("On: each check also pings ", target, ". Choose which address under Connection › Status.");

        _readingContacts = false;
    }

    [RelayCommand]
    private static void OpenPrivacyPolicy() => OpenLink(PrivacyPolicy);

    // ---------------------------------------------------------------- advanced

    /// <summary>Whether this Windsock is running with administrator rights.</summary>
    [ObservableProperty]
    public partial bool IsElevated { get; private set; }

    /// <summary>What administrator mode is doing, or would do, in a sentence.</summary>
    [ObservableProperty]
    public partial string AdministratorNote { get; private set; } = string.Empty;

    /// <summary>Whether the processor temperature is being read, in a few words.</summary>
    [ObservableProperty]
    public partial string TemperatureState { get; private set; } = string.Empty;

    /// <summary>Why, at more length.</summary>
    [ObservableProperty]
    public partial string TemperatureNote { get; private set; } = string.Empty;

    private void ReadAdvanced()
    {
#if STORE
        IsElevated = false;
        AdministratorNote = string.Empty;
        TemperatureState = string.Empty;
        TemperatureNote = string.Empty;
#else
        IsElevated = Elevation.IsElevated();
        AdministratorNote = IsElevated
            ? "Windsock is running as administrator for this session: each app and each process is measured every second, and the processor temperature can be read. Start it normally to run without."
            : "Measuring each app and each process every second, the Apps page's process view, each app's minutes in History and the processor temperature need administrator rights. Windows asks first, and it lasts until Windsock closes.";

        (PawnIoState state, string? detail, string? explanation) = _system.CpuTemperature;

        TemperatureState = state switch
        {
            PawnIoState.Running => "Reading the processor temperature through PawnIO",
            PawnIoState.Stopped => "Not read yet",
            _ => detail ?? "Not available",
        };

        TemperatureNote = state switch
        {
            PawnIoState.Running => "PawnIO is a signed driver you installed separately. Windsock ships no driver and installs none.",
            PawnIoState.Stopped => "The processor temperature is read while the Processor page or the taskbar meter asks for it.",
            _ => explanation ?? string.Empty,
        };
#endif
    }

#if !STORE
    /// <summary>
    /// Closes Windsock and starts it again with administrator rights, after
    /// Windows asks for consent. Nothing changes if the consent is refused.
    /// </summary>
    [RelayCommand]
    private static void RestartElevated() => Restart(elevated: true);
#endif

    [RelayCommand]
    private static void OpenPawnIo() => OpenLink(PawnIoSite);

    // ------------------------------------------------------ the settings file

    /// <summary>What the last export or import did, or why it did not.</summary>
    [ObservableProperty]
    public partial string TransferNote { get; private set; } = string.Empty;

    /// <summary>Writes every setting but the adapters to one file.</summary>
    [RelayCommand]
    private async Task ExportSettingsAsync()
    {
        SaveFileDialog dialog = new()
        {
            Title = "Export Windsock's settings",
            Filter = "Windsock settings (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = string.Format(CultureInfo.InvariantCulture, "Windsock-settings-{0:yyyy-MM-dd}.json", DateTimeOffset.Now),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // A data plan's networks are written with their kind, which history knows.
        IReadOnlyCollection<NetworkTag> seen = await Task.Run(SeenNetworks).ConfigureAwait(true);
        string text = SettingsTransfer.Export(_settings, DateTimeOffset.Now, seen);
        string path = dialog.FileName;

        try
        {
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false)).ConfigureAwait(true);
            TransferNote = string.Concat("Exported to ", Path.GetFileName(path), ". History is not in it; it stays on this PC.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TransferNote = "Could not write that file: " + error.Message;
        }
    }

    /// <summary>
    /// Reads a settings file, shows what it would change, and on a yes takes
    /// it and restarts so every part of Windsock starts from it. A file that
    /// is not one, or holds something Windsock cannot use, changes nothing.
    /// </summary>
    [RelayCommand]
    private async Task ImportSettingsAsync()
    {
        OpenFileDialog dialog = new()
        {
            Title = "Import Windsock's settings",
            Filter = "Windsock settings (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string text;

        try
        {
            FileInfo file = new(dialog.FileName);

            if (file.Length > SettingsTransfer.LargestFile)
            {
                TransferNote = "That file is far too large to be Windsock's settings. Nothing was changed.";
                return;
            }

            text = await File.ReadAllTextAsync(file.FullName).ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TransferNote = "Could not read that file: " + error.Message;
            return;
        }

        // Read away from the window, against a copy of the settings taken here,
        // so a file at the size limit never holds the window up and nothing
        // the window changes meanwhile is read half-changed.
        WindsockSettings here = SettingsTransfer.Snapshot(_settings);
        SettingsImport read;

        try
        {
            read = await Task.Run(() => SettingsTransfer.Read(text, here, SeenNetworks(), MeterOffers)).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Read turns every failure it knows into a refusal; anything else is still only a file that cannot be used, never a reason to close Windsock.
        catch (Exception error)
#pragma warning restore CA1031
        {
            LogImportFailed(error);
            TransferNote = "That file could not be read. Nothing was changed.";
            return;
        }

        if (!read.IsValid)
        {
            TransferNote = string.Concat(read.Problem, " Nothing was changed.");
            return;
        }

        if (read.Changes.Count == 0)
        {
            TransferNote = string.Join(" ", ["Those settings are the same as yours. Nothing was changed.", .. read.Notes]);
            return;
        }

        if (!ImportSettingsDialog.Ask(read, Application.Current?.MainWindow))
        {
            return;
        }

        try
        {
            _settings.CopyFrom(read.Settings!);
        }
#pragma warning disable CA1031 // CopyFrom makes its copy before it assigns anything, so a failure leaves the settings as they were.
        catch (Exception error)
#pragma warning restore CA1031
        {
            LogImportFailed(error);
            TransferNote = "Those settings could not be put in place. Nothing was changed.";
            return;
        }

        _store.Save(_settings);

        if (!Restart(elevated: false))
        {
            TransferNote = "Imported. Restart Windsock to finish putting them in place.";
        }
    }

    /// <summary>
    /// The networks this PC's history knows, so an imported data plan can find
    /// its networks here by name. None when history cannot be read: the plan's
    /// networks are then found only among those the settings already name.
    /// </summary>
    private IReadOnlyCollection<NetworkTag> SeenNetworks()
    {
        // The history store is also the reader of its networks; asked for
        // directly here rather than given to every opening of Settings.
        if (_history is not IUsageAggregates aggregates)
        {
            return [];
        }

        try
        {
            return [.. aggregates.ReadNetworkTags().Values];
        }
        catch (Exception error) when (HistoryFailure.IsStorage(error))
        {
            return [];
        }
    }

    /// <summary>
    /// Whether this PC's meter can show a reading, as loading a layout file on
    /// the Taskbar page asks: the edition offers it, and this machine has what
    /// it reads. Before the machine is identified its hardware is not judged.
    /// </summary>
    private bool MeterOffers(MeterField field)
    {
        if (!MeterSlotChoiceViewModel.IsOffered(field))
        {
            return false;
        }

        if (!_system.IsIdentified)
        {
            return true;
        }

        return field switch
        {
            MeterField.BatteryCharge or MeterField.BatteryTime => _system.Machine.HasBattery,
            MeterField.NpuLoad => _system.Machine.Npus.Count > 0,
            MeterField.ProcessorPower => _system.ReadsProcessorPower,
            _ => true,
        };
    }

    /// <summary>
    /// Starts another Windsock to take over from this one, then closes this one.
    /// The new one waits for this one to go; see <see cref="SingleInstance"/>.
    /// </summary>
    private static bool Restart(bool elevated)
    {
        string? path = Environment.ProcessPath;

        if (path is null)
        {
            return false;
        }

        try
        {
            ProcessStartInfo start = new(path)
            {
                UseShellExecute = true,
                Arguments = SingleInstance.ReplaceSwitch,
            };

            if (elevated)
            {
                start.Verb = "runas";
            }

            Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Consent refused, or nothing to start: this one carries on.
            return false;
        }

        Application.Current?.Shutdown();
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not import a settings file")]
    private partial void LogImportFailed(Exception exception);
}
