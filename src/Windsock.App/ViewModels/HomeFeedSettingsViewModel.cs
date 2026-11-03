using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.App.Services;
using Windsock.Core;
using Windsock.Core.Feed;
using Windsock.Core.Home;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>One size the overlay's figures can be drawn at.</summary>
public sealed record OverlaySizeOption(int Pixels, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The home network's switches on Settings › Data and privacy, and the local
/// feed, the stream overlay, the command line and the AI tools on Settings ›
/// Advanced.
/// </summary>
/// <remarks>
/// Every switch reads straight from the settings, so showing the page saves
/// nothing; changing one saves it and tells the part it drives. The feed's
/// token is shown hidden until asked for, and is only ever copied, never
/// written into a setting.
/// </remarks>
public sealed partial class HomeFeedSettingsViewModel : ObservableObject, IDisposable
{
    private const string Hidden = "••••••••••••••••••••••••";

    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _store;
    private readonly LocalFeed _feed;
    private readonly HomeDevices _devices;
    private readonly HouseholdTraffic _household;
    private readonly CommandLinePath _path;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public HomeFeedSettingsViewModel(
        WindsockSettings settings,
        ISettingsStore store,
        LocalFeed feed,
        HomeDevices devices,
        HouseholdTraffic household,
        CommandLinePath path,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(feed);

        _settings = settings;
        _store = store;
        _feed = feed;
        _devices = devices;
        _household = household;
        _path = path;
        _dispatcher = dispatcher;

        PortText = settings.Feed.PortInUse.ToString(CultureInfo.InvariantCulture);
        _feed.Changed += OnFeedChanged;
        ReadFeed();
        ReadPath();
    }

    // ------------------------------------------------------------ home network

    /// <summary>Whether devices on the home network are looked for.</summary>
    public bool FindDevices
    {
        get => _settings.Home.FindDevices;
        set
        {
            if (_settings.Home.FindDevices == value)
            {
                return;
            }

            _settings.Home.FindDevices = value;
            _store.Save(_settings);
            _devices.Refresh();
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the router may be asked over UPnP for its details and opened ports.</summary>
    public bool CheckRouter
    {
        get => _settings.Home.CheckRouter;
        set
        {
            if (_settings.Home.CheckRouter == value)
            {
                return;
            }

            _settings.Home.CheckRouter = value;
            _store.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the router's traffic counters are read while shown.</summary>
    public bool ReadRouterTraffic
    {
        get => _settings.Home.ReadRouterTraffic;
        set
        {
            if (_settings.Home.ReadRouterTraffic == value)
            {
                return;
            }

            _settings.Home.ReadRouterTraffic = value;
            _store.Save(_settings);
            _household.Refresh();
            OnPropertyChanged();
        }
    }

    // ------------------------------------------------------------- local feed

    /// <summary>Whether the local feed is on.</summary>
    public bool FeedEnabled
    {
        get => _settings.Feed.Enabled;
        set
        {
            if (_settings.Feed.Enabled == value)
            {
                return;
            }

            _settings.Feed.Enabled = value;
            _store.Save(_settings);
            _feed.Apply();
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the token may be given in an address, for OBS and browsers.</summary>
    public bool TokenInAddress
    {
        get => _settings.Feed.TokenInAddress;
        set
        {
            if (_settings.Feed.TokenInAddress == value)
            {
                return;
            }

            _settings.Feed.TokenInAddress = value;
            _store.Save(_settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(OverlayNote));
        }
    }

    /// <summary>The port, as typed.</summary>
    [ObservableProperty]
    public partial string PortText { get; set; } = string.Empty;

    /// <summary>Why the port typed cannot be used, or empty.</summary>
    [ObservableProperty]
    public partial string PortProblem { get; private set; } = string.Empty;

    /// <summary>Where the feed stands, in a sentence.</summary>
    [ObservableProperty]
    public partial string FeedState { get; private set; } = string.Empty;

    /// <summary>Whether the feed failed to start.</summary>
    [ObservableProperty]
    public partial bool FeedFailed { get; private set; }

    /// <summary>The feed's address, such as http://127.0.0.1:40406/.</summary>
    [ObservableProperty]
    public partial string Address { get; private set; } = string.Empty;

    /// <summary>The token, hidden until <see cref="TokenShown"/>.</summary>
    [ObservableProperty]
    public partial string TokenText { get; private set; } = Hidden;

    /// <summary>Whether the token is shown in full.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenButtonLabel))]
    public partial bool TokenShown { get; set; }

    /// <summary>Whether a token exists yet: the feed has been switched on once.</summary>
    [ObservableProperty]
    public partial bool HasToken { get; private set; }

    /// <summary>Whether the reset is being asked about, in place.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingReset { get; private set; }

    /// <summary>What the last copy or reset did, or why it could not.</summary>
    [ObservableProperty]
    public partial string FeedNotice { get; private set; } = string.Empty;

    /// <summary>The words on the show/hide button.</summary>
    public string TokenButtonLabel => TokenShown ? "Hide" : "Show";

    partial void OnTokenShownChanged(bool value) => ReadFeed();

    [RelayCommand]
    private void ApplyPort()
    {
        if (!int.TryParse(PortText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port < FeedSettings.LowestPort
            || port > 65535)
        {
            PortProblem = string.Create(CultureInfo.CurrentCulture, $"A port is a number from {FeedSettings.LowestPort} to 65535.");
            return;
        }

        PortProblem = string.Empty;

        if (_settings.Feed.Port == port)
        {
            return;
        }

        _settings.Feed.Port = port;
        _store.Save(_settings);
        _feed.Apply();
        ReadFeed();
    }

    [RelayCommand]
    private void ToggleToken() => TokenShown = !TokenShown;

    [RelayCommand]
    private void CopyAddress() => Copy(Address, "The feed's address is on the clipboard.");

    [RelayCommand]
    private void CopyToken()
    {
        if (_feed.Endpoint is { } endpoint)
        {
            Copy(endpoint.Token, "The token is on the clipboard. Anyone with it can read the feed.");
        }
    }

    [RelayCommand]
    private void AskReset() => IsConfirmingReset = true;

    [RelayCommand]
    private void CancelReset() => IsConfirmingReset = false;

    [RelayCommand]
    private void ResetToken()
    {
        IsConfirmingReset = false;
        _feed.ResetToken();
        ReadFeed();
        FeedNotice = "A new token is in use. Give it to every program that reads the feed.";
    }

    private void OnFeedChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(new Action(ReadFeed));

    private void ReadFeed()
    {
        if (_disposed)
        {
            return;
        }

        FeedStatus status = _feed.Status;
        FeedEndpoint? endpoint = _feed.Endpoint;
        int port = status.Port;

        Address = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/");
        HasToken = endpoint is not null;
        TokenText = endpoint is null ? "Made when the feed is first switched on" : TokenShown ? endpoint.Token : Hidden;
        FeedFailed = status.State == Core.Feed.FeedState.Failed;
        FeedState = status.State switch
        {
            Core.Feed.FeedState.Running => status.LiveClients switch
            {
                0 => "Running, on this PC only",
                1 => "Running, on this PC only · 1 program following it live",
                _ => string.Create(CultureInfo.CurrentCulture, $"Running, on this PC only · {status.LiveClients} programs following it live"),
            },
            Core.Feed.FeedState.Failed => status.Problem ?? "It could not start.",
            _ => "Off",
        };

        OnPropertyChanged(nameof(OverlayAddress));
    }

    // ---------------------------------------------------------------- overlay

    /// <summary>The overlay shows the download rate.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayAddress))]
    public partial bool OverlayDown { get; set; } = true;

    /// <summary>The overlay shows the upload rate.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayAddress))]
    public partial bool OverlayUp { get; set; } = true;

    /// <summary>The overlay shows the response time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayAddress))]
    public partial bool OverlayPing { get; set; } = true;

    /// <summary>The overlay shows today's total.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayAddress))]
    public partial bool OverlayToday { get; set; }

    /// <summary>The overlay shows the processor's load.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayAddress))]
    public partial bool OverlayProcessor { get; set; }

    /// <summary>The overlay counts in bits rather than bytes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayAddress))]
    public partial bool OverlayBits { get; set; }

    /// <summary>The overlay stacks its figures rather than lining them up.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayAddress))]
    public partial bool OverlayColumn { get; set; }

    /// <summary>The sizes on offer.</summary>
    public IReadOnlyList<OverlaySizeOption> OverlaySizes { get; } =
    [
        new(20, "Small (20 px)"),
        new(28, "Medium (28 px)"),
        new(40, "Large (40 px)"),
        new(56, "Huge (56 px)"),
    ];

    /// <summary>The size chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayAddress))]
    public partial int OverlaySize { get; set; } = 28;

    /// <summary>The address to give OBS's browser source; the token shows only while it is shown above.</summary>
    public string OverlayAddress => Overlay(_feed.Endpoint is { } endpoint && TokenShown ? endpoint.Token : "<token>");

    private string Overlay(string token)
    {
        List<string> shown = [];

        if (OverlayDown)
        {
            shown.Add("down");
        }

        if (OverlayUp)
        {
            shown.Add("up");
        }

        if (OverlayPing)
        {
            shown.Add("ping");
        }

        if (OverlayToday)
        {
            shown.Add("today");
        }

        if (OverlayProcessor)
        {
            shown.Add("cpu");
        }

        StringBuilder address = new(Address);
        address.Append("overlay?token=").Append(token);
        address.Append("&show=").Append(shown.Count == 0 ? "down" : string.Join(',', shown));
        address.Append(CultureInfo.InvariantCulture, $"&size={OverlaySize}");

        if (OverlayBits)
        {
            address.Append("&units=bits");
        }

        if (OverlayColumn)
        {
            address.Append("&layout=column");
        }

        return address.ToString();
    }

    /// <summary>What OBS needs that is not yet so, or a hint.</summary>
    public string OverlayNote => TokenInAddress
        ? "Add a Browser source in OBS with this address. Copy puts the real token in; the page is transparent, so set the source's size to fit."
        : "OBS cannot send a header, so it needs the token in the address: turn on Token in the address above.";

    [RelayCommand]
    private void CopyOverlay()
    {
        if (_feed.Endpoint is not { } endpoint)
        {
            FeedNotice = "Switch the feed on first; its token is made then.";
            return;
        }

        Copy(Overlay(endpoint.Token), "The overlay's address, with the token, is on the clipboard.");
    }

    // ------------------------------------------------------- AI tools, command line

    /// <summary>Whether this is a packaged Windsock, whose tools are reached by alias.</summary>
    public static bool IsPackaged => PackageIdentity.Exists;

    /// <summary>The AI tools server's command, as an assistant's settings name it.</summary>
    public static string McpCommand => IsPackaged ? "windsock-mcp" : Path.Combine(CommandLinePath.Folder, "Windsock.Mcp.exe");

    /// <summary>The block to paste into an MCP client's settings.</summary>
    public static string McpSnippet => string.Concat(
        "{\n  \"mcpServers\": {\n    \"windsock\": {\n      \"command\": \"",
        McpCommand.Replace("\\", "\\\\", StringComparison.Ordinal),
        "\"\n    }\n  }\n}");

    [RelayCommand]
    private void CopyMcp() => Copy(McpSnippet, "The settings block is on the clipboard. Paste it into your assistant's MCP settings.");

    /// <summary>Whether the PATH button is offered: an unpackaged copy with the command line beside it.</summary>
    public static bool PathOffered => CommandLinePath.IsOffered;

    /// <summary>The folder the command line is in.</summary>
    public static string CommandFolder => CommandLinePath.Folder;

    /// <summary>Whether the folder is on the user's PATH.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddToPath), nameof(CanRemoveFromPath))]
    public partial bool IsOnPath { get; private set; }

    /// <summary>Whether adding it is being asked about, in place.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddToPath))]
    public partial bool IsConfirmingPath { get; private set; }

    /// <summary>Whether the Add to PATH button is shown: offered, not there yet, not being asked about.</summary>
    public bool CanAddToPath => PathOffered && !IsOnPath && !IsConfirmingPath;

    /// <summary>Whether the Remove from PATH button is shown.</summary>
    public bool CanRemoveFromPath => PathOffered && IsOnPath;

    /// <summary>What the command line row says.</summary>
    [ObservableProperty]
    public partial string PathNote { get; private set; } = string.Empty;

    [RelayCommand]
    private void AskAddToPath() => IsConfirmingPath = true;

    [RelayCommand]
    private void CancelPath() => IsConfirmingPath = false;

    [RelayCommand]
    private void AddToPath()
    {
        IsConfirmingPath = false;
        string? problem = _path.Add();
        ReadPath(problem);
    }

    [RelayCommand]
    private void RemoveFromPath()
    {
        string? problem = _path.Remove();
        ReadPath(problem);
    }

    private void ReadPath(string? problem = null)
    {
        if (IsPackaged)
        {
            PathNote = "Type windsock in any console: the Store installs the command for you.";
            return;
        }

        if (!PathOffered)
        {
            PathNote = "The command line is not beside this copy of Windsock.";
            return;
        }

        IsOnPath = _path.IsOnPath();
        PathNote = problem ?? (IsOnPath
            ? "On your PATH: windsock works in consoles opened from now on."
            : "Not on your PATH: run it as " + Path.Combine(CommandFolder, "windsock") + ", or add the folder.");
    }

    private void Copy(string text, string done)
    {
        try
        {
            Clipboard.SetText(text);
            FeedNotice = done;
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            FeedNotice = "The clipboard was busy. Try again.";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _feed.Changed -= OnFeedChanged;
    }
}
