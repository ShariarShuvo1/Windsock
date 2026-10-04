using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.App.Services;
using Windsock.Core.Apps;
using Windsock.Core.Formatting;

namespace Windsock.App.ViewModels;

/// <summary>One app on the Apps page: what it used over the period, as Windows counted it.</summary>
public sealed partial class AppRowViewModel : ObservableObject
{
    private const string Unknown = "—";

    public AppRowViewModel(AppDescription app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Id = app.Id;
        App = app;
    }

    /// <summary>Windows' identifier, which is the row's identity.</summary>
    public string Id { get; }

    /// <summary>What the row names.</summary>
    public AppDescription App { get; }

    /// <summary>What to call it.</summary>
    public string Name => App.Name;

    /// <summary>What sort of thing it is, in words, for the column and for search.</summary>
    public string KindText => App.Kind switch
    {
        AppKind.Program => "Program",
        AppKind.Package => "App",
        AppKind.System => "Windows",
        _ => "Not attributed",
    };

    /// <summary>
    /// The program's file or the package's folder, or empty for Windows' own
    /// buckets.
    /// </summary>
    public string FilePath => App.Path ?? string.Empty;

    /// <summary>Whether there is a file or folder Explorer can be shown.</summary>
    /// <remarks>
    /// Not a device path no drive letter was found for, which Explorer cannot
    /// open, and not an installed package's folder: WindowsApps is closed to
    /// ordinary users, and Explorer's offer to "continue" rewrites its
    /// permissions, which is a known way to break the Store.
    /// </remarks>
    public bool CanReveal =>
        App.Path is { } path
        && (App.Kind is AppKind.Program or AppKind.Package)
        && FileReveal.IsOrdinaryPath(path)
        && !(App.Kind == AppKind.Package && FileReveal.IsInstalledPackage(path));

    /// <summary>
    /// Whether this row is traffic Windows kept for itself or could not put to
    /// an app, which reads quieter than an app's.
    /// </summary>
    public bool IsBucket => App.Kind is AppKind.System or AppKind.Unattributed;

    /// <summary>The picture the app is drawn with, once read.</summary>
    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    public long BytesReceived { get; private set; }

    public long BytesSent { get; private set; }

    public long BytesTotal => BytesReceived + BytesSent;

    /// <summary>This app's part of everything counted in the period, from 0 to 1.</summary>
    public double Share { get; private set; }

    /// <summary>Average in both directions over the last few minutes, or -1 when not known.</summary>
    public double RecentRate { get; private set; } = -1;

    public string DownloadText => SizeFormatter.Format(BytesReceived);

    public string UploadText => SizeFormatter.Format(BytesSent);

    public string TotalText => SizeFormatter.Format(BytesTotal);

    public string ShareText => Share <= 0
        ? "0%"
        : Share < 0.001
            ? "<0.1%"
            : Share.ToString(Share < 0.1 ? "P1" : "P0", CultureInfo.CurrentCulture);

    public string RecentText => RecentRate < 0
        ? Unknown
        : RateFormatter.Format(RecentRate, RateFamily.Bytes, RateScale.Auto);

    /// <summary>Whether anything came in, so the figure carries its colour.</summary>
    public bool HasReceive => BytesReceived > 0;

    /// <summary>Whether anything went out, so the figure carries its colour.</summary>
    public bool HasSend => BytesSent > 0;

    /// <summary>Applies the period's figures.</summary>
    public void Update(long received, long sent, double share, double recentRate)
    {
        bool moved = received != BytesReceived || sent != BytesSent;

        BytesReceived = received;
        BytesSent = sent;
        Share = share;
        RecentRate = recentRate;

        if (moved)
        {
            OnPropertyChanged(nameof(BytesReceived));
            OnPropertyChanged(nameof(BytesSent));
            OnPropertyChanged(nameof(BytesTotal));
            OnPropertyChanged(nameof(DownloadText));
            OnPropertyChanged(nameof(UploadText));
            OnPropertyChanged(nameof(TotalText));
            OnPropertyChanged(nameof(HasReceive));
            OnPropertyChanged(nameof(HasSend));
        }

        OnPropertyChanged(nameof(Share));
        OnPropertyChanged(nameof(ShareText));
        OnPropertyChanged(nameof(RecentRate));
        OnPropertyChanged(nameof(RecentText));
    }

    /// <summary>Whether the row matches a search term.</summary>
    public bool Contains(string term) =>
        Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || FilePath.Contains(term, StringComparison.OrdinalIgnoreCase)
        || KindText.Contains(term, StringComparison.OrdinalIgnoreCase);
}
